package nativeaccept

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// BaselineSave is the starting colony most save-driven harnesses begin from
// (profile/Saves/BaselineSave); sustained.BaselineSave is its name without
// the extension.
const BaselineSave = "RimGovernor-tribal8-baseline.rws"

// BaselineName is BaselineSave without the extension: the save name the op
// writes and Save starts load.
const BaselineName = "RimGovernor-tribal8-baseline"

// BaselineStart is the tribal-8 spec the baseline is generated from: eight
// LostTribe colonists in a TemperateForest, Medium difficulty, a little
// colder world, 250-cell map on a 0.3 planet. The seed pins the world; the
// team policy's rerolls decide the colonists.
var BaselineStart = ScenarioStart{
	Scenario: "LostTribe", Count: 8, Seed: "rimgovernor-tribal-eight-e",
	Biome: "TemperateForest", Difficulty: "Medium", WorldTemperature: "LittleBitColder",
	Size: DebugStart{MapSize: 250, PlanetCoverage: 0.3}, SaveName: BaselineName,
}

// baselineStampSuffix names the file beside the save that records which spec
// generated it, so a changed BaselineStart regenerates the save.
const baselineStampSuffix = ".spec.json"

// stampedStarts are the starts whose saves the harness generates on first use
// and regenerates when the spec changes, by save name: the baseline, and any
// part-bearing start a case registers with RegisterStampedStart.
var stampedStarts = map[string]ScenarioStart{BaselineName: BaselineStart}

// RegisterStampedStart makes start's save (start.SaveName) a generated,
// stamped save like the baseline: Config.EnsureSave generates it in profile/Saves
// on first use and again whenever the start's spec changes. Call it from an
// init; the start needs a Seed and a SaveName.
func RegisterStampedStart(start ScenarioStart) {
	if start.SaveName == "" || start.Seed == "" {
		panic("nativeaccept: a stamped start needs a SaveName and a Seed")
	}
	if _, dup := stampedStarts[start.SaveName]; dup {
		panic("nativeaccept: stamped start " + start.SaveName + " registered twice")
	}
	stampedStarts[start.SaveName] = start
}

func startSavePath(root, name string) string {
	return filepath.Join(mustAbs(root), "profile", "Saves", name+".rws")
}

func startStampPath(root, name string) string {
	return filepath.Join(mustAbs(root), "profile", "Saves", name+baselineStampSuffix)
}

func baselineSavePath(root string) string  { return startSavePath(root, BaselineName) }
func baselineStampPath(root string) string { return startStampPath(root, BaselineName) }

// startStamp is the stamp start would write now.
func startStamp(start ScenarioStart) ([]byte, error) {
	spec, err := start.Spec(QuietStorytellerDef)
	if err != nil {
		return nil, err
	}
	return json.MarshalIndent(spec, "", "  ")
}

// StartCurrent is whether root holds start's save generated from its current
// spec: the save exists and its stamp matches. A save with no stamp
// (hand-staged, or an older spec) is stale.
func StartCurrent(root string, start ScenarioStart) (bool, error) {
	if info, err := os.Stat(startSavePath(root, start.SaveName)); err != nil || info.IsDir() {
		return false, nil
	}
	want, err := startStamp(start)
	if err != nil {
		return false, err
	}
	have, err := os.ReadFile(startStampPath(root, start.SaveName))
	if err != nil {
		return false, nil
	}
	return bytes.Equal(bytes.TrimSpace(have), want), nil
}

// StampStart records that root's save for start was generated from its
// current spec.
func StampStart(root string, start ScenarioStart) error {
	want, err := startStamp(start)
	if err != nil {
		return err
	}
	return os.WriteFile(startStampPath(root, start.SaveName), append(want, '\n'), 0o644)
}

// BaselineCurrent is StartCurrent for BaselineStart.
func BaselineCurrent(root string) (bool, error) { return StartCurrent(root, BaselineStart) }

// StampBaseline is StampStart for BaselineStart.
func StampBaseline(root string) error { return StampStart(root, BaselineStart) }

// generateStart makes start's save under c.Root; tests replace it.
var generateStart func(c *Config, start ScenarioStart) error

func init() { generateStart = generateStartSave }

// EnsureSave generates a save the harness owns when root lacks a current copy:
// the tribal-8 baseline or a registered stamped start, once per root, on first
// use. Any other name is left to the caller.
func (c *Config) EnsureSave(name string) error {
	start, ok := stampedStarts[strings.TrimSuffix(name, ".rws")]
	if !ok {
		return nil
	}
	current, err := StartCurrent(c.Root, start)
	if err != nil || current {
		return err
	}
	if err := generateStart(c, start); err != nil {
		return fmt.Errorf("generate %s: %w", start.SaveName, err)
	}
	return StampStart(c.Root, start)
}

// generateStartSave starts start through the new-colony op on a
// Core-only profile (a stamped save's recorded mods pin every later run to
// Core), copies the save into profile/Saves and stops the game: its mod list
// differs from the run that asked for it.
func generateStartSave(c *Config, start ScenarioStart) (err error) {
	gameID := c.GameID
	if gameID == "" {
		gameID = "rimgovernor-trial"
	}
	gen := &Config{
		Root: c.Root, Output: filepath.Join(mustAbs(c.Root), "acceptance", "baseline-generate", start.SaveName),
		Headless: c.Headless, GameID: gameID, Timeout: c.Timeout, Expansions: []string{},
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Minute)
	defer cancel()
	sess, err := OpenSession(ctx, gen, Report{}, start, QuietIfAvailable, LiveNeeds)
	if err != nil {
		stopCtx, stop := context.WithTimeout(context.Background(), 2*time.Minute)
		defer stop()
		_ = StopGame(stopCtx, gen.Root, gameID)
		return err
	}
	sess.Game.Keep = false
	sess.Close()
	return PersistSave(c.Root, c.Headless, start.SaveName)
}

// PersistSave copies the save the running process just wrote from its own
// (possibly disposable) profile directory into profile/Saves, the durable
// location every harness's Prepare reads from. In rendered mode the running
// profile already is profile/Saves; in headless mode it is headless-profile/Saves,
// which Prepare overwrites from profile/Saves on every run.
func PersistSave(root string, headless bool, save string) error {
	dst := filepath.Join(mustAbs(root), "profile", "Saves", save+".rws")
	running := "profile"
	if headless {
		running = "headless-profile"
	}
	src := filepath.Join(mustAbs(root), running, "Saves", save+".rws")
	if src == dst {
		return nil
	}
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	out, err := os.OpenFile(dst, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0o644)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}
