// Package variantgen holds the scenario-start save-generation mechanics
// behind variantsavegen and sustainedmatrixaccept's own -manifest mode
// (issue #1's sustained matrix): drive RimWorld's programmatic scenario
// start -- scripts/fixtures/ScenarioStartFixture.cs's test/configure_start,
// wired into Root_Play.SetupForQuickTestPlay via a Harmony prefix -- then
// save the result under the requested name with rimgovernor/lifecycle_save,
// the same trusted native save path checkpointaccept exercises.
//
// It exists as its own package (rather than living only in
// cmd/variantsavegen) so sustainedmatrixaccept can generate any variant save
// missing from a manifest before running its watch window against it,
// without a caller ever needing to invoke variantsavegen as a separate step
// or hand-check in a .rws fixture: the checked-in artifact is the JSON
// Variant spec, not a multi-megabyte binary save.
package variantgen

import (
	"context"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Variant is one scenario-start spec, matching test/configure_start's
// parameters one-to-one (see ScenarioStartFixture.Configure).
type Variant struct {
	Save             string  `json:"save"`
	Scenario         string  `json:"scenario"`
	Count            int     `json:"count"`
	Seed             string  `json:"seed"`
	Biome            string  `json:"biome,omitempty"`
	Difficulty       string  `json:"difficulty,omitempty"`
	MinTemperature   float64 `json:"minTemperature"`
	MaxTemperature   float64 `json:"maxTemperature"`
	WorldTemperature string  `json:"worldTemperature,omitempty"`
	// MapSize and PlanetCoverage default to the small start (issue #91:
	// na.DefaultMapSize, na.DefaultPlanetCoverage); a variant that picks a
	// biome or a temperature band gets ConstrainedPlanetCoverage instead,
	// since a 5% planet has no guaranteed tundra or extreme-desert tile. A
	// variant whose stressor is map- or world-level sets them explicitly.
	MapSize        int     `json:"mapSize,omitempty"`
	PlanetCoverage float64 `json:"planetCoverage,omitempty"`
}

// ConstrainedPlanetCoverage is the planet a biome- or temperature-constrained
// variant defaults to: large enough to hold every settleable biome.
const ConstrainedPlanetCoverage = 0.3

// WithDefaults fills in test/configure_start's own defaults for any field a
// hand-written manifest entry left zero, so a minimal spec ({"save": ...,
// "scenario": "Tribal", "count": 8, "seed": "..."}) is enough.
func (v Variant) WithDefaults() Variant {
	if v.Difficulty == "" {
		v.Difficulty = "Rough"
	}
	if v.WorldTemperature == "" {
		v.WorldTemperature = "Normal"
	}
	if v.MinTemperature == 0 && v.MaxTemperature == 0 {
		v.MinTemperature, v.MaxTemperature = -100, 100
	}
	if v.MapSize == 0 {
		v.MapSize = na.DefaultMapSize
	}
	if v.PlanetCoverage == 0 {
		v.PlanetCoverage = na.DefaultPlanetCoverage
		if v.Biome != "" || v.MinTemperature != -100 || v.MaxTemperature != 100 {
			v.PlanetCoverage = ConstrainedPlanetCoverage
		}
	}
	return v
}

// Validate checks the fields test/configure_start itself would otherwise
// reject, so a manifest typo fails before spending a native session on it.
func (v Variant) Validate() error {
	if v.Save == "" || v.Scenario == "" || v.Seed == "" {
		return fmt.Errorf("variant missing save/scenario/seed: %#v", v)
	}
	if v.Count < 1 || v.Count > 10 {
		return fmt.Errorf("variant %q count must be 1..10, got %d", v.Save, v.Count)
	}
	if err := (na.DebugStart{MapSize: v.MapSize, PlanetCoverage: v.PlanetCoverage}).Validate(); err != nil {
		return fmt.Errorf("variant %q: %w", v.Save, err)
	}
	return nil
}

// SavePath is where a variant's generated save durably lives: profile/Saves
// under the disposable worker root, exactly where docs/players/setup.md says
// to stage the hand-prepared tribal8 baseline. PrepareRendered points
// RimWorld straight at this directory; Prepare (headless) mirrors every
// .rws here into its own disposable headless-profile copy on each run.
func SavePath(root, save string) string {
	return filepath.Join(root, "profile", "Saves", save+".rws")
}

// Exists reports whether a variant's save has already been generated.
func Exists(root, save string) bool {
	info, err := os.Stat(SavePath(root, save))
	return err == nil && !info.IsDir()
}

// openFixtureSession opens a session at the main menu (na.OpenGame: a fresh
// process, or with RIMGOVERNOR_ACCEPT_KEEP_GAME an attach to the process an
// earlier harness left running, unloaded) and validates that the
// ScenarioStartFixture endpoints this package depends on are actually
// present, so a plain production-mod misconfiguration fails immediately with
// a clear cause instead of a confusing later error. The returned close
// records what became of the process under row (na.Game.Close).
func openFixtureSession(ctx context.Context, root, output, gameID string, headless bool, row map[string]any) (*na.Config, *na.Harness, func(), error) {
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return nil, nil, nil, fmt.Errorf("prepare profile: %w", err)
	}
	held, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return nil, nil, nil, err
	}
	h := na.NewHarness(held.Client, output)
	names, err := h.Discovery(ctx)
	if err != nil {
		held.Close(nil)
		return nil, nil, nil, err
	}
	for _, want := range []string{"test/configure_start", "test/list_start_scenarios"} {
		if !na.Contains(names, want) {
			held.Close(nil)
			return nil, nil, nil, fmt.Errorf("missing %s in discovery -- install a ScenarioStartFixture-built mod "+
				"(scripts/build_native_mod.ps1 -Fixture ScenarioStartFixture ...), not the production mod", want)
		}
	}
	return cfg, h, func() { held.Close(na.Report(row)) }, nil
}

