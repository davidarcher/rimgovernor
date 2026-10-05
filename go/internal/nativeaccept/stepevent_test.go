package nativeaccept

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// stepRowLine is one flight.jsonl line: a worker-step planner_step row as the
// recorder writes it (level and tick in the context, the verdict and the
// step's attributes in the payload). A WARN or ERROR row is a failed step
// whose error is msg; any other level is a waiting step and msg is ignored.
func stepRowLine(seq int, level, msg string, tick int, failures ...string) string {
	if failures == nil {
		failures = []string{}
	}
	verdict, reason := "waiting", "no_window"
	attrs := map[string]any{"planner_failures": failures, "admitted": false}
	if level == "WARN" || level == "ERROR" {
		verdict, reason = "failed", "step_error"
		attrs["error"] = msg
	}
	line, _ := json.Marshal(map[string]any{
		"sequence": seq, "kind": "planner_step",
		"context": map[string]any{"level": level, "tick": tick, "at": "2026-09-18T19:46:03.123Z", "component": "clock-worker"},
		"payload": map[string]any{"verdict": verdict, "reason": reason, "target": "worker_step", "dur_ms": 0, "attrs": attrs},
	})
	return string(line)
}

func writeFlight(t *testing.T, lines ...string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "flight.jsonl")
	if err := os.WriteFile(path, []byte(strings.Join(lines, "\n")+"\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	return path
}

func TestLastSchedulerStepReadsPlannerFailures(t *testing.T) {
	other, _ := json.Marshal(map[string]any{"sequence": 4, "kind": "dispatch", "context": map[string]any{}, "payload": map[string]any{}})
	path := writeFlight(t,
		stepRowLine(1, "INFO", "step done", 4100),
		stepRowLine(2, "INFO", "step done", 4200, "resource: add bill preview: bridge read refused: bills/add_bill"),
		string(other),
	)
	step, ok := LastSchedulerStepFile(path)
	if !ok {
		t.Fatal("expected a step row")
	}
	if step.PlannerFailures != "[resource: add bill preview: bridge read refused: bills/add_bill]" {
		t.Fatalf("planner_failures = %q", step.PlannerFailures)
	}
	if !step.Refused() || !strings.Contains(step.Line, `"sequence":2`) {
		t.Fatalf("latest step must be the refused one: %+v", step)
	}
	clean, ok := LastSchedulerStepFile(writeFlight(t, stepRowLine(1, "INFO", "step done", 4100)))
	if !ok || clean.PlannerFailures != "[]" || clean.Refused() {
		t.Fatalf("a clean step has no failures: %+v ok=%v", clean, ok)
	}
	if _, ok := LastSchedulerStepFile(writeFlight(t, string(other))); ok {
		t.Fatal("no step row must report none")
	}
	failed, ok := LastSchedulerStepFile(writeFlight(t, stepRowLine(1, "WARN", "Fields: context deadline exceeded", 4200)))
	if !ok || failed.Refused() {
		t.Fatalf("a transport step failure is not a refusal: %+v", failed)
	}
}

func TestLastSchedulerStepFileReadsTheTail(t *testing.T) {
	lines := make([]string, 0, 4001)
	for i := 0; i < 4000; i++ {
		lines = append(lines, stepRowLine(i, "INFO", "step done filler that pushes the step past the tail window", 1))
	}
	lines = append(lines, stepRowLine(4000, "INFO", "step done", 4200, "Fields: bridge read refused: fields/list"))
	step, ok := LastSchedulerStepFile(writeFlight(t, lines...))
	if !ok || !step.Refused() {
		t.Fatalf("tail read missed the step: %+v ok=%v", step, ok)
	}
	if _, ok := LastSchedulerStepFile(filepath.Join(t.TempDir(), "missing.jsonl")); ok {
		t.Fatal("a missing recording reports no step")
	}
}
