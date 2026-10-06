package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// freshColonyStockpiles is a 40x40 map at the first review after takeover:
// open ground, no zone of any kind, a roofed 4x4 overhang at (30,30) and
// the colonists gathered at (10,10).
func freshColonyStockpiles() StockpileRequest {
	r := StockpileRequest{Tick: 39, Bounds: Bounds{Width: 40, Height: 40}, Colonists: domain.Known(int64(3)), Anchor: domain.Cell{X: 10, Z: 10}, Opening: true}
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 40; z++ {
			roofed := x >= 30 && x < 34 && z >= 30 && z < 34
			r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(roofed), Indoors: domain.Known(false), StorageEmpty: domain.Known(true)})
		}
	}
	return r
}

// The first review of a fresh colony stands the general store near the
// colonists in one review, none deferred by the haul budget. The food stockpile
// is a planner site (storage_plan_food_test.go) and the dump a declared
// Sanitation store (incineration_test.go).
func TestFreshColonyFirstReviewAdmitsOpeningStockpiles(t *testing.T) {
	review := PlanStockpileMaintenance(freshColonyStockpiles())
	if len(review.Edits) != 1 || !review.Active || review.Deferred != 0 {
		t.Fatalf("review %+v", review)
	}
	general := review.Edits[0]
	if general.Kind != StockpileCreate || general.Role != domain.OpeningGeneralRole || general.Filter != domain.OpeningStoreFilter() || general.Priority != domain.NormalPriority || len(general.Cells) != 25 {
		t.Fatalf("general %+v", general)
	}
	for _, c := range general.Cells {
		if absInt32(c.X-10) > 4 || absInt32(c.Z-10) > 4 {
			t.Fatalf("general store not near the colony: %v", general.Cells)
		}
	}
}

// Once a general store stands, no opening zone is proposed again.
func TestOpeningStockpilesStopOnceStanding(t *testing.T) {
	r := freshColonyStockpiles()
	r.Zones = []StockpileZone{
		{ID: "Zone_1", Role: domain.GeneralRole, Cells: []domain.Cell{{X: 0, Z: 0}}, Filter: domain.GeneralFilter(), Priority: domain.NormalPriority},
	}
	for _, e := range PlanStockpileMaintenance(r).Edits {
		if e.Kind == StockpileCreate {
			t.Fatalf("opening zone proposed again: %+v", e)
		}
	}
}
