package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestClearanceHoldReasonIsTheRowsOwnVerdict(t *testing.T) {
	base := ClearanceTarget{EntityID: "ruin", DefName: "Wall", Deconstructible: true}
	if reason := ClearanceHoldReason(base); reason != "" {
		t.Fatal("a ruin outside Home is held for", reason)
	}
	for reason, change := range map[string]func(*ClearanceTarget){
		"not_deconstructible": func(r *ClearanceTarget) { r.Deconstructible = false },
		"roof_blocker":        func(r *ClearanceTarget) { r.RoofBlocker = "unsupported" },
		"ancient_danger":      func(r *ClearanceTarget) { r.AncientDanger = true },
		"casket":              func(r *ClearanceTarget) { r.Class = "ancient_casket" },
	} {
		row := base
		change(&row)
		if got := ClearanceHoldReason(row); got != reason {
			t.Fatal(reason, got)
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
		r, err := ReviewUpkeep(UpkeepObservation{Clearance: step.rows}, previous, map[ConcernID]bool{ClearHomeObstructions: step.issued})
		if err != nil || r.History.Clearance != step.active {
			t.Fatal(r, err)
		}
		previous = r.History
	}
}

func TestClearanceAdmissionFollowsRepairs(t *testing.T) {
	f := stableRounds()
	f.Upkeep.Clearance = domain.Known([]ClearanceTarget{{EntityID: "ruin", InHome: true, Deconstructible: true}})
	f.Upkeep.Structures = domain.Known([]UpkeepStructure{{ID: "door", Home: true, HitPoints: 50, MaxHitPoints: 100}})
	f.Upkeep.Filth = domain.Unknown[[]UpkeepFilth]()
	previous := RoundsLatches{}
	check := func(repair bool) {
		r := needs(t, f, previous)
		found := map[ConcernID]bool{}
		for _, g := range r.Concerns {
			found[g.ID] = true
			if g.ID == ClearHomeObstructions && g.MethodUnavailable != repair {
				t.Fatal("clearance did not defer to repairs", g)
			}
		}
		if !found[ClearHomeObstructions] {
			t.Fatal(found)
		}
	}
	check(true)
	f.Upkeep.Structures = domain.Known([]UpkeepStructure{})
	check(false)
}

// While the Foothold shelter is unmet the housing latch sits at HousingShelter
// and clearance waits with it (#2814); once the shelter stands it is released.
func TestClearanceHeldWhileInitialShelterOwed(t *testing.T) {
	f := stableRounds()
	f.Upkeep.Clearance = domain.Known([]ClearanceTarget{{EntityID: "ruin", InHome: true, Deconstructible: true}})
	held := func() bool {
		r := needs(t, f, RoundsLatches{})
		for _, g := range r.Concerns {
			if g.ID == ClearHomeObstructions {
				return g.MethodUnavailable
			}
		}
		t.Fatal("clearance not raised")
		return false
	}
	if held() {
		t.Fatal("clearance held with the shelter standing")
	}
	f.IndoorCapacity = domain.Known(int64(0))
	if !held() {
		t.Fatal("clearance not held while the shelter is owed")
	}
}

func TestChunkHoldsAndPendingDeficit(t *testing.T) {
	chunk := func(id string, forbidden, stored, destination bool) ClearanceChunk {
		return ClearanceChunk{EntityID: id, DefName: "ChunkGranite", Cell: domain.Cell{X: 1, Z: 1}, Forbidden: forbidden, Stored: stored, Destination: destination}
	}
	rows := []ClearanceChunk{chunk("b", false, false, false), chunk("forbidden", true, false, false), chunk("stored", false, true, false), chunk("hauling", false, false, true), chunk("a", false, false, false)}
	for _, row := range rows[1:4] {
		if ChunkHoldReason(row) != row.EntityID {
			t.Fatal(row)
		}
	}
	pending := PendingChunks(rows)
	if len(pending) != 2 || pending[0].EntityID != "a" || pending[1].EntityID != "b" {
		t.Fatal(pending)
	}
	r, err := ReviewUpkeep(UpkeepObservation{Clearance: domain.Known([]ClearanceTarget{}), Chunks: domain.Known(rows)}, UpkeepHistory{}, nil)
	if err != nil || !r.History.Clearance {
		t.Fatal(r, err)
	}
	// A chunk a store takes stays a deficit until stored: it still needs
	// ordinary hauling to carry it (#702, #764, #2513).
	r, err = ReviewUpkeep(UpkeepObservation{Clearance: domain.Known([]ClearanceTarget{}), Chunks: domain.Known(rows[3:4])}, UpkeepHistory{}, nil)
	if err != nil || !r.History.Clearance {
		t.Fatal(r, err)
	}
	r, err = ReviewUpkeep(UpkeepObservation{Clearance: domain.Known([]ClearanceTarget{}), Chunks: domain.Known(rows[1:3])}, UpkeepHistory{}, nil)
	if err != nil || r.History.Clearance {
		t.Fatal(r, err)
	}
	if _, err = ReviewUpkeep(UpkeepObservation{Clearance: domain.Known([]ClearanceTarget{{EntityID: "a", InHome: true, Deconstructible: true}}), Chunks: domain.Known(rows)}, UpkeepHistory{}, nil); err == nil {
		t.Fatal("duplicate identity across buildings and chunks")
	}
}
