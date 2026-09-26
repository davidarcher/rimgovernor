package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Pending SecureSupplies and MaintainAnimalFeed commitments, neither worked,
// while pawns haul for a third goal (one of them the very thing type the
// supplies haul names, at another thing): the hauls are not evidence of work
// on either commitment, so both release their slots once the lack spans
// DevelopmentIdleTicks. A haul that turns to the supplies thing takes that
// slot back; an older producer without job targets keeps the work-type rule
// (#643).
func TestUnrelatedHaulingDoesNotHoldStalledCommitments(t *testing.T) {
	s := newDevelopmentSim(t, 2, simGoal("supplies", 0.5, GoalLabor(SecureSupplies)), simGoal("feed", 0.5, GoalLabor(MaintainAnimalFeed)), simGoal("storage", 0.9, GoalLabor(MaintainStorage)))
	s.tick = 5000
	haul, err := domain.NewHaul("pawn-a", "Thing_Steel1", "Steel", domain.Cell{X: 4, Z: 4})
	if err != nil {
		t.Fatal(err)
	}
	haulAction, err := domain.NewHaulAction("supplies-haul", haul)
	if err != nil {
		t.Fatal(err)
	}
	supplies := s.commitment("supplies", AutopilotGoal, 4, true)
	supplies.Labor, supplies.Targets = GoalLabor(SecureSupplies), ActionWorkTargets(haulAction)
	feed := s.commitment("feed", AutopilotGoal, 4, true)
	feed.Labor, feed.Targets = GoalLabor(MaintainAnimalFeed), domain.Known(WorkTargets{Things: []string{"Thing_Stove1"}})
	for _, c := range []*Commitment{&supplies, &feed} {
		c.Dispatched = domain.Known(s.tick)
	}
	hauler := func(id PawnID, thing string) WorkPawn {
		job := PawnJob{Def: "HaulToCell", Work: WorkHauling, Target: domain.Known(JobTarget{Thing: thing, Cell: domain.Known(domain.Cell{X: 9, Z: 9})})}
		return WorkPawn{ID: id, Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkHauling, Priority: 3}, {Work: WorkCooking, Priority: 3}}), Job: domain.Known(job)}
	}
	use := RoutineLaborUse([]WorkPawn{hauler("a", "Thing_Other9"), hauler("b", "Thing_Steel2")})
	if laborIdle(use, supplies.Labor) || laborIdle(use, feed.Labor) {
		t.Fatal("fixture: the work-type rule already saw these commitments idle")
	}
	for _, c := range []Commitment{supplies, feed} {
		if e := CommitmentLabor(use, c.Labor, c.Targets); e != LaborUnattributed {
			t.Fatal("an unrelated haul read as", e, c.Goal)
		}
	}
	r := DevelopmentRequest{Snapshot: s.snapshot, Tick: s.tick, Workers: s.workers, Limit: 2, Goals: s.goals, Commitments: []Commitment{supplies, feed}, LaborUse: use}
	first := rank(t, r)
	requireSelected(t, first)
	r.Previous, r.Tick = first, first.Tick+DevelopmentIdleTicks/2
	// A repeated review with no new evidence keeps the original deadline.
	again := rank(t, r)
	for _, id := range []GoalID{"supplies", "feed"} {
		if row := s.row(again, id); row.Reason != DevelopmentCommitted || !reflect.DeepEqual(row.LaborIdleSince, domain.Known(domain.Tick(5000))) || row.LaborEvidence != LaborUnattributed {
			t.Fatal("a repeat review moved the deadline", row)
		}
	}
	r.Previous, r.Tick = again, first.Tick+DevelopmentIdleTicks
	released := rank(t, r)
	requireSelected(t, released, "storage")
	for _, id := range []GoalID{"supplies", "feed"} {
		if row := s.row(released, id); row.Reason != DevelopmentLaborIdle || row.Committed || row.LaborEvidence != LaborUnattributed {
			t.Fatal("unrelated hauling held a stalled commitment", row)
		}
	}
	if err := ValidateDevelopmentState(released); err != nil {
		t.Fatal(err)
	}

	// The same work type on the supplies' own thing is attributed activity.
	r.Previous, r.Tick = released, released.Tick+100
	r.LaborUse = RoutineLaborUse([]WorkPawn{hauler("a", "Thing_Other9"), hauler("b", "Thing_Steel1")})
	resumed := rank(t, r)
	if row := s.row(resumed, "supplies"); row.Reason != DevelopmentCommitted || row.LaborEvidence != LaborAttributed || row.LaborIdleSince != domain.Unknown[domain.Tick]() {
		t.Fatal("matched work did not retain its commitment", row)
	}
	if row := s.row(resumed, "feed"); row.Reason != DevelopmentLaborIdle {
		t.Fatal("another goal's matched haul held feed", row)
	}

	// An older producer carries no job targets: the work-type rule holds.
	old := hauler("a", "")
	job, _ := old.Job.Value()
	job.Target = domain.Unknown[JobTarget]()
	old.Job = domain.Known(job)
	r.Previous, r.Tick = first, first.Tick+2*DevelopmentIdleTicks
	r.LaborUse = RoutineLaborUse([]WorkPawn{old})
	if row := s.row(rank(t, r), "supplies"); row.Reason != DevelopmentCommitted || row.LaborEvidence != LaborWorkTypeBusy {
		t.Fatal("an untargeted job became idle evidence", row)
	}
	// A colony asleep is no evidence either way.
	sleeper := hauler("a", "")
	sleeper.Job = domain.Known(PawnJob{Def: "LayDown"})
	if e := CommitmentLabor(RoutineLaborUse([]WorkPawn{sleeper}), supplies.Labor, supplies.Targets); e != LaborUnknown {
		t.Fatal("sleep read as", e)
	}
}

