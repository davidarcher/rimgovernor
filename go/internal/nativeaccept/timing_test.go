package nativeaccept

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestFinalizeRecordsTiming(t *testing.T) {
	ResetTickStats()
	observeReplyTick("home/colony_facts", map[string]any{"tick": 1000.0})
	observeWireTick(map[string]any{"loaded": map[string]any{"context": map[string]any{"tick": "1600"}}})
	output := t.TempDir()
	report := NewReport("timing", true)
	report[StartedAtKey] = time.Now().Add(-2 * time.Second).UTC().Format(time.RFC3339Nano)
	report["game_reuse"] = map[string]any{"reused": false, "openMs": int64(4200)}
	report["passed"] = true
	if code := report.Finalize(output); code != 0 {
		t.Fatalf("exit %d: %v", code, report["error"])
	}
	data, err := os.ReadFile(filepath.Join(output, "result.json"))
	if err != nil {
		t.Fatal(err)
	}
	var written map[string]any
	if err := json.Unmarshal(data, &written); err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{StartedAtKey, FinishedAtKey, WallMsKey, BootMsKey, TicksAdvancedKey, WallTPSKey} {
		if _, has := written[key]; !has {
			t.Errorf("result.json lacks %s: %v", key, written)
		}
	}
	if written[BootMsKey] != 4200.0 {
		t.Errorf("boot_ms = %v, want game_reuse.openMs", written[BootMsKey])
	}
	if written[TicksAdvancedKey] != 600.0 {
		t.Errorf("ticks_advanced = %v, want 600", written[TicksAdvancedKey])
	}
	if tps, _ := written[WallTPSKey].(float64); tps <= 0 || tps > 300 {
		t.Errorf("wall_tps = %v for 600 ticks over about 2s", written[WallTPSKey])
	}
	if _, has := written[BudgetMsKey]; has {
		t.Errorf("an unbudgeted report recorded budget_ms")
	}
}

func TestFinalizeFailsARunOverBudget(t *testing.T) {
	report := NewReport("budget", true)
	report[StartedAtKey] = time.Now().Add(-3 * time.Second).UTC().Format(time.RFC3339Nano)
	report["passed"] = true
	report.SetBudget(2 * time.Second)
	if code := report.Finalize(t.TempDir()); code != 1 {
		t.Fatalf("exit %d for a run over budget", code)
	}
	if err, _ := report["error"].(string); !strings.HasPrefix(err, "run exceeded its budget: ") {
		t.Errorf("error = %q", err)
	}
	if report["budget_exceeded"] != true || report["passed"] != false {
		t.Errorf("report = %v", report)
	}

	// A failed run over budget keeps its own error.
	report = NewReport("budget", true)
	report[StartedAtKey] = time.Now().Add(-3 * time.Second).UTC().Format(time.RFC3339Nano)
	report["error"] = "its own failure"
	report.SetBudget(2 * time.Second)
	report.Finalize(t.TempDir())
	if report["error"] != "its own failure" || report["budget_exceeded"] != true {
		t.Errorf("report = %v", report)
	}

	// Within budget passes.
	report = NewReport("budget", true)
	report["passed"] = true
	report.SetBudget(time.Minute)
	if code := report.Finalize(t.TempDir()); code != 0 {
		t.Errorf("exit %d within budget: %v", code, report["error"])
	}
}

func TestFinalizeWithoutABudgetPasses(t *testing.T) {
	report := NewReport("budget", true)
	report[StartedAtKey] = time.Now().Add(-2 * time.Second).UTC().Format(time.RFC3339Nano)
	report["passed"] = true
	if code := report.Finalize(t.TempDir()); code != 0 {
		t.Errorf("exit %d for a run with no budget: %v", code, report["error"])
	}
	if _, has := report[BudgetMsKey]; has {
		t.Errorf("budget_ms = %v without SetBudget", report[BudgetMsKey])
	}
}

func TestTickObservationCountsForwardProgressOnly(t *testing.T) {
	ResetTickStats()
	observeReplyTick("home/status", map[string]any{"time": map[string]any{"ticksGame": 500.0}})
	observeReplyTick("home/supervised_play", map[string]any{"lastTick": 800.0})
	// A rewind (an older save loaded without a load tool passing through
	// the harness) re-baselines without counting.
	observeReplyTick("home/colony_facts", map[string]any{"tick": 100.0})
	observeReplyTick("home/colony_facts", map[string]any{"tick": 150.0})
	// A load re-baselines: the loaded save's tick is not progress.
	observeReplyTick("rimworld/load_game_ready", map[string]any{})
	observeWireTick(map[string]any{"loaded": map[string]any{"context": map[string]any{"tick": "90000"}}})
	observeWireTick(map[string]any{"loaded": map[string]any{"context": map[string]any{"tick": "90010"}}})
	if got := TicksAdvanced(); got != 300+50+10 {
		t.Errorf("ticks advanced = %d, want 360", got)
	}
	ResetTickStats()
	if TicksAdvanced() != 0 {
		t.Errorf("reset kept ticks")
	}
}
