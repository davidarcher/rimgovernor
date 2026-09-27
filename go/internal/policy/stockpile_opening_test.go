package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// freshColonyStockpiles is a 40x40 map at the first review after takeover:
// open ground, no zone of any kind, a roofed 4x4 overhang at (30,30) and
// the colonists gathered at (10,10).
func freshColonyStockpiles() StockpileRequest {
	r := StockpileRequest{Tick: 39, Bounds: Bounds{Width: 40, Height: 40}, Colonists: domain.Known(int64(3)), Anchor: domain.Cell{X: 10, Z: 10}, Rooms: domain.Unknown[[]Room](), Opening: true}
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 40; z++ {
			roofed := x >= 30 && x < 34 && z >= 30 && z < 34
			r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(roofed), Indoors: domain.Known(false), StorageEmpty: domain.Known(true)})
		}
	}
	return r
}

// The first review of a fresh colony stands the general store near the
// colonists, the food stockpile on the only roofed floor and the corpse
// dump well clear of the shelter, all in one review and none deferred by
// the haul budget.
func TestFreshColonyFirstReviewAdmitsOpeningStockpiles(t *testing.T) {
	review := PlanStockpileMaintenance(freshColonyStockpiles())
	got := map[string]StockpileEdit{}
	for _, e := range review.Edits {
		if e.Kind != StockpileCreate {
			t.Fatalf("unexpected edit %+v", e)
		}
		got[e.Role] = e
	}
	if !review.Active || review.Deferred != 0 || len(got) != 3 {
		t.Fatalf("review %+v", review)
	}
	general := got[domain.GeneralRole]
	if general.Filter != domain.GeneralFilter() || general.Priority != domain.NormalPriority || len(general.Cells) != 25 {
		t.Fatalf("general %+v", general)
	}
	for _, c := range general.Cells {
		if absInt32(c.X-10) > 4 || absInt32(c.Z-10) > 4 {
			t.Fatalf("general store not near the colony: %v", general.Cells)
		}
	}
	food := got[domain.FoodRole]
	if food.Filter != domain.FoodFilter() || food.Priority != domain.PreferredPriority || len(food.Cells) != 9 {
		t.Fatalf("food %+v", food)
	}
	for _, c := range food.Cells {
		if c.X < 30 || c.X >= 34 || c.Z < 30 || c.Z >= 34 {
			t.Fatalf("food not on the roofed floor: %v", food.Cells)
		}
	}
	dump := got[domain.CorpseDumpRole]
	if dump.Filter != domain.CorpseDumpFilter() || dump.Priority != domain.LowPriority || len(dump.Cells) != 9 {
		t.Fatalf("dump %+v", dump)
	}
	for _, c := range dump.Cells {
		if max(absInt32(c.X-10), absInt32(c.Z-10)) < openingDumpDistance {
			t.Fatalf("dump inside the shelter's clearance: %v", dump.Cells)
		}
	}
}

// With no roofed floor the food stockpile sits beside the cooking spot;
// once zones of every kind stand, including a planner's unroled food zone,
// no opening zone is proposed again.
func TestOpeningStockpilesFollowTheKitchenAndStopOnceStanding(t *testing.T) {
	r := freshColonyStockpiles()
	for i := range r.Cells {
		r.Cells[i].Roofed = domain.Known(false)
	}
	kitchen := domain.Cell{X: 20, Z: 5}
	r.Kitchen = &kitchen
	review := PlanStockpileMaintenance(r)
	var food StockpileEdit
	for _, e := range review.Edits {
		if e.Role == domain.FoodRole {
			food = e
		}
	}
	if len(food.Cells) != 9 {
		t.Fatalf("no food stockpile: %+v", review.Edits)
	}
	for _, c := range food.Cells {
		if absInt32(c.X-kitchen.X) > 2 || absInt32(c.Z-kitchen.Z) > 2 {
			t.Fatalf("food not beside the cooking spot: %v", food.Cells)
		}
	}
	r.Zones = []StockpileZone{
		{ID: "Zone_1", Role: domain.GeneralRole, Cells: []domain.Cell{{X: 0, Z: 0}}, Filter: domain.GeneralFilter(), Priority: domain.NormalPriority},
		{ID: "Zone_2", Cells: []domain.Cell{{X: 0, Z: 2}}, Filter: domain.FoodFilter(), Priority: domain.ImportantPriority},
		{ID: "Zone_3", Role: domain.CorpseDumpRole, Cells: []domain.Cell{{X: 0, Z: 4}}, Filter: domain.CorpseDumpFilter(), Priority: domain.LowPriority},
	}
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Kind == StockpileCreate {
			t.Fatalf("opening zone proposed again: %+v", e)
		}
	}
}
