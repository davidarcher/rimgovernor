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
	// An observed (non-intent) kind: commitments follow per-attempt effects.
	cut, err := domain.NewTend("doctor", "patient")
	if err != nil {
		s.t.Fatal(err)
	}
	a, err := domain.NewTendAction(domain.ActionID(string(goal)+"-action"), cut)
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
	state, err := RankDevelopment(DevelopmentRequest{Snapshot: s.snapshot, Tick: s.tick, Workers: s.workers, Labor: s.labor, Goals: s.goals, Commitments: commitments, Previous: s.state})
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

// Capacity loss retains accepted work and resumes admission on recovery.
func TestDevelopmentSimulationCapacityLossAndRecovery(t *testing.T) {
	s := newDevelopmentSim(t, 2,
		simGoal("comfort", 0.8, GoalLabor(EnsureComfort)),
		simGoal("research", 0.6, GoalLabor(EnsureResearch)),
		simGoal("wood", 0.4, GoalLabor(MaintainResource)),
	)
	s.duration = 20000
	first := s.review()
	requireSelected(t, first, "comfort", "research", "wood")
	s.workers = domain.Known(0)
	lost := s.review()
	if len(lost.Committed) != 3 || lost.Capacity != 0 || selected(lost) != nil {
		t.Fatal(lost)
	}
	s.workers = domain.Unknown[int]()
	unknown := s.review()
	if unknown.Capacity != 0 || selected(unknown) != nil || len(unknown.Committed) != 3 {
		t.Fatal(unknown)
	}
	s.workers = domain.Known(4)
	s.open = map[GoalID]domain.Tick{}
	requireSelected(t, s.review(), "comfort", "research", "wood")
}

// Load, colony or map changes and tick rewinds discard ranking history;
// continuing the same world keeps it.
func TestDevelopmentSimulationContextResets(t *testing.T) {
	s := newDevelopmentSim(t, 1, simGoal("comfort", 0.5, nil), simGoal("research", 0.5, nil))
	s.review()
	s.open = map[GoalID]domain.Tick{}
	aged := s.review()
	// Both goals act; their waiting age carries from the first review.
	if s.row(aged, "research").WaitingSince != 100 || s.row(aged, "comfort").WaitingSince != 100 {
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
