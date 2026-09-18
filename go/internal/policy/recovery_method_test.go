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
func recoveryPlanning(t *testing.T, hazard bool) (RecoveryPlanning, *DisasterHistory) {
	t.Helper()
	b := recoveryBuilding("wall")
	b.HitPoints = domain.Known(int64(50))
	p := RecoveryPlanning{Buildings: domain.Known([]RecoveryBuilding{b}), Workers: domain.Known([]RecoveryWorker{recoveryWorker("b"), recoveryWorker("a")}), Safety: domain.Known(RecoverySafety{RoofHazard: domain.Known(hazard), SafeAreas: []string{"roof2", "roof1"}, Restrictions: []RecoveryRestriction{{"b", domain.Known("")}, {"a", domain.Known("player")}}})}
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
func TestRecoverySelectionProtectsAreaBeforeService(t *testing.T) {
	p, h := recoveryPlanning(t, true)
	s := recoverySelect(t, p, h)
	if s.Reason != RecoveryAdmissionRequired || len(s.Candidates) != 4 {
		t.Fatal(s)
	}
	for i, c := range s.Candidates {
		if c.Kind != RecoveryAreaProposal || c.Pawn != []PawnID{"a", "a", "b", "b"}[i] || c.Area != []string{"roof1", "roof2", "roof1", "roof2"}[i] {
			t.Fatal(s)
		}
	}
	if *s.Candidates[0].PriorArea != "player" {
		t.Fatal("lost player restriction")
	}
	used := []domain.MethodID{}
	for _, c := range s.Candidates {
		used = append(used, c.ID)
	}
	if exhausted := recoverySelect(t, p, h, used...); exhausted.Reason != RecoveryMethodsSeen || len(exhausted.Candidates) != 0 {
		t.Fatal("exhausted refuge authorized work", exhausted)
	}
	h.Observed = 600
	s2 := recoverySelect(t, p, h, used...)
	if len(s2.Candidates) != 4 || s2.Candidates[0].ID == s.Candidates[0].ID {
		t.Fatal("new lease window did not renew candidates")
	}
}
func TestRecoverySelectionCurrentRoofRestrictionAllowsBoundedWork(t *testing.T) {
	p, h := recoveryPlanning(t, true)
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
func TestRecoverySelectionUnknownsAndPlayerGuards(t *testing.T) {
	for _, name := range []string{"no_refuge", "unknown_hazard", "missing_worker", "unknown_area", "dead", "downed", "drafted", "mental", "forced", "unknown_job"} {
		t.Run(name, func(t *testing.T) {
			p, h := recoveryPlanning(t, true)
			safe, _ := p.Safety.Value()
			workers, _ := p.Workers.Value()
			want := RecoveryFactsUnknown
			switch name {
			case "no_refuge":
				safe.SafeAreas = nil
				want = RecoveryNoRefuge
			case "unknown_hazard":
				safe.RoofHazard = domain.Unknown[bool]()
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
					case "forced":
						workers[i].PlayerForced = domain.Known(true)
					case "unknown_job":
						workers[i].PlayerForced = domain.Unknown[bool]()
					}
				}
				if name != "unknown_job" {
					want = RecoveryNoWorker
				}
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
	p, h := recoveryPlanning(t, false)
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
func TestRecoveryRoofHazardMaintainsNeedWithoutDamagedBuildings(t *testing.T) {
	p, _ := recoveryPlanning(t, true)
	p.Buildings = domain.Known([]RecoveryBuilding{})
	h, err := ReviewDisaster(domain.Known([]DisasterCondition{{ID: "event", Definition: "ToxicFallout"}}), p.Buildings, disasterGates(), nil, 10)
	if err != nil {
		t.Fatal(err)
	}
	if h.Phase != DisasterSurvival || RecoveryNeed(h, p.Safety) != domain.NeedDeficit || recoverySelect(t, p, h).Reason != RecoveryAdmissionRequired {
		t.Fatal(h)
	}
	safe, _ := p.Safety.Value()
	safe.RoofHazard = domain.Known(false)
	p.Safety = domain.Known(safe)
	if RecoveryNeed(h, p.Safety) != domain.NeedRecovered || recoverySelect(t, p, h).Reason != RecoveryNoWork {
		t.Fatal("clear roof hazard stayed active")
	}
	p.Safety = domain.Unknown[RecoverySafety]()
	if RecoveryNeed(h, p.Safety) != domain.NeedUnknown {
		t.Fatal("unknown safety recovered")
	}
}
