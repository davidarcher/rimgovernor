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

func baselineSavePath(root string) string {
	return filepath.Join(mustAbs(root), "profile", "Saves", BaselineSave)
}

func baselineStampPath(root string) string {
	return filepath.Join(mustAbs(root), "profile", "Saves", BaselineName+baselineStampSuffix)
}

// baselineStamp is the stamp BaselineStart would write now.
func baselineStamp() ([]byte, error) {
	spec, err := BaselineStart.Spec(true)
	if err != nil {
		return nil, err
	}
	return json.MarshalIndent(spec, "", "  ")
}

// BaselineCurrent is whether root holds a baseline save generated from the
// current BaselineStart: the save exists and its stamp matches. A save with no
// stamp (hand-staged, or an older spec) is stale.
func BaselineCurrent(root string) (bool, error) {
	if info, err := os.Stat(baselineSavePath(root)); err != nil || info.IsDir() {
		return false, nil
	}
	want, err := baselineStamp()
	if err != nil {
		return false, err
	}
	have, err := os.ReadFile(baselineStampPath(root))
	if err != nil {
		return false, nil
	}
	return bytes.Equal(bytes.TrimSpace(have), want), nil
}

// StampBaseline records that root's baseline save was generated from the
// current BaselineStart.
func StampBaseline(root string) error {
	want, err := baselineStamp()
	if err != nil {
		return err
	}
	return os.WriteFile(baselineStampPath(root), append(want, '\n'), 0o644)
}

// generateBaseline makes the baseline save under c.Root; tests replace it.
var generateBaseline func(c *Config) error

func init() { generateBaseline = generateBaselineSave }

// EnsureSave generates a save the harness owns when root lacks a current copy:
// today the tribal-8 baseline (BaselineName), once per root, on first use. Any
// other name is left to the caller.
func (c *Config) EnsureSave(name string) error {
	if strings.TrimSuffix(name, ".rws") != BaselineName {
		return nil
	}
	current, err := BaselineCurrent(c.Root)
	if err != nil || current {
		return err
	}
	if err := generateBaseline(c); err != nil {
		return fmt.Errorf("generate %s: %w", BaselineName, err)
	}
	return StampBaseline(c.Root)
}

// generateBaselineSave starts BaselineStart through the new-colony op on a
// Core-only profile (the baseline's recorded mods pin every later run to
// Core), copies the save into profile/Saves and stops the game: its mod list
// differs from the run that asked for the baseline.
func generateBaselineSave(c *Config) (err error) {
	gameID := c.GameID
	if gameID == "" {
		gameID = "rimgovernor-trial"
	}
	gen := &Config{
		Root: c.Root, Output: filepath.Join(mustAbs(c.Root), "acceptance", "baseline-generate"),
		Headless: c.Headless, GameID: gameID, Timeout: c.Timeout, Expansions: []string{},
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Minute)
	defer cancel()
	sess, err := OpenSession(ctx, gen, Report{}, BaselineStart, QuietIfAvailable, LiveNeeds)
	if err != nil {
		stopCtx, stop := context.WithTimeout(context.Background(), 2*time.Minute)
		defer stop()
		_ = StopGame(stopCtx, gen.Root, gameID)
		return err
	}
	sess.Game.Keep = false
	sess.Close()
	return PersistSave(c.Root, c.Headless, BaselineName)
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