func TestJobTargetMatchesThingOrCell(t *testing.T) {
	want := WorkTargets{Things: []string{"Thing_Frame1"}, Cells: []domain.Cell{{X: 3, Z: 5}}}
	for _, c := range []struct {
		job  JobTarget
		want bool
	}{
		{JobTarget{Thing: "Thing_Frame1"}, true},
		{JobTarget{Thing: "Thing_Rock7", Cell: domain.Known(domain.Cell{X: 3, Z: 5})}, true},
		{JobTarget{Thing: "Thing_Rock7", Cell: domain.Known(domain.Cell{X: 3, Z: 6})}, false},
		{JobTarget{}, false},
	} {
		if c.job.on(want) != c.want {
			t.Fatal(c.job, c.want)
		}
	}
}

// A pawn whose job stays aimed at the commitment's own thing is activity,
// not progress: it keeps the slot, but once the effect has stayed pending
// past DevelopmentStallTicks the stall bound releases it anyway (#643).
func TestMatchedButStuckActivityFollowsTheStallBound(t *testing.T) {
	s := newDevelopmentSim(t, 1, simGoal("supplies", 0.5, GoalLabor(SecureSupplies)), simGoal("storage", 0.9, GoalLabor(MaintainStorage)))
	s.tick = 5000
	c := s.commitment("supplies", AutopilotGoal, 4, true)
	c.Labor, c.Targets, c.Dispatched = GoalLabor(SecureSupplies), domain.Known(WorkTargets{Things: []string{"Thing_Steel1"}}), domain.Known(s.tick)
	p, err := c.Progress.Observe(domain.Observation{Action: c.Progress.View().Action, Attempt: 1, Snapshot: s.snapshot, Tick: s.tick, Effect: domain.EffectPending, Causality: domain.AfterDispatch}, s.snapshot)
	if err != nil {
		t.Fatal(err)
	}
	c.Progress = p
	if effect, _ := c.Progress.View().Effect.Value(); effect != domain.EffectPending {
		t.Fatal("fixture: effect", effect)
	}
	stuck := WorkPawn{ID: "a", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkHauling, Priority: 3}}), Job: domain.Known(PawnJob{Def: "HaulToCell", Work: WorkHauling, Target: domain.Known(JobTarget{Thing: "Thing_Steel1"})})}
	r := DevelopmentRequest{Snapshot: s.snapshot, Tick: s.tick, Workers: s.workers, Limit: 1, Goals: s.goals, Commitments: []Commitment{c}, LaborUse: RoutineLaborUse([]WorkPawn{stuck})}
	state := rank(t, r)
	for _, tick := range []domain.Tick{DevelopmentIdleTicks, DevelopmentStallTicks / 2, DevelopmentStallTicks} {
		r.Previous, r.Tick = state, 5000+tick
		state = rank(t, r)
		if row := s.row(state, "supplies"); !row.Committed || row.LaborEvidence != LaborAttributed || row.LaborIdleSince != domain.Unknown[domain.Tick]() {
			t.Fatal("matched activity within the stall bound", tick, row)
		}
	}
	r.Previous, r.Tick = state, 5000+DevelopmentStallTicks+1
	state = rank(t, r)
	if row := s.row(state, "supplies"); row.Committed || len(state.Committed) != 0 {
		t.Fatal("matched-but-stuck activity held the slot past the stall bound", row)
	}
	requireSelected(t, state, "storage")
}

