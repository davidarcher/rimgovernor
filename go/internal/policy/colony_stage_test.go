package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func stageFacts(shelter bool, food float64) ColonyStageFacts {
	return ColonyStageFacts{Shelter: domain.Known(shelter), FoodDays: domain.Known(food), ResourcesShort: domain.Known(false)}
}

// The stage climbs one step per review as the colony achieves each
// condition, names the first unmet condition of the next stage, and drops
// straight to Foothold when the food runway falls under the exit threshold.
func TestColonyStageTransitions(t *testing.T) {
	t.Parallel()
	p := DefaultRoutinePolicy().Stages()
	day := DevelopmentStallTicks
	blockedFood := stageFacts(true, 9)
	blockedFood.ProductionBlocked, blockedFood.Blocked = EnsureFoodSupply, BlockedNoWorker
	steps := []struct {
		name    string
		facts   ColonyStageFacts
		tick    domain.Tick
		stage   ColonyStage
		blocker StageBlocker
		held    bool
	}{
		{"no shelter", stageFacts(false, 10), 0, StageFoothold, StageBlockerShelter, true},
		{"shelter, starving", stageFacts(true, 2), day, StageFoothold, StageBlockerStarvation, false},
		{"shelter, short of the reserve", stageFacts(true, 5), 2 * day, StageFoothold, StageBlockerFood, false},
		{"wood short", ColonyStageFacts{Shelter: domain.Known(true), FoodDays: domain.Known(8.0), WoodShort: true, ResourcesShort: domain.Known(false)}, 3 * day, StageFoothold, StageBlockerWood, false},
		{"resources unknown", ColonyStageFacts{Shelter: domain.Known(true), FoodDays: domain.Known(8.0)}, 4 * day, StageFoothold, StageBlockerUnknown, false},
		{"reserves reached", stageFacts(true, 8), 5 * day, StageReserves, StageBlockerSettling, false},
		{"production clear one day", stageFacts(true, 8), 6 * day, StageReserves, StageBlockerSettling, false},
		{"production clear two days", stageFacts(true, 8), 7 * day, StageStable, StageBlockerSettling, false},
		{"stable, food short of development", stageFacts(true, 9), 10 * day, StageStable, StageBlockerFood, false},
		{"development", stageFacts(true, 15), 11 * day, StageDevelopment, "", false},
		{"food under the development exit", stageFacts(true, 6), 12 * day, StageStable, StageBlockerFood, false},
		{"food back over the development exit stays stable", stageFacts(true, 8), 13 * day, StageStable, StageBlockerSettling, false},
		{"production blocked for under a day keeps stable", blockedFood, 14 * day, StageStable, StageBlockerSettling, false},
		{"production blocked a day drops to reserves", blockedFood, 15 * day, StageReserves, StageBlockerProduction, false},
		{"starving drops to foothold", stageFacts(true, 2), 16 * day, StageFoothold, StageBlockerStarvation, false},
		{"unknown facts keep the record", ColonyStageFacts{Shelter: domain.Unknown[bool](), FoodDays: domain.Unknown[float64]()}, 17 * day, StageFoothold, StageBlockerUnknown, false},
	}
	var r ColonyStageRecord
	for _, step := range steps {
		r = ReviewColonyStage(r, step.facts, p, step.tick)
		if r.Stage != step.stage || r.Blocker != step.blocker || r.Held != step.held || r.Reason == "" != (step.blocker == "") {
			t.Fatalf("%s: %+v", step.name, r)
		}
		if err := ValidateColonyStage(r, step.tick); err != nil {
			t.Fatal(step.name, err)
		}
	}
}

