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
	observeReply(t, "home/colony_facts", map[string]any{"tick": 1000.0})
	observeReply(t, "rimgovernor/lifecycle_read_identity", wireReply(`{"loaded":{"context":{"tick":"1600"}}}`))
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
	observeReply(t, "home/status", map[string]any{"time": map[string]any{"ticksGame": 500.0}})
	observeReply(t, "home/status", map[string]any{"time": map[string]any{"ticksGame": 800.0}})
	// A rewind (an older save loaded without a load tool passing through
	// the harness) re-baselines without counting.
	observeReply(t, "home/colony_facts", map[string]any{"tick": 100.0})
	observeReply(t, "home/colony_facts", map[string]any{"tick": 150.0})
	// A load re-baselines: the loaded save's tick is not progress.
	observeReplyTick("rimworld/load_game_ready", nil)
	observeReply(t, "rimgovernor/lifecycle_read_identity", wireReply(`{"loaded":{"context":{"tick":"90000"}}}`))
	observeReply(t, "rimgovernor/lifecycle_read_identity", wireReply(`{"loaded":{"context":{"tick":"90010"}}}`))
	if got := TicksAdvanced(); got != 300+50+10 {
		t.Errorf("ticks advanced = %d, want 360", got)
	}
	ResetTickStats()
	if TicksAdvanced() != 0 {
		t.Errorf("reset kept ticks")
	}
}

func TestFinalizeWritesDiagnosisFirst(t *testing.T) {
	output := t.TempDir()
	report := NewReport("digest", true)
	report["error"] = "boom"
	report["diagnosis"] = map[string]any{"sections": []any{}}
	if code := report.Finalize(output); code != 1 {
		t.Fatalf("exit %d", code)
	}
	data, err := os.ReadFile(filepath.Join(output, "result.json"))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(string(data), "{\n  \"diagnosis\": {") {
		t.Fatalf("diagnosis is not the first member:\n%s", data)
	}
	var written map[string]any
	if err := json.Unmarshal(data, &written); err != nil {
		t.Fatalf("result.json is not valid JSON: %v\n%s", err, data)
	}
	if written["error"] != "boom" || written["diagnosis"] == nil || written["scope"] != "digest" {
		t.Fatalf("result.json = %v", written)
	}
}

// observeReply feeds a decoded reply through replyTick and observeReplyTick
// as Harness.Call does, failing when the reply carries no tick.
func observeReply(t *testing.T, tool string, payload map[string]any) {
	t.Helper()
	tick, ok := replyTick(tool, payload)
	if !ok {
		t.Fatalf("%s reply %v carries no tick", tool, payload)
	}
	observeReplyTick(tool, &tick)
}

// wireReply is a rimgovernor/* reply's structured content: the ProtoJSON
// message as the "payload" string.
func wireReply(message string) map[string]any { return map[string]any{"payload": message} }

func TestReplyTickIgnoresRepliesWithoutOne(t *testing.T) {
	for tool, payload := range map[string]map[string]any{
		"home/status":                           {"time": map[string]any{}},
		"rimworld/set_time_speed":               {"success": true},
		"rimgovernor/authority_read_status":     wireReply(`{"status":{"owner":"controller"}}`),
		"rimgovernor/lifecycle_read_identity":   {"payload": 7},
		"rimgovernor/lifecycle_read_identity_2": wireReply(`not json`),
	} {
		if tick, ok := replyTick(tool, payload); ok {
			t.Errorf("%s: unexpected tick %d from %v", tool, tick, payload)
		}
	}
}
