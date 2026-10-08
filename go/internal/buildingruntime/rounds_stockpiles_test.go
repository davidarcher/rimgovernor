package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func stockpileSiteCell(x, z int32, zone string, stored bool) policy.SiteCell {
	cell := policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Things: policy.OccupantThings(false), Zone: domain.Known(zone != ""), StorageEmpty: domain.Known(!stored)}
	if zone != "" {
		cell.ZoneID = domain.Known(zone)
	}
	return cell
}

// A snapshot over recorded colony facts: the planning cells name each
// zone's cells and the storage-empty flag its used ones; the owned
// stockpile claims (not field zones, not zones the census lost) become
// the review's zones, a later patch supersedes the created settings and
// role
func TestStockpileRequestFromCensusAndClaims(t *testing.T) {
	projection := &observation.ColonyProjection{Bounds: policy.Bounds{Width: 20, Height: 20}, Facts: policy.RoundsFacts{Colonists: domain.Known(int64(2))}}
	projection.Identity.Tick = 5000
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			zone := ""
			switch {
			case x >= 2 && x < 4 && z >= 2 && z < 4:
				zone = "Zone_1"
			case x >= 10 && x < 12 && z >= 10 && z < 12:
				zone = "Zone_2"
			case x == 15 && z == 15:
				zone = "Zone_3"
			}
			projection.Cells = append(projection.Cells, stockpileSiteCell(x, z, zone, zone == "Zone_1" || zone == "Zone_2" && x == 10 && z == 10))
		}
	}
	food := domain.FoodFilter()
	owned := []store.OwnedZone{
		{ID: "Zone_1", Kind: domain.StockpileZone, Role: "general", Filter: domain.OpeningStoreFilter(), Priority: domain.NormalPriority},
		{ID: "Zone_2", Kind: domain.StockpileZone, Filter: domain.GeneralFilter(), Priority: domain.NormalPriority},
		{ID: "Zone_3", Kind: domain.GrowingZone, Crop: "Plant_Rice"},
		{ID: "Zone_9", Kind: domain.StockpileZone, Role: "general"},
	}
	patches := map[string]store.AppliedStockpile{"Zone_2": {Target: "Zone_2", Kind: domain.StorageZoneTarget, Filter: food, Priority: domain.PreferredPriority, Role: "kitchen", Tick: 10}}
	request := stockpileRequest(projection, owned, patches, domain.Unknown[map[string]bool](), nil, nil, nil, nil)
	if len(request.Zones) != 2 || request.Tick != 5000 {
		t.Fatalf("request zones %+v", request.Zones)
	}
	one, two := request.Zones[0], request.Zones[1]
	if one.ID != "Zone_1" || len(one.Cells) != 4 || one.Used() != 4 || one.Role != "general" {
		t.Fatalf("zone 1 %+v", one)
	}
	if two.ID != "Zone_2" || len(two.Cells) != 4 || two.Used() != 1 || two.Role != "kitchen" || two.Filter != food || two.Priority != domain.PreferredPriority {
		t.Fatalf("zone 2 %+v", two)
	}
	review := policy.PlanStockpileMaintenance(request)
	if len(review.Edits) != 0 {
		t.Fatalf("review %+v", review)
	}
}

func TestStockpileEditActionsCarryTheZoneToken(t *testing.T) {
	for _, tc := range []struct {
		edit policy.StockpileEdit
		kind domain.ActionKind
	}{
		{policy.StockpileEdit{Kind: policy.StockpileRetarget, Zone: "Zone_1", Filter: domain.FoodFilter(), Priority: domain.ImportantPriority, Role: "kitchen"}, domain.StockpilePatchAction},
		{policy.StockpileEdit{Kind: policy.StockpileShelfPatch, Zone: "Shelf_1", Filter: domain.FoodFilter(), Priority: domain.ImportantPriority, Role: "kitchen"}, domain.StockpilePatchAction},
		{policy.StockpileEdit{Kind: policy.StockpileDelete, Zone: "Zone_1"}, domain.ZoneDeleteAction},
		{policy.StockpileEdit{Kind: policy.StockpileGrow, Zone: "Zone_1", AddedCells: []domain.Cell{{X: 2, Z: 3}}}, domain.ZoneCellEditAction},
	} {
		a, err := stockpileEditAction("a-0", tc.edit)
		if err != nil || a.Kind() != tc.kind {
			t.Fatalf("%s: %v %v", tc.edit.Kind, a.Kind(), err)
		}
		want := domain.StorageZoneTarget
		if tc.edit.Kind == policy.StockpileShelfPatch {
			want = domain.StorageBuildingTarget
		}
		if p, ok := a.StockpilePatch(); ok && (p.Role() != "kitchen" || p.TargetKind() != want || p.Target() != tc.edit.Zone) {
			t.Fatalf("patch %+v", p)
		}
		if edit, ok := a.ZoneCellEdit(); ok && (edit.Zone() != tc.edit.Zone || edit.Mode() != domain.AddZoneCells || len(edit.Cells()) != 1) {
			t.Fatalf("zone growth %+v", edit)
		}
	}
}

