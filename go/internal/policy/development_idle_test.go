package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A wood cut whose plant cutters are all building or wandering records the
// idle age but keeps its commitment past DevelopmentIdleTicks: work nobody
// has picked up yet is still the goal's work. Work picked up clears the age.
func TestIdleLaborRecordsButKeepsCommitmentAcrossReviews(t *testing.T) {
	s := newDevelopmentSim(t, 1, simGoal("wood", 0.4, ConcernLabor(MaintainResource)), simGoal("sleeping", 0.9, ConcernLabor(MaintainHousing)))
	s.tick = 5000
	c := s.commitment("wood", 4, true)
	c.Labor = ConcernLabor(MaintainResource)
	idle := domain.Known(LaborUse{Busy: map[WorkType]int{WorkConstruction: 2}, Idle: map[WorkType]int{WorkPlantCutting: 2}})
	busy := domain.Known(LaborUse{Busy: map[WorkType]int{WorkPlantCutting: 1, WorkConstruction: 1}, Idle: map[WorkType]int{WorkPlantCutting: 1}})
	r := DevelopmentRequest{Snapshot: s.snapshot, Tick: s.tick, Workers: s.workers, Concerns: s.goals, Commitments: []Commitment{c}, LaborUse: idle}
	first := rank(t, r)
	if row := s.row(first, "wood"); row.Reason != DevelopmentCommitted || !reflect.DeepEqual(row.LaborIdleSince, domain.Known(domain.Tick(5000))) || !reflect.DeepEqual(first.Committed, []ConcernID{"wood"}) {
		t.Fatal("an idle review keeps the commitment and starts the idle age", row, first.Committed)
	}
	r.Tick += 2 * DevelopmentIdleTicks
	r.Previous = first
	held := rank(t, r)
	if row := s.row(held, "wood"); row.Reason != DevelopmentCommitted || !reflect.DeepEqual(row.LaborIdleSince, domain.Known(domain.Tick(5000))) || !reflect.DeepEqual(held.Committed, []ConcernID{"wood"}) {
		t.Fatal("idle past the bound must not release", row)
	}
	r.Tick += 100
	r.Previous = held
	r.LaborUse = busy
	resumed := rank(t, r)
	if row := s.row(resumed, "wood"); row.Reason != DevelopmentCommitted || row.LaborIdleSince != domain.Unknown[domain.Tick]() {
		t.Fatal("work picked up clears the idle age", row)
	}
}