// The idle deadline is game-tick history: a rewind or a world change
// discards it and the next idle review starts a fresh one (#643).
func TestLaborIdleSinceResetsOnRewindAndWorldChange(t *testing.T) {
	s := newDevelopmentSim(t, 1, simGoal("supplies", 0.5, GoalLabor(SecureSupplies)))
	s.tick = 5000
	c := s.commitment("supplies", AutopilotGoal, 4, true)
	c.Labor, c.Targets = GoalLabor(SecureSupplies), domain.Known(WorkTargets{Things: []string{"Thing_Steel1"}})
	other := WorkPawn{ID: "a", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkHauling, Priority: 3}}), Job: domain.Known(PawnJob{Def: "HaulToCell", Work: WorkHauling, Target: domain.Known(JobTarget{Thing: "Thing_Other9"})})}
	r := DevelopmentRequest{Snapshot: s.snapshot, Tick: s.tick, Workers: s.workers, Limit: 1, Goals: s.goals, Commitments: []Commitment{c}, LaborUse: RoutineLaborUse([]WorkPawn{other})}
	first := rank(t, r)
	if row := s.row(first, "supplies"); row.LaborIdleSince != domain.Known(domain.Tick(5000)) {
		t.Fatal(row)
	}
	later := r
	later.Previous, later.Tick = first, 6000
	if row := s.row(rank(t, later), "supplies"); row.LaborIdleSince != domain.Known(domain.Tick(5000)) {
		t.Fatal("a later review lost the deadline", row)
	}
	rewound := r
	rewound.Previous, rewound.Tick = rank(t, later), 5500
	if row := s.row(rank(t, rewound), "supplies"); row.LaborIdleSince != domain.Known(domain.Tick(5500)) || !row.Committed {
		t.Fatal("a rewind kept the old deadline", row)
	}
	for _, snap := range []domain.GenerationSnapshot{
		{Colony: "colony", Map: 1, Load: "reloaded", Plan: "plan"},
		{Colony: "other", Map: 1, Load: "load", Plan: "plan"},
	} {
		changed := r
		changed.Snapshot, changed.Previous, changed.Tick = snap, first, 5000+DevelopmentIdleTicks
		if row := s.row(rank(t, changed), "supplies"); row.LaborIdleSince != domain.Known(changed.Tick) || !row.Committed {
			t.Fatal("a world change kept the old deadline", snap, row)
		}
	}
}
