// Package variantgen holds the scenario-start save-generation mechanics
// behind the tools/variantsavegen and sustained/matrix cases
// (issue #1's sustained matrix): drive RimWorld's programmatic scenario
// start -- scripts/fixtures/ScenarioStartFixture.cs's test/configure_start,
// wired into Root_Play.SetupForQuickTestPlay via a Harmony prefix -- then
// save the result under the requested name with rimgovernor/lifecycle_save,
// the same trusted native save path checkpointaccept exercises.
//
// It exists as its own package so the sustained/matrix cases can generate
// any variant save missing from a manifest before running their watch
// window against it, without a caller ever needing to run
// tools/variantsavegen as a separate step or hand-check in a .rws fixture:
// the checked-in artifact is the JSON Variant spec, not a multi-megabyte
// binary save.
package variantgen

import (
	"bytes"
	"context"
	"encoding/json"
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

// Start is the programmatic scenario start that generates v.
func (v Variant) Start() na.ScenarioStart {
	v = v.WithDefaults()
	return na.ScenarioStart{
		Scenario: v.Scenario, Count: v.Count, Seed: v.Seed, Biome: v.Biome, Difficulty: v.Difficulty,
		MinTemperature: v.MinTemperature, MaxTemperature: v.MaxTemperature, WorldTemperature: v.WorldTemperature,
		Size: na.DebugStart{MapSize: v.MapSize, PlanetCoverage: v.PlanetCoverage},
	}
}

// Manifest decodes a JSON array of variant specs, applies the defaults and
// validates every entry (unknown fields, out-of-range counts, an empty seed
// and a duplicate save name are errors).
func Manifest(data []byte) ([]Variant, error) {
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.DisallowUnknownFields()
	var variants []Variant
	if err := dec.Decode(&variants); err != nil {
		return nil, fmt.Errorf("manifest must be a JSON array of variant specs: %w", err)
	}
	if len(variants) == 0 {
		return nil, fmt.Errorf("manifest names no variants")
	}
	seen := map[string]bool{}
	for i := range variants {
		variants[i] = variants[i].WithDefaults()
		if err := variants[i].Validate(); err != nil {
			return nil, err
		}
		if seen[variants[i].Save] {
			return nil, fmt.Errorf("duplicate save name %q", variants[i].Save)
		}
		seen[variants[i].Save] = true
	}
	return variants, nil
}

// Sanitize maps a save name onto the characters a case name and a
// directory admit.
func Sanitize(name string) string { return sanitize(name) }

// SaveVariant writes the loaded, paused game as save through
// lifecycle_save and persists the .rws into root/profile/Saves (the
// durable location Prepare mirrors into the headless profile). The
// completed save must report the identity and tick that were loaded.
func SaveVariant(ctx context.Context, h *na.Harness, root string, headless bool, save string, row map[string]any) error {
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

	requestID := fmt.Sprintf("variantgen-%s-%d", sanitize(save), time.Now().UnixNano())
	saveReply, err := h.Wire(ctx, "save", "lifecycle_save", map[string]any{
		"player":       map[string]any{"identity": identity, "playerDirection": 1, "requestId": requestID},
		"saveName":     save,
		"expectedTick": tick,
	})
	if err != nil {
		return fmt.Errorf("lifecycle_save: %w", err)
	}
	_, completed, err := na.Outcome(saveReply, "completed")
	if err != nil {
		return fmt.Errorf("lifecycle_save: expected a completed save: %w", err)
	}
	if na.AsString(completed["saveName"]) != save {
		return fmt.Errorf("lifecycle_save: completed save name mismatch: %#v", completed)
	}
	completedContext, _ := na.AsMap(completed["context"])
	if na.AsNumber(completedContext["tick"]) != tick {
		return fmt.Errorf("lifecycle_save: completed tick does not match expected: %#v", completed)
	}
	row["saved"] = completed
	if err := persistSave(root, headless, save); err != nil {
		return fmt.Errorf("persist generated save to profile/Saves: %w", err)
	}
	row["path"] = SavePath(root, save)
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
