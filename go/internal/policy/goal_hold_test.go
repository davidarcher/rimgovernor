package policy

import "testing"

func TestStatusRowsActionableFirstHeldCollapsed(t *testing.T) {
	rows := StatusRows(StatusInput{Progress: []GoalProgress{
		{Goal: EnsureComfort, Method: "assess", Blocked: HeldStage},
		{Goal: EnsureResearch, Method: "assess", Blocked: BlockedNoMethod},
		{Goal: MaintainResource, Method: "mine"},
		{Goal: EnsureBasicPower, Method: "assess", Planner: "insufficient_verified_space", Blocked: BlockedPlanner("insufficient_verified_space")},
		{Goal: EnsureFoodSupply, Method: "cook", Blocked: BlockedPrerequisite(EnsureCooking)},
		{Goal: MaintainLighting, Method: "assess", Blocked: HeldLabor(WorkConstruction)},
	}})
	var got []string
	for _, r := range rows {
		got = append(got, r.Text)
		if r.Key == "goal.EnsureResearch" && r.Severity != StatusWarning {
			t.Fatalf("actionable row not a warning: %+v", r)
		}
	}
	want := []string{
		"goal MaintainResource: mine",
		"EnsureResearch - no_method",
		"EnsureBasicPower: assess - planner:insufficient_verified_space",
		"MaintainResource: mine",
		"held EnsureComfort, EnsureFoodSupply, MaintainLighting",
	}
	if len(got) != len(want) {
		t.Fatalf("%q", got)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("%q, want %q", got, want)
		}
	}
	// Nothing worked: the top row is the first actionable goal, not a held one.
	rows = StatusRows(StatusInput{Progress: []GoalProgress{{Goal: EnsureComfort, Method: "assess", Blocked: HeldStage}, {Goal: EnsureResearch, Method: "assess", Blocked: BlockedNoMethod}}})
	if rows[0].Text != "goal EnsureResearch - blocked no_method" || rows[0].Severity != StatusWarning {
		t.Fatalf("%+v", rows[0])
	}
	rows = StatusRows(StatusInput{Progress: []GoalProgress{{Goal: EnsureComfort, Method: "assess", Blocked: HeldStage}}})
	if rows[0].Text != "goal EnsureComfort - held:stage" || rows[0].Severity != StatusInfo {
		t.Fatalf("%+v", rows[0])
	}
}

func TestPlannerRefusalNamesTheBlock(t *testing.T) {
	c := GoalProgressContract("", DefaultRoutinePolicy())
	p := ReviewGoalProgress(GoalProgress{Goal: EnsureBasicPower, Method: "assess", Planner: "insufficient_verified_space"}, EnsureBasicPower, c, ProgressEvidence{}, 10)
	if p.Blocked != BlockedPlanner("insufficient_verified_space") || !p.Blocked.Actionable() || ValidateGoalProgress(p, 10) != nil {
		t.Fatalf("%+v", p)
	}
	if bad := (GoalProgress{Goal: EnsureBasicPower, Planner: "has space"}); ValidateGoalProgress(bad, 10) == nil {
		t.Fatal("unprintable planner reason accepted")
	}
}

func TestHoldProgressNamesIntentionalHolds(t *testing.T) {
	progress := []GoalProgress{
		{Goal: EnsureComfort, Blocked: BlockedNoMethod},
		{Goal: EnsureResearch, Blocked: BlockedPlanner("x")},
		{Goal: MaintainLighting, Blocked: BlockedNoMethod},
		{Goal: MaintainRefrigeration, Blocked: BlockedNoMethod, Planner: PlannerOptOut},
		{Goal: MaintainFireSafety, Blocked: BlockedNoMethod},
		{Goal: EnsureExpansion, Blocked: BlockedNoMethod},
		{Goal: MaintainResource, Blocked: BlockedNoWorker},
		{Goal: MaintainStorage, Blocked: BlockedNoMethod},
	}
	rows := []DevelopmentRow{{Goal: EnsureComfort, Reason: DevelopmentStage}, {Goal: EnsureResearch, Reason: DevelopmentLabor, Bottleneck: WorkResearch}, {Goal: EnsureExpansion, Reason: DevelopmentCapacity}}
	got := HoldProgress(progress, rows, LaborProfile{WorkConstruction}, map[GoalID]bool{MaintainFireSafety: true})
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
