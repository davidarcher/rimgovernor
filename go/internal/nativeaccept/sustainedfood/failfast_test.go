package sustainedfood

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestFailFastRetryableUnsuccessfulSkipsListedReasons(t *testing.T) {
	sample := func(reason string) map[string]any {
		return map[string]any{"method_count": 1, "plans": []map[string]any{{
			"plan": "plan-1", "unsuccessful": []map[string]any{{"action": "a-1", "kind": "acquire", "reason": reason}},
		}}}
	}
	on := newFailFast(FailFast{RetryableUnsuccessful: []domain.UnsuccessfulReason{domain.OutcomeNotAchieved}}, policy.MaintainResource, "")
	if v, failed := on.check(sample("outcome_not_achieved")); failed {
		t.Fatalf("outcome_not_achieved is retryable here: %v", v)
	}
	if _, failed := on.check(sample("native_failure")); !failed {
		t.Fatal("native_failure must still trip")
	}
	off := newFailFast(FailFast{}, policy.MaintainResource, "")
	if _, failed := off.check(sample("outcome_not_achieved")); !failed {
		t.Fatal("outcome_not_achieved must trip without the option")
	}
}

func idleSample(revision uint64, idle bool, methods int) map[string]any {
	blocked := ""
	if !idle {
		blocked = string(policy.BlockedWaiting("prerequisite"))
	}
	return map[string]any{"review_revision": revision, "method_count": methods, "need": "unmet", "status": "open", "blocked": blocked}
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
	recovered["need"] = "met"
	if _, failed := g.check(recovered); failed {
		t.Fatal("a recovered goal with no method is not a refusal")
	}
	// A goal the review never selected (startup_survival) is waiting, not refused.
	waiting := idleSample(2, false, 0)
	waiting["blocked"] = string(policy.BlockedWaiting("prerequisite"))
	if _, failed := g.check(waiting); failed {
		t.Fatal("an unselected goal is not a refusal")
	}
}

func TestFailFastNoMethodMethodUnavailableWaits(t *testing.T) {
	yielded := func(revision uint64) map[string]any {
		s := idleSample(revision, true, 0)
		s["blocked"] = string(policy.HeldUnavailable)
		return s
	}
	// By default a method_unavailable row counts like any idle review.
	f := newFailFast(FailFast{NoMethodReviews: 2}, policy.MaintainResource, "")
	f.check(yielded(1))
	if _, failed := f.check(yielded(2)); !failed {
		t.Fatal("two method_unavailable reviews must fail without the opt-in")
	}
	// With the opt-in the ladder's research rung is neutral: it neither
	// counts nor resets, and the count resumes once the slot is handed.
	g := newFailFast(FailFast{NoMethodReviews: 2, MethodUnavailableWaits: true}, policy.MaintainResource, "")
	g.check(idleSample(1, true, 0))
	for rev := uint64(2); rev < 8; rev++ {
		if v, failed := g.check(yielded(rev)); failed {
			t.Fatalf("revision %d: %v", rev, v)
		}
	}
	if v, failed := g.check(idleSample(8, true, 0)); !failed || !strings.Contains(v.Reason, "revisions 1..8") {
		t.Fatalf("the handed slot resumes the count: failed=%v %+v", failed, v)
	}
}

// A review an emergency holds (a dialog pause parks every development row
// idle at one tick, #156) hands no planner the slot: it is neutral for the
// no-method count, without any opt-in.
func TestFailFastNoMethodEmergencyHoldIsNeutral(t *testing.T) {
	held := func(revision uint64) map[string]any {
		s := idleSample(revision, true, 0)
		s["blocked"] = string(policy.HeldEmergency)
		return s
	}
	f := newFailFast(FailFast{NoMethodReviews: 2}, policy.MaintainResource, "")
	f.check(idleSample(1, true, 0))
	for rev := uint64(2); rev < 8; rev++ {
		if v, failed := f.check(held(rev)); failed {
			t.Fatalf("revision %d: %v", rev, v)
		}
	}
	if v, failed := f.check(idleSample(8, true, 0)); !failed || !strings.Contains(v.Reason, "revisions 1..8") {
		t.Fatalf("the handed slot resumes the count: failed=%v %+v", failed, v)
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

// stepRow is a worker-step planner_step flight row carrying the given planner failures.
func stepRow(seq int, failures ...string) string {
	if failures == nil {
		failures = []string{}
	}
	line, _ := json.Marshal(map[string]any{
		"sequence": seq, "kind": "planner_step",
		"context": map[string]any{"level": "INFO", "tick": 4200},
		"payload": map[string]any{"verdict": "waiting", "reason": "no_window", "target": "worker_step", "dur_ms": 0, "attrs": map[string]any{"planner_failures": failures}},
	})
	return string(line)
}

func TestFailFastRepeatedRefusalReadsTheLatestStep(t *testing.T) {
	path := filepath.Join(t.TempDir(), "flight.jsonl")
	write := func(lines ...string) {
		if err := os.WriteFile(path, []byte(strings.Join(lines, "\n")+"\n"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	refused := stepRow(1, "resource: bridge read refused: bills/add_bill")
	clean := stepRow(2)
	f := newFailFast(FailFast{RefusalSamples: 3}, policy.MaintainResource, path)
	sample := map[string]any{"method_count": 0, "need": "unmet", "status": "open"}
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
	refused = stepRow(3, "resource: bridge read refused: bills/add_bill")
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
	f := newFailFast(FailFast{ParkSamples: 3}, policy.ManageSupplySafety, "")
	parked := func(tick uint64, vetoed bool, emergency ...string) map[string]any {
		return map[string]any{"review_revision": uint64(42), "status": "open", "vetoed": vetoed, "need": "unmet", "tick": tick, "emergency": emergency}
	}
	// A vetoed goal under an emergency with the tick moving is being served.
	for _, tick := range []uint64{100, 160, 220} {
		if v, failed := f.check(parked(tick, true, "EnsureComfort")); failed {
			t.Fatalf("a moving tick is not a park: %v", v)
		}
	}
	// Two parked samples, then the emergency clears: the count resets.
	f.check(parked(220, true, "EnsureComfort"))
	if v, failed := f.check(parked(220, false)); failed {
		t.Fatalf("no emergency is not a park: %v", v)
	}
	if v, failed := f.check(parked(220, true, "EnsureComfort")); failed {
		t.Fatalf("first parked sample after a reset: %v", v)
	}
	if v, failed := f.check(parked(220, true, "EnsureComfort")); failed {
		t.Fatalf("second parked sample: %v", v)
	}
	v, failed := f.check(parked(220, true, "EnsureComfort"))
	if !failed || v.Shape != "emergency_park" || !strings.Contains(v.Reason, "[EnsureComfort]") || !strings.Contains(v.Reason, "parked at 220 for 3 samples") {
		t.Fatalf("third consecutive parked sample must fail: failed=%v %+v", failed, v)
	}
	// A sample without a live tick never counts.
	g := newFailFast(FailFast{ParkSamples: 1}, policy.ManageSupplySafety, "")
	noTick := parked(0, true, "EnsureComfort")
	delete(noTick, "tick")
	if _, failed := g.check(noTick); failed {
		t.Fatal("a sample without a live tick is not a park")
	}
}