// A colony whose food runway oscillates around the Reserves threshold keeps
// its stage: Reserves is entered at the target and only left under the
// foothold days, so a runway swinging between them flips nothing.
func TestColonyStageHysteresisUnderOscillation(t *testing.T) {
	t.Parallel()
	p := DefaultRoutinePolicy().Stages()
	var r ColonyStageRecord
	r = ReviewColonyStage(r, stageFacts(true, 7.5), p, 100)
	if r.Stage != StageReserves {
		t.Fatal(r)
	}
	changes := 0
	for i, food := range []float64{6.9, 7.1, 6.5, 7.4, 4.0, 7.0, 3.5, 6.8} {
		before := r.Stage
		r = ReviewColonyStage(r, stageFacts(true, food), p, domain.Tick(200+i*100))
		if r.Stage != before {
			changes++
		}
	}
	if changes != 0 || r.Stage != StageReserves {
		t.Fatalf("stage flipped %d times ending at %s", changes, r.Stage)
	}
	// Under the exit threshold it drops, and climbing back needs the
	// enter threshold again, not the exit one.
	r = ReviewColonyStage(r, stageFacts(true, 2.9), p, 1000)
	if r.Stage != StageFoothold || r.Blocker != StageBlockerStarvation {
		t.Fatal(r)
	}
	r = ReviewColonyStage(r, stageFacts(true, 5), p, 1100)
	if r.Stage != StageFoothold || r.Blocker != StageBlockerFood {
		t.Fatal(r)
	}
	r = ReviewColonyStage(r, stageFacts(true, 7), p, 1200)
	if r.Stage != StageReserves || r.Since != 1200 {
		t.Fatal(r)
	}
	// The same at the Stable boundary: production blocked and unblocked
	// under a day at a time never drops Stable.
	r = ReviewColonyStage(r, stageFacts(true, 8), p, 1200+2*DevelopmentStallTicks)
	if r.Stage != StageStable {
		t.Fatal(r)
	}
	for i := 0; i < 6; i++ {
		f := stageFacts(true, 8)
		if i%2 == 0 {
			f.ProductionBlocked, f.Blocked = MaintainWood, BlockedNoWorker
		}
		r = ReviewColonyStage(r, f, p, 1200+2*DevelopmentStallTicks+domain.Tick(i+1)*DevelopmentStallTicks/2)
		if r.Stage != StageStable {
			t.Fatal(i, r)
		}
	}
}

// The Foothold hold follows the last known shelter gate and holds only the
// comfort-class development; a tick rewind resets the record.
func TestColonyStageHoldAndReset(t *testing.T) {
	t.Parallel()
	p := DefaultRoutinePolicy().Stages()
	r := ReviewColonyStage(ColonyStageRecord{}, ColonyStageFacts{Shelter: domain.Known(false)}, p, 10)
	if !r.HoldsDevelopment() {
		t.Fatal(r)
	}
	r = ReviewColonyStage(r, ColonyStageFacts{Shelter: domain.Unknown[bool]()}, p, 20)
	if !r.HoldsDevelopment() || r.Reason != "shelter unknown" {
		t.Fatal(r)
	}
	r = ReviewColonyStage(r, ColonyStageFacts{Shelter: domain.Known(true)}, p, 30)
	if r.HoldsDevelopment() || r.Blocker != StageBlockerUnknown {
		t.Fatal(r)
	}
	rewound := ReviewColonyStage(ColonyStageRecord{Stage: StageStable, Since: 500, Held: true}, ColonyStageFacts{}, p, 40)
	if rewound.Stage != StageFoothold || rewound.Since != 40 || rewound.Held {
		t.Fatal(rewound)
	}
	for _, id := range []GoalID{EnsureComfort, MaintainStoneShell, MaintainHomeCoverage} {
		if !StageDevelopmentGoal(id) {
			t.Fatal(id)
		}
	}
	for _, id := range []GoalID{EnsureResearch, EnsureExpansion, MaintainResource, MaintainWood, EnsureBasicDefense} {
		if StageDevelopmentGoal(id) {
			t.Fatal(id)
		}
	}
	if err := ValidateColonyStage(ColonyStageRecord{Stage: 7}, 0); err == nil {
		t.Fatal("invalid stage accepted")
	}
	if err := ValidateColonyStage(ColonyStageRecord{Since: 5}, 4); err == nil {
		t.Fatal("future record accepted")
	}
}

// ProductionBlockedGoal reports native blockers on the production goals
// only: a goal between methods or reconciling a write is not stalled.
func TestProductionBlockedGoal(t *testing.T) {
	t.Parallel()
	records := []GoalProgress{{Goal: EnsureComfort, Blocked: BlockedNoWorker}, {Goal: EnsureFoodSupply, Blocked: BlockedNoMethod}, {Goal: MaintainWood, Blocked: BlockedReconciling}}
	if goal, _ := ProductionBlockedGoal(records); goal != "" {
		t.Fatal(goal)
	}
	records = append(records, GoalProgress{Goal: MaintainResource, Blocked: BlockedCooldown}, GoalProgress{Goal: EnsureCooking, Blocked: BlockedPrerequisite(EnsureInitialShelter)})
	if goal, reason := ProductionBlockedGoal(records); goal != EnsureCooking || reason.Prerequisite() != EnsureInitialShelter {
		t.Fatal(goal, reason)
	}
}

