package policy

import (
	"testing"
)

func TestPlannerRefusalNamesTheBlock(t *testing.T) {
	c := GoalProgressContract("", DefaultRoutinePolicy())
	p := ReviewGoalProgress(GoalProgress{Goal: EnsureBasicPower, Method: "assess", Planner: "insufficient_verified_space"}, EnsureBasicPower, c, ProgressEvidence{}, 10)
	if p.Blocked != BlockedPlanner("insufficient_verified_space") || !p.Blocked.Actionable() || ValidateGoalProgress(p, 10) != nil {
		t.Fatalf("%+v", p)
	}
	if bad := (GoalProgress{Goal: EnsureBasicPower, Planner: "bell\a"}); ValidateGoalProgress(bad, 10) == nil {
		t.Fatal("unprintable planner reason accepted")
	}
	if bad := (GoalProgress{Goal: EnsureBasicPower, PlannerWaiting: true}); ValidateGoalProgress(bad, 10) == nil {
		t.Fatal("a wait with no text accepted")
	}
}

// A waiting goal reads as a wait, not a block, and a
// review that recomputes the record keeps the wait.
func TestWaitingGoalSaysWhatItWaitsOn(t *testing.T) {
	const text = "waiting for work it already started"
	c := GoalProgressContract("", DefaultRoutinePolicy())
	p := ReviewGoalProgress(GoalProgress{Goal: EnsureComfort, Method: "assess", Planner: text, PlannerWaiting: true}, EnsureComfort, c, ProgressEvidence{}, 10)
	if p.Blocked != BlockedWaiting(text) || p.Blocked.Actionable() || p.Blocked.Held() || !p.Blocked.Waiting() || ValidateGoalProgress(p, 10) != nil {
		t.Fatalf("%+v", p)
	}
}

func TestHoldProgressNamesIntentionalHolds(t *testing.T) {
	progress := []GoalProgress{
		{Goal: EnsureComfort, Blocked: BlockedNoMethod},
		{Goal: EnsureResearch, Blocked: BlockedPlanner("x")},
		{Goal: MaintainLighting, Blocked: BlockedNoMethod},
		{Goal: MaintainRefrigeration, Blocked: BlockedNoMethod, Planner: PlannerOptOut},
		{Goal: MaintainFireSafety, Blocked: BlockedNoMethod},
		{Goal: MaintainHousing, Blocked: BlockedNoMethod},
		{Goal: MaintainResource, Blocked: BlockedNoWorker},
		{Goal: ManagePollution, Blocked: BlockedNoMethod},
	}
	rows := []DevelopmentRow{{Goal: EnsureComfort, Reason: DevelopmentStage}, {Goal: EnsureResearch, Reason: DevelopmentLabor, Bottleneck: WorkResearch}, {Goal: MaintainHousing, Reason: DevelopmentCapacity}}
	got := HoldProgress(progress, rows, LaborProfile{WorkConstruction}, map[ConcernID]bool{MaintainFireSafety: true})
	want := []BlockedReason{HeldStage, HeldLabor(WorkResearch), HeldLabor(WorkConstruction), HeldOptIn, HeldUnavailable, HeldCapacity, BlockedNoWorker, BlockedNoMethod}
	for i, w := range want {
		if got[i].Blocked != w || ValidateGoalProgress(got[i], 0) != nil {
			t.Fatalf("%s = %q, want %q", got[i].Goal, got[i].Blocked, w)
		}
		if w.Held() == w.Actionable() {
			t.Fatalf("%q held and actionable", w)
		}
	}
	if progress[0].Blocked != BlockedNoMethod {
		t.Fatal("input mutated")
	}
}
