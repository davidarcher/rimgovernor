package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// developmentSim replays RankDevelopment across many reviews. Selected goals
// commit for a fixed number of ticks (dispatched building progress) and then
// complete, so the simulation checks bounded admission and starvation
// resistance of the ordering itself: no pawn progress is modelled.
type developmentSim struct {
	t        *testing.T
	snapshot domain.GenerationSnapshot
	tick     domain.Tick
	step     domain.Tick
	workers  domain.Fact[int]
	labor    domain.Fact[map[WorkType]int]
	limit    int
	goals    []DevelopmentGoal
	// open holds committed work: goal -> tick it completes.
	open     map[GoalID]domain.Tick
	duration domain.Tick
	player   []Commitment
	state    DevelopmentState
	// wait counts consecutive reviews each goal was eligible but not selected.
	wait, maxWait map[GoalID]int
	selections    map[GoalID]int
}

func newDevelopmentSim(t *testing.T, limit int, goals ...DevelopmentGoal) *developmentSim {
	return &developmentSim{t: t, snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: "plan"}, tick: 100, step: 2500, workers: domain.Known(4), limit: limit, goals: goals, open: map[GoalID]domain.Tick{}, duration: 5000, wait: map[GoalID]int{}, maxWait: map[GoalID]int{}, selections: map[GoalID]int{}}
}

func (s *developmentSim) commitment(goal GoalID, source GoalSource, priority int, dispatched bool) Commitment {
	s.t.Helper()
	b, err := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 1}, domain.North, "WoodLog")
	if err != nil {
		s.t.Fatal(err)
	}
	a, err := domain.NewBuildingAction(domain.ActionID(string(goal)+"-action"), b)
	if err != nil {
		s.t.Fatal(err)
	}
	p, err := domain.NewPlan(s.snapshot.Plan, 0, []domain.Action{a})
	if err != nil {
		s.t.Fatal(err)
	}
	progress, err := domain.NewProgress(p, a.ID())
	if err != nil {
		s.t.Fatal(err)
	}
	if dispatched {
		if progress, err = progress.Prepare(s.snapshot, s.tick); err != nil {
			s.t.Fatal(err)
		}
		if progress, err = progress.MarkDispatched(s.snapshot, s.tick); err != nil {
			s.t.Fatal(err)
		}
	}
	profile := GoalLabor(goal)
	if source == PlayerGoal {
		profile = LaborProfile{WorkConstruction}
	}
	return Commitment{Goal: goal, Source: source, Priority: priority, Progress: progress, Labor: profile}
}

// review runs one ranking, commits every selected goal and completes work
// whose duration has elapsed.
func (s *developmentSim) review() DevelopmentState {
	s.t.Helper()
	for goal, done := range s.open {
		if s.tick >= done {
			delete(s.open, goal)
		}
	}
	var commitments []Commitment
	for goal := range s.open {
		commitments = append(commitments, s.commitment(goal, AutopilotGoal, 4, true))
	}
	commitments = append(commitments, s.player...)
	state, err := RankDevelopment(DevelopmentRequest{Snapshot: s.snapshot, Tick: s.tick, Workers: s.workers, Labor: s.labor, Limit: s.limit, Goals: s.goals, Commitments: commitments, Previous: s.state})
	if err != nil {
		s.t.Fatal(err)
	}
	if err := ValidateDevelopmentState(state); err != nil {
		s.t.Fatal(err)
	}
	for _, row := range state.Rows {
		switch {
		case row.Selected:
			s.selections[row.Goal]++
			s.wait[row.Goal] = 0
			s.open[row.Goal] = s.tick + s.duration
		case row.Reason == DevelopmentCapacity || row.Reason == DevelopmentLabor:
			s.wait[row.Goal]++
			s.maxWait[row.Goal] = max(s.maxWait[row.Goal], s.wait[row.Goal])
		default:
			s.wait[row.Goal] = 0
		}
	}
	s.state = state
	s.tick += s.step
	return state
}

func (s *developmentSim) run(reviews int) {
	for i := 0; i < reviews; i++ {
		s.review()
	}
}

func (s *developmentSim) row(state DevelopmentState, id GoalID) DevelopmentRow {
	s.t.Helper()
	for _, row := range state.Rows {
		if row.Goal == id {
			return row
		}
	}
	s.t.Fatal("missing row", id)
	return DevelopmentRow{}
}

func simGoal(id GoalID, deficit float64, labor LaborProfile) DevelopmentGoal {
	return DevelopmentGoal{ID: id, Source: AutopilotGoal, Priority: 4, Deficit: domain.Known(deficit), Labor: labor}
}

// Four competing needs on one slot: the smallest constant deficit must still
// be admitted within a bounded number of reviews, and no goal monopolises
// the slot through hysteresis once it has completed its work.
func TestDevelopmentSimulationNoStarvationUnderCompetition(t *testing.T) {
	s := newDevelopmentSim(t, 1,
		simGoal("comfort", 0.9, GoalLabor(EnsureComfort)),
		simGoal("expansion", 0.7, GoalLabor(EnsureExpansion)),
		simGoal("research", 0.5, GoalLabor(EnsureResearch)),
		simGoal("resource", 0.1, GoalLabor(MaintainResource)),
	)
	s.run(60)
	for _, g := range s.goals {
		if s.selections[g.ID] == 0 {
			t.Fatal("starved", g.ID, s.selections)
		}
	}
	// Calibration: one age point per 1,000 ticks means a persistent 0.8
	// deficit gap (80 points) is overtaken after 80,000 ticks, 32 reviews at
	// this cadence, plus the two reviews the incumbent's job holds the slot.
	if s.maxWait["resource"] > 36 {
		t.Fatal("resource waited too long", s.maxWait)
	}
	if s.selections["comfort"] < s.selections["resource"] {
		t.Fatal("deficit ordering lost", s.selections)
	}
}

