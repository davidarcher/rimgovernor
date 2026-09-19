package sustainedfood

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func idleSample(revision uint64, idle bool, methods int) map[string]any {
	return map[string]any{
		"review_revision": revision, "method_count": methods, "need": "deficit", "status": "active",
		"development": map[string]any{"reason": "", "selected": true, "committed": methods > 0, "idle": idle},
	}
}

func TestFailFastNoMethodCountsDistinctIdleReviews(t *testing.T) {
	f := newFailFast(FailFast{NoMethodReviews: 3}, policy.EnsureComfort, "")
	for _, s := range []map[string]any{idleSample(1, false, 0), idleSample(2, true, 0), idleSample(2, true, 0), idleSample(3, true, 0)} {
		if v, failed := f.check(s); failed {
			t.Fatalf("two idle revisions must not fail: %v", v)
		}
	}
	// A committed review resets the count.
	if _, failed := f.check(idleSample(4, false, 1)); failed {
		t.Fatal("a committed method resets the count")
	}
	for _, rev := range []uint64{5, 6} {
		if v, failed := f.check(idleSample(rev, true, 0)); failed {
			t.Fatalf("revision %d: %v", rev, v)
		}
	}
	v, failed := f.check(idleSample(7, true, 0))
	if !failed || v.Shape != "no_method" || !strings.Contains(v.Reason, "revisions 5..7") {
		t.Fatalf("third consecutive idle review must fail: failed=%v %+v", failed, v)
	}
	// A satisfied goal is never a no-method failure, however idle.
	g := newFailFast(FailFast{NoMethodReviews: 1}, policy.EnsureComfort, "")
	recovered := idleSample(1, true, 0)
	recovered["need"] = "recovered"
	if _, failed := g.check(recovered); failed {
		t.Fatal("a recovered goal with no method is not a refusal")
	}
	// A goal the review never selected (startup_survival) is waiting, not refused.
	waiting := idleSample(2, false, 0)
	waiting["development"] = map[string]any{"reason": "startup_survival", "selected": false, "committed": false, "idle": false}
	if _, failed := g.check(waiting); failed {
		t.Fatal("an unselected goal is not a refusal")
	}
}

func TestFailFastUnsuccessfulStageSkipsReplannedReasons(t *testing.T) {
	f := newFailFast(FailFast{}, policy.MaintainResource, "")
	sample := func(reason string) map[string]any {
		return map[string]any{"method_count": 1, "plans": []map[string]any{{
			"plan": "plan-1", "unsuccessful": []map[string]any{{"action": "a-1", "kind": "haul", "reason": reason}},
		}}}
	}
	for _, reason := range []string{"interrupted", "cancelled"} {
		if v, failed := f.check(sample(reason)); failed {
			t.Fatalf("%s is re-planned, not fatal: %v", reason, v)
		}
	}
	v, failed := f.check(sample("native_failure"))
	if !failed || v.Shape != "unsuccessful" || !strings.Contains(v.Reason, "plan plan-1 action a-1 ended unsuccessful (native_failure)") {
		t.Fatalf("failed=%v %+v", failed, v)
	}
	if !strings.HasPrefix(v.Error(), "fail-fast (unsuccessful): ") {
		t.Fatalf("error text: %s", v.Error())
	}
}

func TestFailFastRepeatedRefusalReadsTheLatestStep(t *testing.T) {
	path := filepath.Join(t.TempDir(), "stderr.log")
	write := func(lines ...string) {
		if err := os.WriteFile(path, []byte(strings.Join(lines, "\n")+"\n"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	refused := `2026-09-18T19:46:03.123Z tick=4200 INFO [clock-worker] step done err=<nil> planner_failures="[resource: bridge read refused: bills/add_bill]" cause=timer`
	clean := `2026-09-18T19:46:09.000Z tick=4300 INFO [clock-worker] step done err=<nil> planner_failures=[] cause=timer`
	f := newFailFast(FailFast{RefusalSamples: 3}, policy.MaintainResource, path)
	sample := map[string]any{"method_count": 0, "need": "deficit", "status": "active"}
	write(refused)
	for i := 0; i < 2; i++ {
		if v, failed := f.check(sample); failed {
			t.Fatalf("sample %d: %v", i, v)
		}
	}
	// A clean step in between resets the run.
	write(refused, clean)
	if _, failed := f.check(sample); failed {
		t.Fatal("a clean latest step is not a refusal")
	}
	write(refused, clean, refused)
	for i := 0; i < 2; i++ {
		if v, failed := f.check(sample); failed {
			t.Fatalf("after reset, sample %d: %v", i, v)
		}
	}
	v, failed := f.check(sample)
	if !failed || v.Shape != "refusal" || !strings.Contains(v.Reason, "bridge read refused: bills/add_bill") || v.Evidence != refused {
		t.Fatalf("failed=%v %+v", failed, v)
	}
	// Disabled never fails.
	d := newFailFast(FailFast{Disabled: true, RefusalSamples: 1}, policy.MaintainResource, path)
	if _, failed := d.check(sample); failed {
		t.Fatal("disabled fail-fast must not fire")
	}
}

func TestFailFastEmergencyParkNeedsAnUnmovingTick(t *testing.T) {
	f := newFailFast(FailFast{ParkSamples: 3}, policy.AllowStartingSupplies, "")
	parked := func(tick uint64, status string, emergency ...string) map[string]any {
		return map[string]any{"review_revision": uint64(42), "status": status, "need": "deficit", "tick": tick, "emergency": emergency}
	}
	// A suspended goal under an emergency with the tick moving is being served.
	for _, tick := range []uint64{100, 160, 220} {
		if v, failed := f.check(parked(tick, "suspended", "EnsureComfort")); failed {
			t.Fatalf("a moving tick is not a park: %v", v)
		}
	}
	// Two parked samples, then the emergency clears: the count resets.
	f.check(parked(220, "suspended", "EnsureComfort"))
	if v, failed := f.check(parked(220, "active")); failed {
		t.Fatalf("no emergency is not a park: %v", v)
	}
	if v, failed := f.check(parked(220, "suspended", "EnsureComfort")); failed {
		t.Fatalf("first parked sample after a reset: %v", v)
	}
	if v, failed := f.check(parked(220, "suspended", "EnsureComfort")); failed {
		t.Fatalf("second parked sample: %v", v)
	}
	v, failed := f.check(parked(220, "suspended", "EnsureComfort"))
	if !failed || v.Shape != "emergency_park" || !strings.Contains(v.Reason, "[EnsureComfort]") || !strings.Contains(v.Reason, "parked at 220 for 3 samples") {
		t.Fatalf("third consecutive parked sample must fail: failed=%v %+v", failed, v)
	}
	// A sample without a live tick never counts.
	g := newFailFast(FailFast{ParkSamples: 1}, policy.AllowStartingSupplies, "")
	noTick := parked(0, "suspended", "EnsureComfort")
	delete(noTick, "tick")
	if _, failed := g.check(noTick); failed {
		t.Fatal("a sample without a live tick is not a park")
	}
}
