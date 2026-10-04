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
	progress := []ConcernProgress{
		{Concern: EnsureComfort, Blocked: BlockedNoMethod},
		{Concern: EnsureResearch, Blocked: BlockedPlanner("x")},
		{Concern: MaintainLighting, Blocked: BlockedNoMethod},
		{Concern: MaintainRefrigeration, Blocked: BlockedNoMethod, Planner: PlannerOptOut},
		{Concern: MaintainFireSafety, Blocked: BlockedNoMethod},
		{Concern: MaintainHousing, Blocked: BlockedNoMethod},
		{Concern: MaintainResource, Blocked: BlockedNoWorker},
		{Concern: ManagePollution, Blocked: BlockedNoMethod},
	}
	rows := []DevelopmentRow{{Concern: EnsureComfort, Reason: DevelopmentStage}, {Concern: EnsureResearch, Reason: DevelopmentLabor, Bottleneck: WorkResearch}, {Concern: MaintainHousing, Reason: DevelopmentCapacity}}
	got := HoldProgress(progress, rows, LaborProfile{WorkConstruction}, map[ConcernID]bool{MaintainFireSafety: true})
	want := []BlockedReason{HeldStage, HeldLabor(WorkResearch), HeldLabor(WorkConstruction), HeldOptIn, HeldUnavailable, HeldCapacity, BlockedNoWorker, BlockedNoMethod}
	for i, w := range want {
		if got[i].Blocked != w || ValidateConcernProgress(got[i], 0) != nil {
			t.Fatalf("%s = %q, want %q", got[i].Concern, got[i].Blocked, w)
		}
		if w.Held() == w.Actionable() {
			t.Fatalf("%q held and actionable", w)
		}
	}
	if progress[0].Blocked != BlockedNoMethod {
		t.Fatal("input mutated")
	}
}