// The stage sets the budgets: the project limit, the ladder's pace and the
// reserve targets, each within the policy's own bounds.
func TestStageRoutinePolicyBudgets(t *testing.T) {
	t.Parallel()
	base := DefaultRoutinePolicy()
	base.SetProjectLimit(2)
	for _, tc := range []struct {
		stage   ColonyStage
		limit   int
		rungs   int
		reserve float64
		wood    int64
		stall   int64
	}{
		{StageFoothold, 2, 2, 5, 350, base.GoalStallTicks / 24},
		{StageReserves, 2, 5, 5, 350, base.GoalStallTicks},
		{StageStable, 2, 8, 7.5, 525, base.GoalStallTicks},
		{StageDevelopment, 3, len(DefaultResearchLadder()), 10, 700, base.GoalStallTicks},
	} {
		p := StageRoutinePolicy(base, tc.stage)
		if p.MaxDevelopmentProjects != tc.limit || len(p.ResearchLadder) != tc.rungs || p.FoodReserveDays != tc.reserve || p.WoodTarget != tc.wood || p.WoodMax < p.WoodTarget || p.GoalStallTicks != tc.stall {
			t.Fatalf("%s: limit %d rungs %d reserve %v wood %d/%d stall %d", tc.stage, p.MaxDevelopmentProjects, len(p.ResearchLadder), p.FoodReserveDays, p.WoodTarget, p.WoodMax, p.GoalStallTicks)
		}
		if err := p.Validate(); err != nil {
			t.Fatal(tc.stage, err)
		}
	}
	if StageDevelopmentLimit(StageFoothold, 1) != 1 || StageDevelopmentLimit(StageDevelopment, 8) != 8 {
		t.Fatal("limit bounds")
	}
	wide := base
	wide.FoodReserveDays = 40
	if StageRoutinePolicy(wide, StageDevelopment).FoodReserveDays != 60 {
		t.Fatal("reserve days cap")
	}
	if got := StageResearchLadder(StageFoothold, []string{"A"}); len(got) != 1 {
		t.Fatal(got)
	}
	bad := base
	bad.Stage = ColonyStagePolicy{ReserveEnterDays: 5, ReserveExitDays: 6}
	if err := bad.Validate(); err == nil {
		t.Fatal("unordered thresholds accepted")
	}
}

// The ranking refuses the comfort-class development under the Foothold
// hold and admits it once the stage no longer holds.
func TestRankDevelopmentStageHold(t *testing.T) {
	t.Parallel()
	goals := []DevelopmentGoal{
		{ID: MaintainStoneShell, Source: AutopilotGoal, Priority: 4, Deficit: domain.Known(1.0), Labor: LaborProfile{WorkConstruction}},
		{ID: MaintainWood, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(0.5), Labor: LaborProfile{WorkPlantCutting}},
	}
	request := DevelopmentRequest{Snapshot: domain.GenerationSnapshot{Colony: "c", Load: "l", Plan: "p"}, Tick: 7, Workers: domain.Known(2), Limit: 2, Goals: goals, Stage: ColonyStageRecord{Stage: StageFoothold, Held: true}}
	held, err := RankDevelopment(request)
	if err != nil {
		t.Fatal(err)
	}
	for _, row := range held.Rows {
		switch row.Goal {
		case MaintainStoneShell:
			if row.Selected || row.Reason != DevelopmentStage {
				t.Fatal(row)
			}
		case MaintainWood:
			if !row.Selected {
				t.Fatal(row)
			}
		}
	}
	request.Stage = ColonyStageRecord{Stage: StageDevelopment}
	open, err := RankDevelopment(request)
	if err != nil {
		t.Fatal(err)
	}
	for _, row := range open.Rows {
		if !row.Selected {
			t.Fatal(row)
		}
	}
}
