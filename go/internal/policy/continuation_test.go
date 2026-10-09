package policy

import (
	"encoding/json"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The shared contract is exercised through the existing owners, rather than
// through a parallel lifecycle object that could disagree with them.
func TestContinuationPreservesStableWorkAcrossReconstruction(t *testing.T) {
	t.Run("shelter", func(t *testing.T) {
		view := shelterTestView()
		_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
		encoded, err := json.Marshal(memory)
		if err != nil {
			t.Fatal(err)
		}
		var restored CombatMemory
		if err = json.Unmarshal(encoded, &restored); err != nil {
			t.Fatal(err)
		}
		for i := range view.Pawns {
			view.Pawns[i].Stance = StanceMoving
		}
		view.Tick++
		orders, next := decideStop(t, view, StopEvent{}, restored)
		if len(orders) != 0 || !reflect.DeepEqual(next.Roles, memory.Roles) {
			t.Fatalf("reconstruction replaced shelter: %+v", orders)
		}
	})
	t.Run("recovery", func(t *testing.T) {
		p, h := recoveryPlanning(t)
		first := recoverySelect(t, p, h)
		encoded, err := json.Marshal(h)
		if err != nil {
			t.Fatal(err)
		}
		var restored DisasterHistory
		if err = json.Unmarshal(encoded, &restored); err != nil {
			t.Fatal(err)
		}
		if next := recoverySelect(t, p, &restored); !reflect.DeepEqual(first, next) {
			t.Fatal("reconstruction replaced recovery candidates")
		}
		used := []domain.MethodID{}
		for _, candidate := range first.Candidates {
			used = append(used, candidate.ID)
		}
		if next := recoverySelect(t, p, &restored, used...); next.Reason != RecoveryMethodsSeen || RecoveryNeed(&restored) == domain.FindingMet {
			t.Fatal("applied/attempted work completed the concern", next)
		}
	})
}

func TestContinuationDeadlineReassessesWithoutClaimingCompletion(t *testing.T) {
	t.Run("shelter", func(t *testing.T) {
		view := shelterTestView()
		_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
		for i := range view.Pawns {
			view.Pawns[i].Stance = StanceMoving
		}
		view.Tick = memory.WaitSince + raidGiveUpTicks
		orders, next := decideStop(t, view, StopEvent{}, memory)
		if len(orders) != 0 || next.Tactic != TacticShelter || len(next.Roles) == 0 {
			t.Fatalf("deadline discarded useful shelter: %+v %+v", orders, next)
		}
	})
	t.Run("recovery", func(t *testing.T) {
		p, h := recoveryPlanning(t)
		zero := int64(0)
		conditions := domain.Known([]DisasterCondition{{ID: "event", Definition: "ToxicFallout", TicksLeft: &zero}})
		next, err := ReviewDisaster(conditions, p.Buildings, disasterGates(), h, h.Observed+raidGiveUpTicks)
		if err != nil || next.Phase == DisasterRestored {
			t.Fatal("elapsed condition duration proved recovery", next, err)
		}
		unknown, err := ReviewDisaster(domain.Unknown[[]DisasterCondition](), p.Buildings, disasterGates(), next, next.Observed+1)
		if err != nil || unknown.Phase != DisasterUnknown || RecoveryNeed(unknown) != domain.FindingUnclear {
			t.Fatal("unknown became completion", unknown, err)
		}
		clear, err := ReviewDisaster(domain.Known([]DisasterCondition{}), domain.Known([]RecoveryBuilding{recoveryBuilding("wall")}), disasterGates(), unknown, unknown.Observed+1)
		if err != nil || clear.Phase != DisasterRestored || RecoveryNeed(clear) != domain.FindingMet {
			t.Fatal("observed recovery failed to release", clear, err)
		}
	})
}

func TestRecoveryContinuationUsesObservedJobUntilWorkEnds(t *testing.T) {
	p, _ := recoveryPlanning(t)
	service, _ := domain.NewRecoveryService("a", "wall", domain.RecoveryServiceRepair)
	job := PawnJob{Def: "Repair", Target: domain.Known(JobTarget{Thing: "wall"})}
	pawns := []WorkPawn{{ID: "a", Job: domain.Known(job)}}
	for _, hp := range []int64{50, 60, 90} {
		buildings, _ := p.Buildings.Value()
		buildings[0].HitPoints = domain.Known(hp)
		if got := RecoveryWorkContinuing(service, domain.Known(buildings), domain.Known(pawns)); got != domain.Known(true) {
			t.Fatalf("progress replaced useful repair: %v", got)
		}
	}
	pawns[0].Job = domain.Unknown[PawnJob]()
	if _, known := RecoveryWorkContinuing(service, p.Buildings, domain.Known(pawns)).Value(); known {
		t.Fatal("unknown job released work")
	}
	pawns[0].Job = domain.Known(PawnJob{})
	// Unrelated native repair and unread jobs do not own this Method.
	pawns = append(pawns, WorkPawn{ID: "b", Job: domain.Known(job)}, WorkPawn{ID: "c", Job: domain.Unknown[PawnJob]()})
	if got := RecoveryWorkContinuing(service, p.Buildings, domain.Known(pawns)); got != domain.Known(false) {
		t.Fatal("interrupted work never reconsidered", got)
	}
	pawns[0].Job = domain.Known(job)
	if got := RecoveryWorkContinuing(service, domain.Known([]RecoveryBuilding{recoveryBuilding("wall")}), domain.Known(pawns)); got != domain.Known(false) {
		t.Fatal("finished repair held responsibility", got)
	}
}