// ListScenarios reads test/list_start_scenarios once, for discovering valid
// -scenario/-difficulty names before writing a manifest.
func ListScenarios(ctx context.Context, root, output, gameID string, headless bool) (map[string]any, error) {
	_, h, closeFn, err := openFixtureSession(ctx, root, output, gameID, headless, nil)
	if err != nil {
		return nil, err
	}
	defer closeFn()
	return h.Call(ctx, "list-scenarios", "test/list_start_scenarios", map[string]any{})
}

// Generate runs one variant end to end: arm the scenario, start it, dismiss
// the naming dialog, save under v.Save, and persist that save to SavePath so
// it survives past this disposable session's own profile copy. row records
// evidence for the caller's own report, mirroring every other native
// acceptance binary's convention. It needs the main menu --
// test/configure_start refuses with a game loaded -- which na.OpenGame
// guarantees for a fresh process and a kept one alike; callers generating
// several variants must do so sequentially (see sustainedmatrixaccept's
// saves loop for why: one native session can be connected through the
// sole GABP slot at a time).
func Generate(ctx context.Context, root, output, gameID string, headless bool, startTimeout time.Duration, v Variant, row map[string]any) error {
	if err := v.Validate(); err != nil {
		return err
	}
	_, h, closeFn, err := openFixtureSession(ctx, root, output, gameID, headless, row)
	if err != nil {
		return err
	}
	defer closeFn()

	configured, err := h.Call(ctx, "configure-start", "test/configure_start", map[string]any{
		"scenario": v.Scenario, "count": v.Count, "seed": v.Seed, "biome": v.Biome,
		"difficulty": v.Difficulty, "minTemperature": v.MinTemperature, "maxTemperature": v.MaxTemperature,
		"worldTemperature": v.WorldTemperature, "mapSize": v.MapSize, "planetCoverage": v.PlanetCoverage,
	})
	if err != nil {
		return fmt.Errorf("configure-start: %w", err)
	}
	if success, _ := na.AsBool(configured["success"]); !success {
		return fmt.Errorf("configure-start refused: %#v", configured)
	}
	row["configured"] = configured

	if _, err := h.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
		"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": startTimeout.Milliseconds(),
	}); err != nil {
		return fmt.Errorf("start_debug_game_ready: %w", err)
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}

	// A fresh scenario start can leave the initial faction/settlement naming
	// dialog open, same as sustainedfoodaccept's baseline load; dismiss it so
	// the saved colony starts in the same clean state as the hand-prepared
	// baseline.
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return err
	}
	if naming, ok := na.AsMap(facts["colonyNaming"]); ok && naming != nil {
		confirmed, err := h.Call(ctx, "confirm-colony-names", "home/confirm_colony_names", map[string]any{
			"windowId":       int(na.AsNumber(naming["windowId"])),
			"factionName":    na.AsString(naming["factionName"]),
			"settlementName": na.AsString(naming["settlementName"]),
			"dryRun":         false,
		})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(confirmed["success"]); !success {
			return fmt.Errorf("confirm_colony_names refused: %#v", confirmed)
		}
		row["confirmed_colony_names"] = confirmed
	}

	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(loaded["paused"]); !paused {
		return fmt.Errorf("generated colony did not end up paused before save")
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])
	tick := na.AsNumber(loadedContext["tick"])

	requestID := fmt.Sprintf("variantgen-%s-%d", sanitize(v.Save), time.Now().UnixNano())
	saveReply, err := h.Wire(ctx, "save", "lifecycle_save", map[string]any{
		"player":       map[string]any{"identity": identity, "playerDirection": 1, "requestId": requestID},
		"saveName":     v.Save,
		"expectedTick": tick,
	})
	if err != nil {
		return fmt.Errorf("lifecycle_save: %w", err)
	}
	_, completed, err := na.Outcome(saveReply, "completed")
	if err != nil {
		return fmt.Errorf("lifecycle_save: expected a completed save: %w", err)
	}
	if na.AsString(completed["saveName"]) != v.Save {
		return fmt.Errorf("lifecycle_save: completed save name mismatch: %#v", completed)
	}
	completedContext, _ := na.AsMap(completed["context"])
	if na.AsNumber(completedContext["tick"]) != tick {
		return fmt.Errorf("lifecycle_save: completed tick does not match expected: %#v", completed)
	}
	row["saved"] = completed

	if err := persistSave(root, headless, v.Save); err != nil {
		return fmt.Errorf("persist generated save to profile/Saves: %w", err)
	}
	return nil
}

// persistSave copies the save the running process just wrote from its own
// (possibly disposable) profile directory into profile/Saves, the durable
// location every other tool's Prepare/PrepareRendered reads from. In
// rendered mode the running profile already is profile/Saves (no copy
// needed); in headless mode it wrote into headless-profile/Saves, a fresh
// per-run mirror that Prepare() overwrites from profile/Saves on every
// subsequent run -- so without this copy, a headless-generated save would be
// silently lost the next time anything calls Prepare().
func persistSave(root string, headless bool, save string) error {
	dst := SavePath(root, save)
	var runningProfile string
	if headless {
		runningProfile = "headless-profile"
	} else {
		runningProfile = "profile"
	}
	src := filepath.Join(root, runningProfile, "Saves", save+".rws")
	if src == dst {
		return nil
	}
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	if err := os.MkdirAll(filepath.Dir(dst), 0755); err != nil {
		return err
	}
	out, err := os.OpenFile(dst, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0644)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

func sanitize(name string) string {
	var b []byte
	for _, r := range name {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '-', r == '_':
			b = append(b, byte(r))
		default:
			b = append(b, '-')
		}
	}
	return string(b)
}
