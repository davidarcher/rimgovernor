package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A wood cut whose plant cutters are all building or wandering holds its
// slot for one review and releases it once the idleness spans
// DevelopmentIdleTicks; the released goal reads labor_idle, holds no slot
// and is not reselected while its work stays open, and takes its slot
// back the review a cutter is on the work again (#445).
func TestIdleLaborReleasesCommitmentAcrossReviews(t *testing.T) {
	s := newDevelopmentSim(t, 1, simGoal("wood", 0.4, GoalLabor(MaintainWood)), simGoal("sleeping", 0.9, GoalLabor(MaintainSleeping)))
	s.tick = 5000
	c := s.commitment("wood", AutopilotGoal, 4, true)
	c.Labor = GoalLabor(MaintainWood)
	c.Dispatched = domain.Known(s.tick)
	idle := domain.Known(LaborUse{Busy: map[WorkType]int{WorkConstruction: 2}, Idle: map[WorkType]int{WorkPlantCutting: 2}})
	busy := domain.Known(LaborUse{Busy: map[WorkType]int{WorkPlantCutting: 1, WorkConstruction: 1}, Idle: map[WorkType]int{WorkPlantCutting: 1}})
	r := DevelopmentRequest{Snapshot: s.snapshot, Tick: s.tick, Workers: s.workers, Limit: 1, Goals: s.goals, Commitments: []Commitment{c}, LaborUse: idle}
	first := rank(t, r)
	requireSelected(t, first)
	if row := s.row(first, "wood"); row.Reason != DevelopmentCommitted || !reflect.DeepEqual(row.LaborIdleSince, domain.Known(domain.Tick(5000))) || !reflect.DeepEqual(first.Committed, []GoalID{"wood"}) {
		t.Fatal("one idle review keeps the slot and starts the idle age", row, first.Committed)
	}
	r.Tick += DevelopmentIdleTicks - 1
	r.Previous = first
	held := rank(t, r)
	requireSelected(t, held)
	if row := s.row(held, "wood"); row.Reason != DevelopmentCommitted || !reflect.DeepEqual(row.LaborIdleSince, domain.Known(domain.Tick(5000))) {
		t.Fatal("idle short of the bound still holds", row)
	}
	r.Tick++
	r.Previous = held
	released := rank(t, r)
	requireSelected(t, released, "sleeping")
	if row := s.row(released, "wood"); row.Reason != DevelopmentLaborIdle || row.Committed || row.Selected || row.WaitingSince != r.Tick || !reflect.DeepEqual(row.LaborIdleSince, domain.Known(domain.Tick(5000))) || len(released.Committed) != 0 {
		t.Fatal("idle past the bound releases the slot", row, released.Committed)
	}
	if err := ValidateDevelopmentState(released); err != nil {
		t.Fatal(err)
	}
	r.Tick += 100
	r.Previous = released
	still := rank(t, r)
	requireSelected(t, still, "sleeping")
	if row := s.row(still, "wood"); row.Reason != DevelopmentLaborIdle || !reflect.DeepEqual(row.LaborIdleSince, domain.Known(domain.Tick(5000))) {
		t.Fatal("still idle stays released with its original age", row)
	}
	r.Tick += 100
	r.Previous = still
	r.LaborUse = busy
	resumed := rank(t, r)
	requireSelected(t, resumed)
	if row := s.row(resumed, "wood"); row.Reason != DevelopmentCommitted || row.LaborIdleSince != domain.Unknown[domain.Tick]() || !reflect.DeepEqual(resumed.Committed, []GoalID{"wood"}) {
		t.Fatal("work picked up takes the slot back and clears the idle age", row)
	}
	// Unknown use, or a colony asleep (nobody busy, nobody idle), is no
	// evidence: the commitment holds through the bound, and carries the idle
	// age rather than resetting it (#643).
	for _, use := range []domain.Fact[LaborUse]{domain.Unknown[LaborUse](), domain.Known(LaborUse{Busy: map[WorkType]int{}, Idle: map[WorkType]int{}})} {
		r.Previous = first
		r.Tick = first.Tick + 2*DevelopmentIdleTicks
		r.LaborUse = use
		if row := s.row(rank(t, r), "wood"); row.Reason != DevelopmentCommitted || !reflect.DeepEqual(row.LaborIdleSince, domain.Known(domain.Tick(5000))) || row.LaborEvidence != LaborUnknown {
			t.Fatal("no evidence released a commitment", use, row)
		}
	}
	// A commitment without a labor profile is never idle.
	c.Labor = nil
	r.Commitments = []Commitment{c}
	r.LaborUse = idle
	if row := s.row(rank(t, r), "wood"); row.Reason != DevelopmentCommitted {
		t.Fatal("a profile-less commitment was judged idle", row)
	}
}

func TestRoutineLaborUse(t *testing.T) {
	enabled := func(id PawnID, job PawnJob, work ...WorkType) WorkPawn {
		var priorities []WorkPriority
		for _, w := range work {
			priorities = append(priorities, WorkPriority{Work: w, Priority: 3})
		}
		return WorkPawn{ID: id, Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known(priorities), Job: domain.Known(job)}
	}
	cutting := enabled("a", PawnJob{Def: "CutPlant", Work: WorkPlantCutting}, WorkPlantCutting, WorkConstruction)
	building := enabled("b", PawnJob{Def: "FinishFrame", Work: WorkConstruction}, WorkPlantCutting, WorkConstruction)
	wandering := enabled("c", PawnJob{Def: "Wait_Wander"}, WorkPlantCutting)
	jobless := enabled("d", PawnJob{}, WorkHauling)
	sleeping := enabled("e", PawnJob{Def: "LayDown"}, WorkPlantCutting, WorkHauling)
	drafted := WorkPawn{ID: "f", Available: domain.Known(false), Job: domain.Known(PawnJob{Def: "AttackStatic"})}
	use, known := RoutineLaborUse([]WorkPawn{cutting, building, wandering, jobless, sleeping, drafted}).Value()
	if !known || !reflect.DeepEqual(use.Busy, map[WorkType]int{WorkPlantCutting: 1, WorkConstruction: 1}) || !reflect.DeepEqual(use.Idle, map[WorkType]int{WorkPlantCutting: 2, WorkConstruction: 1, WorkHauling: 1}) {
		t.Fatal(use, known)
	}
	if laborIdle(domain.Known(use), GoalLabor(MaintainWood)) || !laborIdle(domain.Known(use), GoalLabor(SecureSupplies)) || laborIdle(domain.Known(use), GoalLabor(MaintainHerd)) || laborIdle(domain.Known(use), nil) {
		t.Fatal("unexpected idle judgement", use)
	}
	unknownJob := enabled("g", PawnJob{}, WorkHauling)
	unknownJob.Job = domain.Unknown[PawnJob]()
	if _, known := RoutineLaborUse([]WorkPawn{cutting, unknownJob}).Value(); known {
		t.Fatal("an unknown job became use evidence")
	}
	if _, known := RoutineLaborUse([]WorkPawn{cutting, {ID: "h", Available: domain.Known(true), Applies: domain.Known(true), Job: domain.Known(PawnJob{})}}).Value(); known {
		t.Fatal("unknown work settings became use evidence")
	}
}
