package nativeaccept

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestLastSchedulerStepReadsPlannerFailures(t *testing.T) {
	log := strings.Join([]string{
		`2026-09-18T19:46:01.000Z tick=4100 INFO [clock-worker] step done err=<nil> planner_failures=[] cause=full admitted=true`,
		`2026-09-18T19:46:03.123Z tick=4200 INFO [clock-worker] step done err=<nil> planner_failures="[resource: add bill preview: bridge read refused: bills/add_bill]" cause=timer admitted=false running=false`,
		`2026-09-18T19:46:04.000Z tick=4200 DEBUG [clock-scheduler] planner failed (isolated): resource: ...`,
	}, "\n")
	step, ok := LastSchedulerStep(strings.NewReader(log))
	if !ok {
		t.Fatal("expected a step line")
	}
	if step.PlannerFailures != "[resource: add bill preview: bridge read refused: bills/add_bill]" {
		t.Fatalf("planner_failures = %q", step.PlannerFailures)
	}
	if !step.Refused() || !strings.HasPrefix(step.Line, "2026-09-18T19:46:03.123Z") {
		t.Fatalf("latest step must be the refused one: %+v", step)
	}
	step, ok = LastSchedulerStep(strings.NewReader(log[:strings.Index(log, "\n")]))
	if !ok || step.PlannerFailures != "[]" || step.Refused() {
		t.Fatalf("a clean step has no failures: %+v ok=%v", step, ok)
	}
	if _, ok := LastSchedulerStep(strings.NewReader("[clock-scheduler] EvaluateClockWindow: work=false\n")); ok {
		t.Fatal("no step line must report none")
	}
	failed, ok := LastSchedulerStep(strings.NewReader(`2026-09-18T19:46:03.123Z tick=4200 WARN [clock-worker] step failed: Fields: context deadline exceeded err="Fields: context deadline exceeded" planner_failures=[]`))
	if !ok || failed.Refused() {
		t.Fatalf("a transport step failure is not a refusal: %+v", failed)
	}
}

func TestLastSchedulerStepFileReadsTheTail(t *testing.T) {
	path := filepath.Join(t.TempDir(), "stderr.log")
	var b strings.Builder
	for i := 0; i < 20000; i++ {
		b.WriteString("2026-09-18T19:46:00.000Z tick=1 DEBUG [clock-scheduler] filler line that pushes the step past the tail window\n")
	}
	b.WriteString(`2026-09-18T19:46:03.123Z tick=4200 INFO [clock-worker] step done err=<nil> planner_failures="[Fields: bridge read refused: fields/list]" cause=timer` + "\n")
	if err := os.WriteFile(path, []byte(b.String()), 0o644); err != nil {
		t.Fatal(err)
	}
	step, ok := LastSchedulerStepFile(path)
	if !ok || !step.Refused() {
		t.Fatalf("tail read missed the step: %+v ok=%v", step, ok)
	}
	if _, ok := LastSchedulerStepFile(filepath.Join(t.TempDir(), "missing.log")); ok {
		t.Fatal("a missing log reports no step")
	}
}
