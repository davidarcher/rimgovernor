package policy

import (
	"fmt"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func recoveryWorker(id string) RecoveryWorker {
	k := domain.Known(false)
	return RecoveryWorker{Pawn: PawnID(id), Dead: k, Downed: k, Drafted: k, Mental: k, PlayerForced: k}
}
func recoveryPlanning(t *testing.T) (RecoveryPlanning, *DisasterHistory) {
	t.Helper()
	b := recoveryBuilding("wall")
	b.HitPoints = domain.Known(int64(50))
	p := RecoveryPlanning{Buildings: domain.Known([]RecoveryBuilding{b}), Workers: domain.Known([]RecoveryWorker{recoveryWorker("b"), recoveryWorker("a")}), Safety: domain.Known(RecoverySafety{Restrictions: []RecoveryRestriction{{"b", domain.Known("")}, {"a", domain.Known("player")}}})}
	h, err := ReviewDisaster(domain.Known([]DisasterCondition{{ID: "event", Definition: "ToxicFallout"}}), p.Buildings, disasterGates(), nil, 10)
	if err != nil {
		t.Fatal(err)
	}
	return p, h
}
func recoverySelect(t *testing.T, p RecoveryPlanning, h *DisasterHistory, used ...domain.MethodID) RecoverySelection {
	t.Helper()
	s, err := SelectRecoveryMethods(p, h, used, h.Observed)
	if err != nil || s.Validate() != nil {
		t.Fatal(s, err, s.Validate())
	}
	return s
}
func TestRecoverySelectionKeepsRestrictionAndBoundsWork(t *testing.T) {
	p, h := recoveryPlanning(t)
	safe, _ := p.Safety.Value()
	for i := range safe.Restrictions {
		safe.Restrictions[i].Area = domain.Known("roof1")
	}
	p.Safety = domain.Known(safe)
	s := recoverySelect(t, p, h)
	if len(s.Candidates) != 2 || s.Candidates[0].Kind != RecoveryServiceProposal || s.Candidates[0].Method != RecoveryRepair || *s.Candidates[0].PriorArea != "roof1" {
		t.Fatal(s)
	}
	first := s.Candidates[0].ID
	s = recoverySelect(t, p, h, first)
	if len(s.Candidates) != 1 || s.Candidates[0].Pawn != "b" {
		t.Fatal(s)
	}
	buildings, _ := p.Buildings.Value()
	buildings[0].HitPoints = domain.Known(int64(49))
	p.Buildings = domain.Known(buildings)
	s = recoverySelect(t, p, h, first)
	if len(s.Candidates) != 2 || s.Candidates[0].ID == first {
		t.Fatal("changed native state reused method", s)
	}
}
func TestRecoverySelectionUnknownsAndUnavailableWorkers(t *testing.T) {
	for _, name := range []string{"missing_worker", "unknown_area", "dead", "downed", "drafted", "mental"} {
		t.Run(name, func(t *testing.T) {
			p, h := recoveryPlanning(t)
			safe, _ := p.Safety.Value()
			workers, _ := p.Workers.Value()
			want := RecoveryFactsUnknown
			switch name {
			case "missing_worker":
				workers = workers[:1]
			case "unknown_area":
				for i := range safe.Restrictions {
					safe.Restrictions[i].Area = domain.Unknown[string]()
				}
			default:
				for i := range workers {
					switch name {
					case "dead":
						workers[i].Dead = domain.Known(true)
					case "downed":
						workers[i].Downed = domain.Known(true)
					case "drafted":
						workers[i].Drafted = domain.Known(true)
					case "mental":
						workers[i].Mental = domain.Known(true)
					}
				}
				want = RecoveryNoWorker
			}
			p.Safety = domain.Known(safe)
			p.Workers = domain.Known(workers)
			s := recoverySelect(t, p, h)
			if s.Reason != want || len(s.Candidates) != 0 {
				t.Fatal(s)
			}
		})
	}
}
func TestRecoverySelectionStableBoundedAndNonAliasing(t *testing.T) {
	p, h := recoveryPlanning(t)
	safe, _ := p.Safety.Value()
	safe.Restrictions = nil
	workers := []RecoveryWorker{}
	for i := 15; i >= 0; i-- {
		id := fmt.Sprintf("pawn%02d", i)
		workers = append(workers, recoveryWorker(id))
		safe.Restrictions = append(safe.Restrictions, RecoveryRestriction{Pawn: PawnID(id), Area: domain.Known("")})
	}
	p.Workers = domain.Known(workers)
	p.Safety = domain.Known(safe)
	s := recoverySelect(t, p, h)
	if len(s.Candidates) != 8 || s.Candidates[0].Pawn != "pawn00" || s.Candidates[7].Pawn != "pawn07" {
		t.Fatal(s)
	}
	before := recoverySelect(t, p, h)
	*s.Candidates[0].HitPoints = 1
	*s.Candidates[0].PriorArea = "changed"
	if after := recoverySelect(t, p, h); !reflect.DeepEqual(after, before) {
		t.Fatal("proposal aliases input")
	}
	used := []domain.MethodID{}
	for _, c := range before.Candidates {
		used = append(used, c.ID)
	}
	next := recoverySelect(t, p, h, used...)
	if len(next.Candidates) != 8 || next.Candidates[0].Pawn != "pawn08" {
		t.Fatal(next)
	}
	if _, err := SelectRecoveryMethods(p, h, []domain.MethodID{"same", "same"}, h.Observed); err == nil {
		t.Fatal("accepted duplicate method history")
	}
	if _, err := SelectRecoveryMethods(p, h, nil, h.Observed+1); err == nil {
		t.Fatal("stale planning snapshot admitted")
	}
}
func TestRecoveryUsesForcedWorkerAsFallback(t *testing.T) {
	p, h := recoveryPlanning(t)
	workers, _ := p.Workers.Value()
	workers[1].PlayerForced = domain.Known(true) // a sorts after ordinary worker b.
	p.Workers = domain.Known(workers)
	s := recoverySelect(t, p, h)
	if len(s.Candidates) != 2 || s.Candidates[0].Pawn != "b" || s.Candidates[1].Pawn != "a" {
		t.Fatal(s)
	}
	for i := range workers {
		workers[i].PlayerForced = domain.Known(true)
	}
	p.Workers = domain.Known(workers)
	if s := recoverySelect(t, p, h); len(s.Candidates) != 2 {
		t.Fatal(s)
	}
}

// A step anchors on the review's tick: an active disaster observed at the
// review refuses a later live tick, which is why the step passes review.Tick.
func TestRecoverySelectionBoundaryIsTheObservationTick(t *testing.T) {
	p, h := recoveryPlanning(t)
	if _, err := SelectRecoveryMethods(p, h, nil, h.Observed+5); err == nil {
		t.Fatal("a live tick ahead of the observation was accepted")
	}
	if _, err := SelectRecoveryMethods(p, h, nil, h.Observed); err != nil {
		t.Fatal(err)
	}
}
