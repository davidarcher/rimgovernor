package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestHomeClearanceBoundDistanceAndHolds(t *testing.T) {
	base := ClearanceTarget{EntityID: "near", DefName: "Wall", InHome: true, Deconstructible: true, Minimum: domain.Cell{X: 10, Z: 10}, Maximum: domain.Cell{X: 10, Z: 10}}
	far := base
	far.EntityID = "far"
	far.Minimum = domain.Cell{X: 30, Z: 30}
	far.Maximum = far.Minimum
	rows := []ClearanceTarget{far, base}
	for reason, change := range map[string]func(*ClearanceTarget){
		"roof_blocker":        func(r *ClearanceTarget) { r.RoofBlocker = "unsupported" },
		"ancient_danger":      func(r *ClearanceTarget) { r.AncientDanger = true },
		"casket":              func(r *ClearanceTarget) { r.Class = "ancient_casket" },
		"foreign_designation": func(r *ClearanceTarget) { r.Designated = true },
		"owned_designation":   func(r *ClearanceTarget) { r.Designated = true; r.ControllerOwned = true },
		"outside_home":        func(r *ClearanceTarget) { r.InHome = false },
	} {
		row := base
		row.EntityID = reason
		change(&row)
		rows = append(rows, row)
	}
	got := SelectHomeClearance(rows, domain.Cell{X: 10, Z: 10})
	if len(got.Targets) != 1 || got.Targets[0].EntityID != "near" || len(got.Holds) != 6 {
		t.Fatal(got)
	}
	for _, h := range got.Holds {
		if h.Target != h.Reason {
			t.Fatal(h)
		}
	}
}

func TestClearanceRecoveryUnknownAndIssued(t *testing.T) {
	row := ClearanceTarget{EntityID: "ruin", InHome: true, Deconstructible: true}
	previous := UpkeepHistory{}
	for _, step := range []struct {
		rows           domain.Fact[[]ClearanceTarget]
		issued, active bool
	}{
		{domain.Known([]ClearanceTarget{row}), false, true},
		{domain.Unknown[[]ClearanceTarget](), false, true},
		{domain.Known([]ClearanceTarget{}), true, true},
		{domain.Known([]ClearanceTarget{}), false, false},
		{domain.Known([]ClearanceTarget{row}), false, true},
	} {
		r, err := ReviewUpkeep(UpkeepObservation{Clearance: step.rows}, previous, map[GoalID]bool{ClearHomeObstructions: step.issued})
		if err != nil || r.History.Clearance != step.active {
			t.Fatal(r, err)
		}
		previous = r.History
	}
}

func TestClearanceAdmissionFollowsRepairsAndPrecedesCleaning(t *testing.T) {
	f := stableRoutine()
	f.Upkeep.Clearance = domain.Known([]ClearanceTarget{{EntityID: "ruin", InHome: true, Deconstructible: true}})
	f.Upkeep.Structures = domain.Known([]UpkeepStructure{{ID: "door", Home: true, HitPoints: 50, MaxHitPoints: 100}})
	f.Upkeep.Filth = domain.Unknown[[]UpkeepFilth]()
	previous := RoutineLatches{Upkeep: UpkeepHistory{Cleaning: true}}
	check := func(repair bool) {
		r := needs(t, f, previous)
		found := map[GoalID]bool{}
		for _, g := range r.Goals {
			found[g.ID] = true
			if g.ID == ClearHomeObstructions && g.MethodUnavailable != repair {
				t.Fatal("clearance did not defer to repairs", g)
			}
			if g.ID == MaintainCleanFacilities && !g.MethodUnavailable {
				t.Fatal("cleaning preceded clearance", g)
			}
		}
		if !found[ClearHomeObstructions] || !found[MaintainCleanFacilities] {
			t.Fatal(found)
		}
	}
	check(true)
	f.Upkeep.Structures = domain.Known([]UpkeepStructure{})
	check(false)
}
