package nativeaccept

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// CachedStartEnv makes StartDebugGame load a saved copy of the debug quick
// start instead of generating a world and map every time (issue #91: a
// load is ~2.7s where a warm quick start is ~5.4s). The first start under a
// given map size, planet coverage and expansion set generates as before,
// saves the result as RimGovernor-debug-<size>-<coverage>[-<expansions>]
// through lifecycle_save, and copies it into profile/Saves so every later
// Prepare carries it; later starts load it (rimworld/load_game_ready). The
// quiet storyteller is applied after either path, as before. Delete the
// save to regenerate; a harness that must see a never-before-seen world
// (world generation itself under test) runs without this.
const CachedStartEnv = "RIMGOVERNOR_ACCEPT_CACHED_START"

// startCache is what PrepareConfig knows and StartDebugGame needs to find
// and persist the cached start: one Config per harness process, so a
// package variable rather than a field on every Harness.
var startCache struct {
	root       string
	headless   bool
	expansions []string
}

// CachedStart reports whether CachedStartEnv asks for the saved start.
func CachedStart() bool {
	switch os.Getenv(CachedStartEnv) {
	case "", "0", "false", "no":
		return false
	}
	return true
}

func cachedStartName(start DebugStart) string {
	name := fmt.Sprintf("RimGovernor-debug-%d-%g", start.MapSize, start.PlanetCoverage)
	if len(startCache.expansions) > 0 {
		name += "-" + strings.ToLower(strings.Join(startCache.expansions, "-"))
	}
	return strings.ReplaceAll(name, ".", "_")
}

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

// loadCachedStart loads the save and waits for it the way a quick start is
// waited for.
func loadCachedStart(ctx context.Context, h *Harness, name string) error {
	_, err := h.Call(ctx, "cached-start-load", "rimworld/load_game_ready", map[string]any{
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
