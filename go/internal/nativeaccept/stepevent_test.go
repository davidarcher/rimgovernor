package nativeaccept

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// stepRow is a worker-step planner_step row (level and tick in the context,
// the verdict and the step's attributes in the payload). A WARN or ERROR row
// is a failed step whose error is msg; any other level is a waiting step and
// msg is ignored.
func stepRow(level, msg string, tick int, failures ...string) telemetrytest.Row {
	if failures == nil {
		failures = []string{}
	}
	verdict, reason := "waiting", "no_window"
	attrs := map[string]any{"planner_failures": failures, "admitted": false}
	if level == "WARN" || level == "ERROR" {
		verdict, reason = "failed", "step_error"
		attrs["error"] = msg
	}
	return telemetrytest.Row{Kind: "planner_step",
		Context: map[string]any{"level": level, "tick": tick, "at": "2026-09-18T19:46:03.123Z", "component": "clock-worker"},
		Payload: map[string]any{"verdict": verdict, "reason": reason, "target": "worker_step", "dur_ms": 0.0, "attrs": attrs}}
}

// writeFlight writes rows as a service's OTLP log ring and returns its path.
func writeFlight(t *testing.T, rows ...telemetrytest.Row) string {
	t.Helper()
	return telemetrytest.WriteLogs(t, t.TempDir(), rows...)
}

func TestLastSchedulerStepReadsPlannerFailures(t *testing.T) {
	other := telemetrytest.Row{Kind: "dispatch", Context: map[string]any{}, Payload: map[string]any{}}
	path := writeFlight(t,
		stepRow("INFO", "step done", 4100),
		stepRow("INFO", "step done", 4200, "resource: add bill preview: bridge read refused: bills/add_bill"),
		other,
	)
	step, ok := LastSchedulerStepFile(path)
	if !ok {
		t.Fatal("expected a step row")
	}
	if step.PlannerFailures != "[resource: add bill preview: bridge read refused: bills/add_bill]" {
		t.Fatalf("planner_failures = %q", step.PlannerFailures)
	}
	if !step.Refused() || !strings.Contains(step.Line, "bills/add_bill") {
		t.Fatalf("latest step must be the refused one: %+v", step)
	}
	clean, ok := LastSchedulerStepFile(writeFlight(t, stepRow("INFO", "step done", 4100)))
	if !ok || clean.PlannerFailures != "[]" || clean.Refused() {
		t.Fatalf("a clean step has no failures: %+v ok=%v", clean, ok)
	}
	if _, ok := LastSchedulerStepFile(writeFlight(t, other)); ok {
		t.Fatal("no step row must report none")
	}
	failed, ok := LastSchedulerStepFile(writeFlight(t, stepRow("WARN", "Fields: context deadline exceeded", 4200)))
	if !ok || failed.Refused() {
		t.Fatalf("a transport step failure is not a refusal: %+v", failed)
	}
}

func TestLastSchedulerStepFileReadsTheTail(t *testing.T) {
	filler := telemetrytest.LogLines(t, stepRow("INFO", "step done filler that pushes the step past the tail window", 1))[0]
	last := telemetrytest.LogLines(t, stepRow("INFO", "step done", 4200, "Fields: bridge read refused: fields/list"))[0]
	path := filepath.Join(t.TempDir(), "logs.jsonl")
	data := strings.Repeat(filler+"\n", 4000) + last + "\n"
	if err := os.WriteFile(path, []byte(data), 0o644); err != nil {
		t.Fatal(err)
	}
	step, ok := LastSchedulerStepFile(path)
	if !ok || !step.Refused() {
		t.Fatalf("tail read missed the step: %+v ok=%v", step, ok)
	}
	if _, ok := LastSchedulerStepFile(filepath.Join(t.TempDir(), "missing.jsonl")); ok {
		t.Fatal("a missing recording reports no step")
	}
}