// Capacity loss retains accepted work and resumes admission on recovery
// without rewriting waiting age.
func TestDevelopmentSimulationCapacityLossAndRecovery(t *testing.T) {
	s := newDevelopmentSim(t, 2,
		simGoal("comfort", 0.8, GoalLabor(EnsureComfort)),
		simGoal("research", 0.6, GoalLabor(EnsureResearch)),
		simGoal("wood", 0.4, GoalLabor(MaintainWood)),
	)
	s.duration = 20000
	first := s.review()
	requireSelected(t, first, "comfort", "research")
	woodSince := s.row(first, "wood").WaitingSince
	s.workers = domain.Known(0)
	lost := s.review()
	if len(lost.Committed) != 2 || lost.Capacity != 0 || selected(lost) != nil {
		t.Fatal(lost)
	}
	if row := s.row(lost, "wood"); row.Reason != DevelopmentNoWorkers || row.WaitingSince != woodSince {
		t.Fatal(row)
	}
	s.workers = domain.Unknown[int]()
	unknown := s.review()
	if unknown.Capacity != 0 || selected(unknown) != nil || len(unknown.Committed) != 2 {
		t.Fatal(unknown)
	}
	s.workers = domain.Known(4)
	s.open = map[GoalID]domain.Tick{}
	recovered := s.review()
	requireSelected(t, recovered, "comfort", "research")
	if row := s.row(recovered, "wood"); row.WaitingSince != woodSince || row.Reason != DevelopmentCapacity {
		t.Fatal("waiting age lost across capacity loss", row)
	}
	// With both slots cycling between the larger deficits, wood's 0.4 gap
	// plus the incumbents' hysteresis is at most 60 age points: admitted
	// within 60,000 ticks (24 reviews) after recovery.
	s.duration = 5000
	s.run(30)
	if s.selections["wood"] == 0 || s.maxWait["wood"] > 28 {
		t.Fatal("wood starved after recovery", s.selections, s.maxWait)
	}
}

// A player project pre-empts optional capacity while open; its completion
// returns the slot with routine waiting ages intact. An uncertain player
// write keeps the slot until observed absent.
func TestDevelopmentSimulationPlayerInterruptionAndUncertainWrite(t *testing.T) {
	s := newDevelopmentSim(t, 1,
		simGoal("comfort", 0.8, GoalLabor(EnsureComfort)),
		simGoal("research", 0.2, GoalLabor(EnsureResearch)),
	)
	requireSelected(t, s.review(), "comfort")
	s.open = map[GoalID]domain.Tick{}
	s.player = []Commitment{s.commitment("player-room", PlayerGoal, 2, true)}
	interrupted := s.review()
	if selected(interrupted) != nil || interrupted.Committed[0] != "player-room" {
		t.Fatal(interrupted)
	}
	researchSince := s.row(interrupted, "research").WaitingSince
	cancelled, err := s.player[0].Progress.Cancel()
	if err != nil {
		t.Fatal(err)
	}
	s.player[0].Progress = cancelled
	uncertain := s.review()
	if selected(uncertain) != nil || s.row(uncertain, "research").Reason != DevelopmentCapacity {
		t.Fatal("cancelled uncertain write released its slot", uncertain)
	}
	observed, err := cancelled.Observe(domain.Observation{Action: "player-room-action", Attempt: 1, Snapshot: s.snapshot, Tick: s.tick, Effect: domain.EffectAbsent}, s.snapshot)
	if err != nil {
		t.Fatal(err)
	}
	s.player[0].Progress = observed
	released := s.review()
	if len(released.Committed) != 0 || len(selected(released)) != 1 {
		t.Fatal(released)
	}
	if row := s.row(released, "research"); row.WaitingSince != researchSince {
		t.Fatal("player interruption rewrote waiting age", row)
	}
	// A 0.6 deficit gap is 60 age points: research is admitted within
	// 60,000 ticks (24 reviews) of a persistently larger comfort deficit.
	s.player = nil
	s.run(30)
	if s.selections["research"] == 0 || s.maxWait["research"] > 28 {
		t.Fatal("research starved behind player and comfort", s.selections, s.maxWait)
	}
}

// Load, colony or map changes and tick rewinds discard ranking history;
// continuing the same world keeps it.
func TestDevelopmentSimulationContextResets(t *testing.T) {
	s := newDevelopmentSim(t, 1, simGoal("comfort", 0.5, nil), simGoal("research", 0.5, nil))
	s.review()
	s.open = map[GoalID]domain.Tick{}
	aged := s.review()
	if aged.Rows[0].Goal != "comfort" || aged.Rows[0].Score != 72.5 || s.row(aged, "research").WaitingSince != 100 {
		t.Fatal("history not retained in the same world", aged.Rows)
	}
	for _, change := range []func(){
		func() { s.snapshot.Load = "reloaded" },
		func() { s.snapshot.Map = 2 },
		func() { s.tick = 50 },
	} {
		before := s.snapshot
		change()
		s.open = map[GoalID]domain.Tick{}
		reset := s.review()
		for _, row := range reset.Rows {
			if row.WaitingSince != reset.Tick || row.Score != 50 {
				t.Fatalf("history survived a context change: %+v", row)
			}
		}
		s.snapshot = before
		s.tick = 100000
		s.state = DevelopmentState{}
	}
}
