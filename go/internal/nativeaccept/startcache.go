package nativeaccept

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// CachedStartEnv controls whether StartDebugGame loads a saved copy of the
// debug start instead of generating a world and map every time (
// a load is ~2.7s where a generation is far longer). It is on by default. The
// first start under a given map size, planet coverage and expansion set
// generates through the new-colony op, which saves the result as
// RimGovernor-debug-<size>-<coverage>[-<expansions>][-<biomes>][-flat]
// [-seed-<seed>]-quiet|loud (the storyteller is part of the save); it is
// copied into profile/Saves so every later Prepare carries it, and later
// starts load it (rimgovernor/load_game_ready). The fixture's quiet op is
// applied after either path where the mode asks. Delete the save to
// regenerate; a harness that must see a never-before-seen world (world
// generation itself under test) runs with RIMGOVERNOR_ACCEPT_CACHED_START=0.
// A start with a pinned seed caches as its own
// RimGovernor-debug-...-seed-<seed> save, so it never takes the plain
// roll's world and a repeat under the seed loads instead of generating.
const CachedStartEnv = "RIMGOVERNOR_ACCEPT_CACHED_START"

// startCache is what PrepareConfig knows and StartDebugGame needs to find
// and persist the cached start: one Config per harness process, so a
// package variable rather than a field on every Harness.
var startCache struct {
	root       string
	headless   bool
	expansions []string
	// seed is the seed the last StartDebugGameSized generated with, "" when
	// it loaded the cached save.
	seed string
	// save is the cached save the last StartDebugGameSized loaded, "" when
	// it generated.
	save string
}

// CachedStart reports whether the saved start is used: true unless
// CachedStartEnv opts out.
func CachedStart() bool { return !envOptsOut(CachedStartEnv) }

// cachedStartName names the save a start caches as. The storyteller is baked
// into the save by the op, so a quiet (RimGovernorQuiet) and a loud (ordinary)
// start never share one.
func cachedStartName(start DebugStart, quiet bool) string {
	name := fmt.Sprintf("RimGovernor-debug-%d-%g", start.MapSize, start.PlanetCoverage)
	if len(startCache.expansions) > 0 {
		name += "-" + strings.ToLower(strings.Join(startCache.expansions, "-"))
	}
	// A biome preference is part of what the save satisfies: a start pinned
	// to a food-bearing biome must not load a plain roll's save.
	if start.Biomes != "" {
		name += "-" + strings.ToLower(strings.ReplaceAll(strings.ReplaceAll(start.Biomes, " ", ""), ",", "-"))
	}
	if start.Flat {
		name += "-flat"
	}
	if start.Seed != "" {
		name += "-seed-" + seedToken(start.Seed)
	}
	if quiet {
		name += "-quiet"
	} else {
		name += "-loud"
	}
	name = strings.ReplaceAll(name, ".", "_")
	if len(name) > maxSaveName {
		// The op's save names are at most 64 characters: keep a readable
		// prefix and make the whole name's hash the tail.
		sum := sha256.Sum256([]byte(name))
		name = name[:maxSaveName-9] + "-" + hex.EncodeToString(sum[:4])
	}
	return name
}

// maxSaveName is the longest save name lifecycle_new_colony accepts.
const maxSaveName = 64

// cachedStartPath is where the running process's profile would hold the
// save, and whether it is there.
func cachedStartPath(name string) (string, bool) {
	profile := "profile"
	if startCache.headless {
		profile = "headless-profile"
	}
	path := filepath.Join(startCache.root, profile, "Saves", name+".rws")
	_, err := os.Stat(path)
	return path, err == nil
}

// cachedStartStale checks whether the cached save requires different expansions from the
// active profile. A mismatch prevents loading (save.missing_mods) and requires regeneration.
func cachedStartStale(path, name string) (bool, error) {
	recorded, err := saveExpansionsAt(path, name)
	if err != nil {
		return false, fmt.Errorf("cached start %s: %w", name, err)
	}
	var wanted []string
	for _, name := range startCache.expansions {
		id, err := ExpansionPackage(name)
		if err != nil {
			return false, err
		}
		wanted = append(wanted, id)
	}
	sort.Strings(wanted)
	sort.Strings(recorded)
	return strings.Join(wanted, ",") != strings.Join(recorded, ","), nil
}

// loadCachedStart loads the save and waits for it the way a quick start is
// waited for.
func loadCachedStart(ctx context.Context, h *Harness, name string) error {
	_, err := h.Call(ctx, "cached-start-load", "rimgovernor/load_game_ready", map[string]any{
		"saveName": name, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
	})
	if err != nil {
		return fmt.Errorf("cached start %s: %w", name, err)
	}
	return nil
}

// saveCachedStart writes the just-started game as name and copies it into
// profile/Saves, the durable location Prepare mirrors from.
func saveCachedStart(ctx context.Context, h *Harness, name string) error {
	identityReply, err := h.Wire(ctx, "cached-start-identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := AsMap(loaded["context"])
	identity, _ := AsMap(loadedContext["identity"])
	tick := AsNumber(loadedContext["tick"])
	saveReply, err := h.Wire(ctx, "cached-start-save", "lifecycle_save", map[string]any{
		"player":       map[string]any{"identity": identity, "playerDirection": 1, "requestId": fmt.Sprintf("cached-start-%d", time.Now().UnixNano())},
		"saveName":     name,
		"expectedTick": tick,
	})
	if err != nil {
		return fmt.Errorf("lifecycle_save: %w", err)
	}
	if _, _, err := Outcome(saveReply, "completed"); err != nil {
		return fmt.Errorf("lifecycle_save: %w", err)
	}
	src, ok := cachedStartPath(name)
	if !ok {
		return fmt.Errorf("lifecycle_save completed but %s is not there", src)
	}
	dst := filepath.Join(startCache.root, "profile", "Saves", name+".rws")
	if src == dst {
		return nil
	}
	return copyFile(src, dst)
}
