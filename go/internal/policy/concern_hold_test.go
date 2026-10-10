package policy

import (
	"strings"
	"testing"
)

func TestPlannerRefusalNamesTheBlock(t *testing.T) {
	c := ConcernProgressContract("", DefaultRoundsPolicy())
	p := ReviewConcernProgress(ConcernProgress{Concern: EnsureBasicPower, Method: "assess", Planner: CauseNoSpace, PlannerSubject: "power"}, EnsureBasicPower, c, ProgressEvidence{}, 10)
	if p.Blocked != BlockedReason(CauseNoSpace) || p.BlockedSubject() != "power" || !p.Blocked.Actionable() || ValidateConcernProgress(p, 10) != nil {
		t.Fatalf("%+v", p)
	}
	for name, bad := range map[string]ConcernProgress{
		"unknown cause":     {Concern: EnsureBasicPower, Planner: "bell\a"},
		"subject, no cause": {Concern: EnsureBasicPower, PlannerSubject: "power"},
		"long subject":      {Concern: EnsureBasicPower, Planner: CauseNoSpace, PlannerSubject: strings.Repeat("x", MaxSubjectLen+1)},
		"unprintable":       {Concern: EnsureBasicPower, Planner: CauseNoSpace, PlannerSubject: "pow\ner"},
		"unknown blocked":   {Concern: EnsureBasicPower, Blocked: "planner:no site"},
	} {
		if ValidateConcernProgress(bad, 10) == nil {
			t.Fatalf("%s accepted", name)
		}
	}
}

// A waiting goal reads as a wait, not a block, and a
// review that recomputes the record keeps the wait.
func TestWaitingGoalSaysWhatItWaitsOn(t *testing.T) {
	c := ConcernProgressContract("", DefaultRoundsPolicy())
	p := ReviewConcernProgress(ConcernProgress{Concern: EnsureComfort, Method: "assess", Planner: CauseExistingWork}, EnsureComfort, c, ProgressEvidence{}, 10)
	if p.Blocked != BlockedReason(CauseExistingWork) || p.Blocked.Actionable() || p.Blocked.Held() || !p.Blocked.Waiting() || ValidateConcernProgress(p, 10) != nil {
		t.Fatalf("%+v", p)
	}
}

func TestHoldProgressNamesIntentionalHolds(t *testing.T) {
	progress := []ConcernProgress{{Concern: EnsureResearch, Blocked: BlockedNoMethod}, {Concern: EnsureComfort, Blocked: BlockedNoMethod, Planner: CauseHeldOptIn}, {Concern: MaintainResource, Blocked: BlockedNoMethod}, {Concern: MaintainHousing, Blocked: BlockedNoWorker}}
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

func TestWaitingCauses(t *testing.T) {
	for _, c := range WaitCauses {
		if !c.Waiting() {
			t.Errorf("%q is a wait kind", c)
		}
	}
	if !CauseCombatOrders.Waiting() || !CauseHoldFallback.Waiting() || CauseNoSpace.Waiting() || CauseHeldOptIn.Waiting() {
		t.Error("waiting classification")
	}
}