// A room shell is days of building work and must not hold zone edits; only
// zone-edit plans do, whichever owner's method carries them.
func TestIsStockpileEditPlan(t *testing.T) {
	zone, err := domain.NewFilteredStockpileZone(domain.FoodFilter(), domain.ImportantPriority, stockpileTestRectangle([]domain.Cell{{X: 1, Z: 1}}))
	if err != nil {
		t.Fatal(err)
	}
	za, err := domain.NewZoneCreateAction("z", zone)
	if err != nil {
		t.Fatal(err)
	}
	b, err := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 3}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	ba, err := domain.NewBuildingAction("b", b)
	if err != nil {
		t.Fatal(err)
	}
	for _, c := range []struct {
		a    domain.Action
		want bool
	}{{za, true}, {ba, false}} {
		plan, err := domain.NewPlan("p", 1, []domain.Action{c.a})
		if err != nil {
			t.Fatal(err)
		}
		if isStockpileEditPlan(plan) != c.want {
			t.Errorf("%s: want %v", c.a.Kind(), c.want)
		}
	}
}

// Loose sleeping spots and the basic-comfort table must not land in a planned
// storage room: it was the starter shell's room once, and furniture there
// carves up the warehouse zone.
func TestNonSleepingPlannedCellsCloseStorage(t *testing.T) {
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{
		{Role: policy.PlannedStorage, Interior: policy.Rectangle{X: 10, Z: 10, Width: 3, Height: 3}},
		{Role: policy.PlannedShelter, Interior: policy.Rectangle{X: 30, Z: 30, Width: 3, Height: 3}},
		{Role: policy.PlannedShelter, Interior: policy.Rectangle{X: 50, Z: 50, Width: 3, Height: 3}},
		{Role: policy.PlannedBarn, Interior: policy.Rectangle{X: 70, Z: 70, Width: 3, Height: 3}},
		{Role: policy.PlannedVetRoom, Interior: policy.Rectangle{X: 90, Z: 90, Width: 3, Height: 3}},
	}}
	facts := observation.ColonyProjection{LayoutPlan: domain.Known(plan)}
	cells := map[domain.Cell]bool{}
	for _, c := range nonSleepingPlannedCells(facts) {
		cells[c] = true
	}
	if !cells[domain.Cell{X: 11, Z: 11}] {
		t.Error("the planned storage room is open to loose spots and furniture")
	}
	if cells[domain.Cell{X: 31, Z: 31}] {
		t.Error("the starter shell (barracks) is closed to loose spots")
	}
	if cells[domain.Cell{X: 51, Z: 51}] {
		t.Error("the shelter is closed to loose spots")
	}
	if !cells[domain.Cell{X: 71, Z: 71}] || !cells[domain.Cell{X: 91, Z: 91}] {
		t.Error("the animal barn and vet room are open to loose colonist spots (they must be protected)")
	}
}

// Loose sleeping spots are allowlisted: only the shelter, bedrooms and
// suites are open to them, every other planned room is not.
func TestSleepingPlannedCellsAllowlist(t *testing.T) {
	at := func(role policy.PlannedRole, x int32) policy.PlannedRoom {
		return policy.PlannedRoom{Role: role, Interior: policy.Rectangle{X: x, Z: 10, Width: 3, Height: 3}}
	}
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{
		at(policy.PlannedShelter, 10), at(policy.PlannedBedroom, 20), at(policy.PlannedSuite, 30),
		at(policy.PlannedBarn, 40), at(policy.PlannedKitchen, 50), at(policy.PlannedReserve, 60),
	}}
	cells := map[domain.Cell]bool{}
	for _, c := range sleepingPlannedCells(observation.ColonyProjection{LayoutPlan: domain.Known(plan)}) {
		cells[c] = true
	}
	for x, want := range map[int32]bool{11: true, 21: true, 31: true, 41: false, 51: false, 61: false} {
		if cells[domain.Cell{X: x, Z: 11}] != want {
			t.Errorf("cell x=%d open to loose spots = %v, want %v", x, !want, want)
		}
	}
}
