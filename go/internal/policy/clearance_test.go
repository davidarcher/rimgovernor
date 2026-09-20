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
		"roof_blocker":   func(r *ClearanceTarget) { r.RoofBlocker = "unsupported" },
		"ancient_danger": func(r *ClearanceTarget) { r.AncientDanger = true },
		"casket":         func(r *ClearanceTarget) { r.Class = "ancient_casket" },
		"outside_home":   func(r *ClearanceTarget) { r.InHome = false },
	} {
		row := base
		row.EntityID = reason
		change(&row)
		rows = append(rows, row)
	}
	got := SelectHomeClearance(rows, domain.Cell{X: 10, Z: 10})
	if len(got.Targets) != 1 || got.Targets[0].EntityID != "near" || len(got.Holds) != 4 {
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
	r, err = ReviewUpkeep(UpkeepObservation{Clearance: domain.Known([]ClearanceTarget{}), Chunks: domain.Known(rows[1:4])}, UpkeepHistory{}, nil)
	if err != nil || r.History.Clearance {
		t.Fatal(r, err)
	}
	if _, err = ReviewUpkeep(UpkeepObservation{Clearance: domain.Known([]ClearanceTarget{{EntityID: "a", InHome: true, Deconstructible: true}}), Chunks: domain.Known(rows)}, UpkeepHistory{}, nil); err == nil {
		t.Fatal("duplicate identity across buildings and chunks")
	}
}

func TestSelectChunkDumpSizesAndConnectsFootprint(t *testing.T) {
	var rows []ClearanceChunk
	for i := 0; i < 6; i++ {
		rows = append(rows, ClearanceChunk{EntityID: string(rune('a' + i)), DefName: "ChunkGranite"})
	}
	rows[0].DefName = "ChunkSandstone"
	rows[5].Destination = true
	// A held footprint splits the native flood; only the run from the first
	// free cell is kept, and it is bounded by the pending count.
	var sites []domain.Cell
	for x := int32(0); x < 12; x++ {
		sites = append(sites, domain.Cell{X: x, Z: 0})
	}
	cells, allow, ok := SelectChunkDump(rows, sites, []domain.Cell{{X: 3, Z: 0}})
	if !ok || len(cells) != 3 || cells[0] != (domain.Cell{X: 0, Z: 0}) || cells[2] != (domain.Cell{X: 2, Z: 0}) {
		t.Fatal(cells, ok)
	}
	if len(allow) != 3 || allow[0] != "ChunkGranite" || allow[1] != "ChunkSandstone" || allow[2] != "ChunkSlagSteel" {
		t.Fatal(allow)
	}
	if cells, _, ok = SelectChunkDump(rows[:1], sites, nil); !ok || len(cells) != minChunkDumpCells {
		t.Fatal(cells, ok)
	}
	if _, _, ok = SelectChunkDump(rows[5:], sites, nil); ok {
		t.Fatal("no pending chunk")
	}
	if _, _, ok = SelectChunkDump(rows, sites[:1], sites[:1]); ok {
		t.Fatal("no free cell")
	}
}
