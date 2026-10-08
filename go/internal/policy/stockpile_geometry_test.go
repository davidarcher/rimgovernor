package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRoomStockpileFragmentsConsolidateAfterBlockersClear(t *testing.T) {
	for _, role := range []string{"morgue:135_35", "general", "yard"} {
		t.Run(role, func(t *testing.T) {
			filter := domain.GeneralFilter()
			zones := []StockpileZone{
				{ID: "Zone_1", Role: role, Cells: stockpileRect(10, 10, 1, 1), Filter: filter, Priority: domain.NormalPriority},
				{ID: "Zone_2", Role: role, Cells: stockpileRect(14, 10, 1, 1), Filter: filter, Priority: domain.NormalPriority},
				{ID: "Zone_3", Role: role, Cells: stockpileRect(14, 12, 1, 1), Filter: filter, Priority: domain.NormalPriority},
				{ID: "PlayerZone", Cells: stockpileRect(11, 12, 1, 1)},
			}
			r := stockpileField(zones...)
			r.Stores = []Store{{StoreSite: StoreSite{Role: role, Interior: Rectangle{X: 10, Z: 10, Width: 5, Height: 3}, Filter: filter, Priority: domain.NormalPriority,
				Avoid: []domain.Cell{{X: 10, Z: 12}}, exact: true}}}
			r.Protected = []domain.Cell{{X: 13, Z: 12}}
			for i := range r.Cells {
				c := &r.Cells[i]
				if c.Cell.X == 12 {
					c.Walkable = domain.Known(false)
				}
				if c.Cell == (domain.Cell{X: 13, Z: 11}) {
					c.Walkable = domain.Unknown[bool]()
				}
			}
			// The survivor can use its side, but cannot absorb disconnected zones.
			for range 3 {
				review := PlanStockpileMaintenance(r)
				for _, edit := range review.Edits {
					if edit.Kind != StockpileGrow {
						t.Fatalf("blocked room merged a disconnected fragment: %+v", edit)
					}
				}
				applyGeometryEdits(t, &r, review.Edits)
			}
			for i := range r.Cells {
				if r.Cells[i].Cell.X == 12 {
					r.Cells[i].Walkable = domain.Known(true)
				}
			}
			for range 6 {
				review := PlanStockpileMaintenance(r)
				if !review.Active {
					break
				}
				applyGeometryEdits(t, &r, review.Edits)
			}
			if len(r.Zones) != 2 || r.Zones[0].ID != "Zone_1" || len(r.Zones[0].Cells) != 11 || r.Zones[1].ID != "PlayerZone" {
				t.Fatalf("consolidated room: %+v", r.Zones)
			}
			if review := PlanStockpileMaintenance(r); review.Active {
				t.Fatalf("settled room still edits: %+v", review)
			}
		})
	}
}

func applyGeometryEdits(t *testing.T, r *StockpileRequest, edits []StockpileEdit) {
	t.Helper()
	for _, edit := range edits {
		found := -1
		for i := range r.Zones {
			if r.Zones[i].ID == edit.Zone {
				found = i
			}
		}
		if found < 0 || edit.Zone == "PlayerZone" {
			t.Fatalf("edit targets unrelated zone: %+v", edit)
		}
		cells := cellSet(r.Zones[found].Cells)
		switch edit.Kind {
		case StockpileGrow:
			cells = cellSet(edit.AddedCells)
			r.Zones[found].Cells = append(r.Zones[found].Cells, edit.AddedCells...)
		case StockpileDelete:
			r.Zones = append(r.Zones[:found], r.Zones[found+1:]...)
		default:
			t.Fatalf("unexpected geometry edit: %+v", edit)
		}
		for i := range r.Cells {
			if cells[r.Cells[i].Cell] {
				r.Cells[i].Zone = domain.Known(edit.Kind == StockpileGrow)
			}
		}
	}
}

func TestStockpileGeometryKeepsSeparateSitesAndWaitsForSettings(t *testing.T) {
	filter := domain.GeneralFilter()
	zones := []StockpileZone{
		{ID: "Zone_1", Role: "general", Cells: stockpileRect(10, 10, 1, 1), Filter: filter, Priority: domain.NormalPriority},
		{ID: "Zone_2", Role: "general", Cells: stockpileRect(20, 20, 1, 1), Filter: filter, Priority: domain.NormalPriority},
	}
	r := stockpileField(zones...)
	for _, x := range []int32{10, 20} {
		r.Stores = append(r.Stores, Store{StoreSite: StoreSite{Role: "general", Interior: Rectangle{X: x, Z: x, Width: 2, Height: 2}, Filter: filter, Priority: domain.NormalPriority}})
	}
	review := PlanStockpileMaintenance(r)
	if len(review.Edits) != 2 {
		t.Fatalf("separate rooms: %+v", review)
	}
	for _, edit := range review.Edits {
		if edit.Kind != StockpileGrow || len(edit.AddedCells) != 3 {
			t.Fatalf("separate room merged or grew outside its site: %+v", edit)
		}
	}
	r.Zones[0].Priority = domain.LowPriority
	review = PlanStockpileMaintenance(r)
	for _, edit := range review.Edits {
		if edit.Zone == "Zone_1" && edit.Kind != StockpileRetarget {
			t.Fatalf("geometry ran before settings reconciled: %+v", edit)
		}
	}
	r.Stores[0].Width, r.Stores[0].Height = 1, 1
	r.Zones[0].Priority = domain.NormalPriority
	review = PlanStockpileMaintenance(r)
	for _, edit := range review.Edits {
		if edit.Zone == "Zone_1" {
			t.Fatalf("fixed-size store grew: %+v", edit)
		}
	}
}
