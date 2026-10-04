package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// stageFacts meets every exit criterion but the shelter and food runway
// given.
func stageFacts(shelter bool, food float64) ColonyStageFacts {
	yes := domain.Known(true)
	return ColonyStageFacts{Shelter: domain.Known(shelter), FoodDays: domain.Known(food), Cooking: yes, FoodStorage: yes, Armed: yes, FieldSown: yes, ResearchBench: yes, Power: yes, Climate: yes, Doctor: yes}
}

func withFact(f ColonyStageFacts, set func(*ColonyStageFacts)) ColonyStageFacts {
	set(&f)
	return f
}

// The stage climbs one step per review once every exit criterion of the
// current stage holds, names the first unmet one, and drops straight to
// Foothold when the food runway falls under the exit threshold.
func TestColonyStageTransitions(t *testing.T) {
	t.Parallel()
	p := DefaultRoundsPolicy().Stages()
	day := DevelopmentStallTicks
	no := domain.Known(false)
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
		{"shelter, starving", stageFacts(true, 2), day / 4, StageFoothold, StageBlockerStarvation, false},
		{"no cooking bill", withFact(stageFacts(true, 5), func(f *ColonyStageFacts) { f.Cooking = no }), day / 2, StageFoothold, StageBlockerCooking, false},
		{"no food stockpile", withFact(stageFacts(true, 5), func(f *ColonyStageFacts) { f.FoodStorage = no }), day, StageFoothold, StageBlockerFoodStorage, false},
		{"unarmed", withFact(stageFacts(true, 5), func(f *ColonyStageFacts) { f.Armed = no }), 2 * day, StageFoothold, StageBlockerDefense, false},
		{"foothold met", stageFacts(true, 5), 3 * day, StageReserves, StageBlockerFood, false},
		{"field unsown", withFact(stageFacts(true, 8), func(f *ColonyStageFacts) { f.FieldSown = no }), 4 * day, StageReserves, StageBlockerField, false},
		{"wood short", withFact(stageFacts(true, 8), func(f *ColonyStageFacts) { f.WoodShort = true }), 5 * day, StageReserves, StageBlockerWood, false},
		{"bench unknown", withFact(stageFacts(true, 8), func(f *ColonyStageFacts) { f.ResearchBench = domain.Unknown[bool]() }), 6 * day, StageReserves, StageBlockerUnknown, false},
		{"reserves met", stageFacts(true, 8), 7 * day, StageStable, StageBlockerSettling, false},
		{"power out", withFact(stageFacts(true, 8), func(f *ColonyStageFacts) { f.Power = no }), 8 * day, StageStable, StageBlockerPower, false},
		{"no doctor", withFact(stageFacts(true, 8), func(f *ColonyStageFacts) { f.Doctor = no }), 9 * day, StageStable, StageBlockerDoctor, false},
		{"stable, food short of development", stageFacts(true, 9), 10 * day, StageStable, StageBlockerFood, false},
		{"development", stageFacts(true, 15), 11 * day, StageDevelopment, "", false},
		{"food under the development exit", stageFacts(true, 6), 12 * day, StageStable, StageBlockerFood, false},
		{"food back over the development exit stays stable", stageFacts(true, 8), 13 * day, StageStable, StageBlockerSettling, false},
		{"production blocked for under a day keeps stable", blockedFood, 14 * day, StageStable, StageBlockerProduction, false},
		{"production blocked a day drops to reserves", blockedFood, 15 * day, StageReserves, StageBlockerProduction, false},
		{"starving drops to foothold", stageFacts(true, 1.5), 16 * day, StageFoothold, StageBlockerStarvation, false},
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

// The season picks the climate criterion: refrigeration in spring and
// summer, sleeping warmth in fall and winter, both when unknown.
func TestSeasonalClimate(t *testing.T) {
	t.Parallel()
	warm, cold := domain.Known(true), domain.Known(false)
	summer, winter := domain.Known(Calendar{Season: "Summer"}), domain.Known(Calendar{Season: "Winter"})
	if v, _ := SeasonalClimate(summer, cold, false).Value(); !v {
		t.Fatal("summer ignores sleeping warmth")
	}
	if v, _ := SeasonalClimate(summer, warm, true).Value(); v {
		t.Fatal("summer needs refrigeration")
	}
	if v, _ := SeasonalClimate(winter, cold, false).Value(); v {
		t.Fatal("winter needs warmth")
	}
	if v, _ := SeasonalClimate(domain.Unknown[Calendar](), warm, true).Value(); v {
		t.Fatal("unknown season asks both")
	}
}

// A colony whose food runway oscillates around the Reserves threshold keeps
// its stage: Reserves is entered at the target and only left under the
// foothold days, so a runway swinging between them flips nothing.
func TestColonyStageHysteresisUnderOscillation(t *testing.T) {
	t.Parallel()
	p := DefaultRoundsPolicy().Stages()
	// An unsown field keeps the colony at Reserves while food swings.
	unsown := func(food float64) ColonyStageFacts {
		return withFact(stageFacts(true, food), func(f *ColonyStageFacts) { f.FieldSown = domain.Known(false) })
	}
	var r ColonyStageRecord
	r = ReviewColonyStage(r, unsown(3.5), p, 100)
	if r.Stage != StageReserves {
		t.Fatal(r)
	}
	changes := 0
	for i, food := range []float64{2.9, 3.1, 2.5, 7.4, 2.1, 7.0, 2.0, 6.8} {
		before := r.Stage
		r = ReviewColonyStage(r, unsown(food), p, domain.Tick(200+i*100))
		if r.Stage != before {
			changes++
		}
	}
	if changes != 0 || r.Stage != StageReserves {
		t.Fatalf("stage flipped %d times ending at %s", changes, r.Stage)
	}
	// Under the exit threshold it drops, and climbing back needs the
	// Foothold exit runway again, not the drop one.
	r = ReviewColonyStage(r, unsown(1.9), p, 1000)
	if r.Stage != StageFoothold || r.Blocker != StageBlockerStarvation {
		t.Fatal(r)
	}
	r = ReviewColonyStage(r, unsown(2.5), p, 1100)
	if r.Stage != StageFoothold || r.Blocker != StageBlockerStarvation {
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
			f.ProductionBlocked, f.Blocked = MaintainResource, BlockedNoWorker
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
	p := DefaultRoundsPolicy().Stages()
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
	records := []ConcernProgress{{Concern: EnsureComfort, Blocked: BlockedNoWorker}, {Concern: EnsureFoodSupply, Blocked: BlockedNoMethod}, {Concern: MaintainResource, Blocked: BlockedReconciling}}
	if goal, _ := ProductionBlockedConcern(records); goal != "" {
		t.Fatal(goal)
	}
	records = append(records, ConcernProgress{Concern: MaintainResource, Blocked: BlockedCooldown}, ConcernProgress{Concern: EnsureCooking, Blocked: BlockedPrerequisite(MaintainHousing)})
	if goal, reason := ProductionBlockedConcern(records); goal != EnsureCooking || reason.Prerequisite() != MaintainHousing {
		t.Fatal(goal, reason)
	}
}

// The stage sets the budgets: the ladder's pace and the stall deadline;
// the reserve targets stay as configured at every stage.
func TestStageRoundsPolicyBudgets(t *testing.T) {
	t.Parallel()
	base := DefaultRoundsPolicy()
	for _, tc := range []struct {
		stage   ColonyStage
		rungs   int
		reserve float64
		wood    int64
		stall   int64
	}{
		{StageFoothold, 2, 5, 350, base.ConcernStallTicks / 4},
		{StageReserves, 5, 5, 350, base.ConcernStallTicks},
		{StageStable, 8, 5, 350, base.ConcernStallTicks},
		{StageDevelopment, len(DefaultResearchLadder()), 5, 350, base.ConcernStallTicks},
	} {
		p := StageRoundsPolicy(base, tc.stage)
		if len(p.ResearchLadder) != tc.rungs || p.FoodReserveDays != tc.reserve || p.WoodTarget != tc.wood || p.WoodMax < p.WoodTarget || p.ConcernStallTicks != tc.stall {
			t.Fatalf("%s: rungs %d reserve %v wood %d/%d stall %d", tc.stage, len(p.ResearchLadder), p.FoodReserveDays, p.WoodTarget, p.WoodMax, p.ConcernStallTicks)
		}
		if err := p.Validate(); err != nil {
			t.Fatal(tc.stage, err)
		}
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

// A goal before its stage is not raised (Staged: it waits, visible, with no slot); the stage's exceptions
// raise it early (the wood floor, a full spoiling emergency) or later (the
// stone shell without Stonecutting, animal goals with no tame animal).
func TestRaisedAtStage(t *testing.T) {
	t.Parallel()
	ids := func(goals []DevelopmentConcern) map[ConcernID]bool {
		out := map[ConcernID]bool{}
		for _, g := range goals {
			out[g.ID] = !g.Staged
		}
		return out
	}
	raised := func(goals []DevelopmentConcern) map[ConcernID]bool {
		out := map[ConcernID]bool{}
		for id, ok := range ids(goals) {
			if ok {
				out[id] = true
			}
		}
		return out
	}
	all := func() []DevelopmentConcern {
		var goals []DevelopmentConcern
		for _, id := range []ConcernID{CriticalMedicine, EnsureFoodSupply, EnsureResearch, MaintainResource, MaintainStoneShell, MaintainRefrigeration, MaintainHerd, MaintainFlooring, MaintainLighting} {
			goals = append(goals, DevelopmentConcern{ID: id, Deficit: domain.Known(0.5)})
		}
		return goals
	}
	p := DefaultRoundsPolicy()
	f := RoundsFacts{Research: domain.Known(ResearchFacts{}), AnimalUpkeep: AnimalUpkeepObservation{Animals: domain.Known([]UpkeepAnimal{})}}
	p.ColonyStage = StageFoothold
	got := raised(raisedAtStage(all(), f, p, RoundsLatches{}))
	if !got[CriticalMedicine] || !got[EnsureFoodSupply] || len(got) != 2 {
		t.Fatalf("foothold raised %v", got)
	}
	spoiling := all()
	spoiling[5].Deficit = domain.Known(1.0)
	if got := raised(raisedAtStage(spoiling, f, p, RoundsLatches{Wood: true})); !got[MaintainResource] || !got[MaintainRefrigeration] {
		t.Fatalf("emergencies not raised early: %v", got)
	}
	p.ColonyStage = StageReserves
	if got := raised(raisedAtStage(all(), f, p, RoundsLatches{})); !got[EnsureResearch] || !got[MaintainResource] || got[MaintainRefrigeration] {
		t.Fatalf("reserves raised %v", got)
	}
	p.ColonyStage = StageStable
	if got := raised(raisedAtStage(all(), f, p, RoundsLatches{})); got[MaintainStoneShell] || got[MaintainHerd] || !got[MaintainRefrigeration] || got[MaintainFlooring] {
		t.Fatalf("stable raised %v", got)
	}
	f.Research = domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Stonecutting"}})
	p.ColonyStage = StageDevelopment
	if got := raised(raisedAtStage(all(), f, p, RoundsLatches{})); !got[MaintainStoneShell] || got[MaintainHerd] || !got[MaintainFlooring] || !got[MaintainLighting] {
		t.Fatalf("development raised %v", got)
	}
}
