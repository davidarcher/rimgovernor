package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Pending MaintainWaste and MaintainAnimalFeed commitments, neither worked,
// while pawns haul for a third goal (one of them the very thing type the
// supplies haul names, at another thing): the hauls are not evidence of work
// on either commitment, which record an idle age but keep their commitments.
// A haul that turns to the supplies thing is attributed activity; an older producer without job targets keeps the work-type rule
// (#643).
func TestUnrelatedHaulingIsNotEvidenceForCommitments(t *testing.T) {
	s := newDevelopmentSim(t, 2, simGoal("supplies", 0.5, GoalLabor(MaintainWaste)), simGoal("feed", 0.5, GoalLabor(MaintainAnimalFeed)), simGoal("storage", 0.9, GoalLabor(ManagePollution)))
	s.tick = 5000
	haul, err := domain.NewHaul("pawn-a", "Thing_Steel1", "Steel", domain.Cell{X: 4, Z: 4})
	if err != nil {
		t.Fatal(err)
	}
	haulAction, err := domain.NewHaulAction("supplies-haul", haul)
	if err != nil {
		t.Fatal(err)
	}
	supplies := s.commitment("supplies", 4, true)
	supplies.Labor, supplies.Targets = GoalLabor(MaintainWaste), ActionWorkTargets(haulAction)
	feed := s.commitment("feed", 4, true)
	feed.Labor, feed.Targets = GoalLabor(MaintainAnimalFeed), domain.Known(WorkTargets{Things: []string{"Thing_Stove1"}})
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
	r := DevelopmentRequest{Snapshot: s.snapshot, Tick: s.tick, Workers: s.workers, Goals: s.goals, Commitments: []Commitment{supplies, feed}, LaborUse: use}
	first := rank(t, r)
	requireSelected(t, first, "storage")
	r.Previous, r.Tick = first, first.Tick+DevelopmentIdleTicks/2
	// A repeated review with no new evidence keeps the original deadline.
	again := rank(t, r)
	for _, id := range []ConcernID{"supplies", "feed"} {
		if row := s.row(again, id); row.Reason != DevelopmentCommitted || !reflect.DeepEqual(row.LaborIdleSince, domain.Known(domain.Tick(5000))) || row.LaborEvidence != LaborUnattributed {
			t.Fatal("a repeat review moved the deadline", row)
		}
	}
	r.Previous, r.Tick = again, first.Tick+DevelopmentIdleTicks
	released := rank(t, r)
	requireSelected(t, released, "storage")
	for _, id := range []ConcernID{"supplies", "feed"} {
		if row := s.row(released, id); row.Reason != DevelopmentCommitted || !row.Committed || row.LaborEvidence != LaborUnattributed || !reflect.DeepEqual(row.LaborIdleSince, domain.Known(domain.Tick(5000))) {
			t.Fatal("idle labor past the bound must keep the commitment and its idle age", row)
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
	if row := s.row(resumed, "feed"); row.LaborEvidence != LaborUnattributed {
		t.Fatal("another goal's matched haul counted for feed", row)
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

// The idle deadline is game-tick history: a rewind or a world change
// discards it and the next idle review starts a fresh one (#643).
func TestLaborIdleSinceResetsOnRewindAndWorldChange(t *testing.T) {
	s := newDevelopmentSim(t, 1, simGoal("supplies", 0.5, GoalLabor(MaintainWaste)))
	s.tick = 5000
	c := s.commitment("supplies", 4, true)
	c.Labor, c.Targets = GoalLabor(MaintainWaste), domain.Known(WorkTargets{Things: []string{"Thing_Steel1"}})
	other := WorkPawn{ID: "a", Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkHauling, Priority: 3}}), Job: domain.Known(PawnJob{Def: "HaulToCell", Work: WorkHauling, Target: domain.Known(JobTarget{Thing: "Thing_Other9"})})}
	r := DevelopmentRequest{Snapshot: s.snapshot, Tick: s.tick, Workers: s.workers, Goals: s.goals, Commitments: []Commitment{c}, LaborUse: RoutineLaborUse([]WorkPawn{other})}
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
