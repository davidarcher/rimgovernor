package nativeaccept

import (
	"os"
	"path/filepath"
	"testing"
)

// withGenerator swaps the baseline generator for fn and counts its calls.
func withGenerator(t *testing.T, fn func(c *Config) error) *int {
	t.Helper()
	calls := 0
	previous := generateBaseline
	generateBaseline = func(c *Config) error { calls++; return fn(c) }
	t.Cleanup(func() { generateBaseline = previous })
	return &calls
}

func writeBaseline(t *testing.T, root, body string) {
	t.Helper()
	path := baselineSavePath(root)
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestEnsureSaveLeavesCurrentBaselineAlone(t *testing.T) {
	root := t.TempDir()
	writeBaseline(t, root, "generated")
	if err := StampBaseline(root); err != nil {
		t.Fatal(err)
	}
	calls := withGenerator(t, func(*Config) error { return nil })
	for _, name := range []string{BaselineName, BaselineSave} {
		if err := (&Config{Root: root}).EnsureSave(name); err != nil {
			t.Fatal(err)
		}
	}
	if *calls != 0 {
		t.Fatalf("a current baseline was regenerated %d times", *calls)
	}
}

func TestEnsureSaveGeneratesMissingBaselineOnce(t *testing.T) {
	root := t.TempDir()
	calls := withGenerator(t, func(c *Config) error {
		writeBaseline(t, c.Root, "generated")
		return nil
	})
	cfg := &Config{Root: root}
	if err := cfg.EnsureSave(BaselineName); err != nil {
		t.Fatal(err)
	}
	if got, _ := os.ReadFile(baselineSavePath(root)); string(got) != "generated" {
		t.Fatalf("baseline = %q", got)
	}
	if current, err := BaselineCurrent(root); err != nil || !current {
		t.Fatalf("generated baseline is not stamped current: %v, %v", current, err)
	}
	if err := cfg.EnsureSave(BaselineName); err != nil {
		t.Fatal(err)
	}
	if *calls != 1 {
		t.Fatalf("generator ran %d times, want 1", *calls)
	}
}

func TestEnsureSaveRegeneratesStaleBaseline(t *testing.T) {
	for name, stamp := range map[string]string{"no stamp": "", "old spec": `{"seed":"an-older-seed"}`} {
		t.Run(name, func(t *testing.T) {
			root := t.TempDir()
			writeBaseline(t, root, "old colony")
			if stamp != "" {
				if err := os.WriteFile(baselineStampPath(root), []byte(stamp), 0o644); err != nil {
					t.Fatal(err)
				}
			}
			calls := withGenerator(t, func(c *Config) error {
				writeBaseline(t, c.Root, "new colony")
				return nil
			})
			if err := (&Config{Root: root}).EnsureSave(BaselineName); err != nil {
				t.Fatal(err)
			}
			if *calls != 1 {
				t.Fatalf("generator ran %d times, want 1", *calls)
			}
			if got, _ := os.ReadFile(baselineSavePath(root)); string(got) != "new colony" {
				t.Fatalf("baseline = %q", got)
			}
			if current, _ := BaselineCurrent(root); !current {
				t.Fatal("regenerated baseline is not stamped current")
			}
		})
	}
}

func TestEnsureSaveFailureLeavesNoStamp(t *testing.T) {
	root := t.TempDir()
	withGenerator(t, func(*Config) error { return os.ErrDeadlineExceeded })
	if err := (&Config{Root: root}).EnsureSave(BaselineName); err == nil {
		t.Fatal("a failed generation was reported as success")
	}
	if current, _ := BaselineCurrent(root); current {
		t.Fatal("a failed generation left a current stamp")
	}
}

func TestEnsureSaveIgnoresOtherSaves(t *testing.T) {
	calls := withGenerator(t, func(*Config) error { return nil })
	if err := (&Config{Root: t.TempDir()}).EnsureSave("RimGovernor-matrix-x"); err != nil || *calls != 0 {
		t.Fatalf("other save triggered generation: %v, %d calls", err, *calls)
	}
}

func TestBaselineStartSpec(t *testing.T) {
	spec, err := BaselineStart.Spec(true)
	if err != nil {
		t.Fatal(err)
	}
	if spec["scenario"] != "LostTribe" || spec["colonistCount"] != 8 || spec["seed"] != "rimgovernor-tribal-eight-e" ||
		spec["difficulty"] != "Medium" || spec["worldTemperature"] != "LittleBitColder" || spec["mapSize"] != 250 ||
		spec["planetCoverage"] != 0.3 || spec["storyteller"] != QuietStorytellerDef || spec["saveName"] != BaselineName {
		t.Fatalf("baseline spec = %v", spec)
	}
	if biomes, _ := spec["biomes"].([]any); len(biomes) != 1 || biomes[0] != "TemperateForest" {
		t.Fatalf("biomes = %v", spec["biomes"])
	}
	if _, ok := spec["minTemperature"]; ok {
		t.Fatalf("an unconstrained temperature band was sent: %v", spec)
	}
}

func TestScenarioStartSpecRules(t *testing.T) {
	if _, err := (ScenarioStart{Scenario: "LostTribe", Count: 1}).Spec(true); err == nil {
		t.Fatal("a start without a seed was accepted")
	}
	loud, err := (ScenarioStart{Scenario: "Crashlanded", Count: 3, Seed: "s", MinTemperature: -10, MaxTemperature: 5}).Spec(false)
	if err != nil {
		t.Fatal(err)
	}
	if loud["storyteller"] != LoudStorytellerDef || loud["difficulty"] != "Rough" || loud["minTemperature"] != -10.0 || loud["maxTemperature"] != 5.0 ||
		loud["saveName"] != DefaultScenarioSaveName {
		t.Fatalf("spec = %v", loud)
	}
	open, _ := (ScenarioStart{Scenario: "Crashlanded", Count: 3, Seed: "s", MinTemperature: -100, MaxTemperature: 100}).Spec(true)
	if _, ok := open["minTemperature"]; ok {
		t.Fatalf("the unconstrained band was sent: %v", open)
	}
}
