package policy

import (
	"testing"
)

func TestPlannerRefusalNamesTheBlock(t *testing.T) {
	c := ConcernProgressContract("", DefaultRoundsPolicy())
	p := ReviewConcernProgress(ConcernProgress{Concern: EnsureBasicPower, Method: "assess", Planner: "insufficient_verified_space"}, EnsureBasicPower, c, ProgressEvidence{}, 10)
	if p.Blocked != BlockedPlanner("insufficient_verified_space") || !p.Blocked.Actionable() || ValidateConcernProgress(p, 10) != nil {
		t.Fatalf("%+v", p)
	}
	if bad := (ConcernProgress{Concern: EnsureBasicPower, Planner: "bell\a"}); ValidateConcernProgress(bad, 10) == nil {
		t.Fatal("unprintable planner reason accepted")
	}
	if bad := (ConcernProgress{Concern: EnsureBasicPower, PlannerWaiting: true}); ValidateConcernProgress(bad, 10) == nil {
		t.Fatal("a wait with no text accepted")
	}
}

// A waiting goal reads as a wait, not a block, and a
// review that recomputes the record keeps the wait.
func TestWaitingGoalSaysWhatItWaitsOn(t *testing.T) {
	const text = "waiting for work it already started"
	c := ConcernProgressContract("", DefaultRoundsPolicy())
	p := ReviewConcernProgress(ConcernProgress{Concern: EnsureComfort, Method: "assess", Planner: text, PlannerWaiting: true}, EnsureComfort, c, ProgressEvidence{}, 10)
	if p.Blocked != BlockedWaiting(text) || p.Blocked.Actionable() || p.Blocked.Held() || !p.Blocked.Waiting() || ValidateConcernProgress(p, 10) != nil {
		t.Fatalf("%+v", p)
	}
}

func TestHoldProgressNamesIntentionalHolds(t *testing.T) {
	progress := []ConcernProgress{{Concern: EnsureResearch, Blocked: BlockedNoMethod}, {Concern: EnsureComfort, Blocked: BlockedNoMethod, Planner: PlannerOptOut}, {Concern: MaintainResource, Blocked: BlockedNoMethod}, {Concern: MaintainHousing, Blocked: BlockedNoWorker}}
	got := HoldProgress(progress, map[ConcernID]bool{EnsureResearch: true})
	want := []BlockedReason{HeldUnavailable, HeldOptIn, BlockedNoMethod, BlockedNoWorker}
	for i, w := range want {
		if got[i].Blocked != w || ValidateConcernProgress(got[i], 0) != nil {
			t.Fatal(got[i], w)
		}
	}
	if progress[0].Blocked != BlockedNoMethod {
		t.Fatal("input mutated")
	}
}
