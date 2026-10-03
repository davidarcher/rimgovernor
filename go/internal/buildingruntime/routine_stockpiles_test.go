package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func stockpileSiteCell(x, z int32, zone string, stored bool) policy.SiteCell {
	cell := policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(zone != ""), StorageEmpty: domain.Known(!stored)}
	if zone != "" {
		cell.ZoneID = domain.Known(zone)
	}
	return cell
}

// A snapshot over recorded colony facts: the planning cells name each
// zone's cells and the storage-empty flag its used ones; the owned
// stockpile claims (not growing zones, not zones the census lost) become
// the review's zones, a later patch supersedes the created settings and
// role, and a nearly full claimed zone grows through the registered role.
func TestStockpileRequestFromCensusAndClaims(t *testing.T) {
	projection := &observation.ColonyProjection{Bounds: policy.Bounds{Width: 20, Height: 20}, Facts: policy.RoutineFacts{Colonists: domain.Known(int64(2)), FoodStorage: domain.Known(true)}}
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
		{ID: "Zone_1", Kind: domain.StockpileZone, Role: domain.OpeningGeneralRole, Filter: domain.OpeningStoreFilter(), Priority: domain.NormalPriority},
		{ID: "Zone_2", Kind: domain.StockpileZone, Filter: domain.GeneralFilter(), Priority: domain.NormalPriority},
		{ID: "Zone_3", Kind: domain.GrowingZone, Crop: "Plant_Rice"},
		{ID: "Zone_9", Kind: domain.StockpileZone, Role: "general"},
	}
	patches := map[string]store.AppliedStockpile{"Zone_2": {Target: "Zone_2", Kind: domain.StorageZoneTarget, Filter: food, Priority: domain.PreferredPriority, Role: "kitchen", Tick: 10}}
	request := stockpileRequest(projection, owned, patches, domain.Unknown[map[string]bool](), nil, nil)
	if len(request.Zones) != 2 || request.Tick != 5000 {
		t.Fatalf("request zones %+v", request.Zones)
	}
	one, two := request.Zones[0], request.Zones[1]
	if one.ID != "Zone_1" || len(one.Cells) != 4 || one.Used() != 4 || one.Role != domain.OpeningGeneralRole {
		t.Fatalf("zone 1 %+v", one)
	}
	if two.ID != "Zone_2" || len(two.Cells) != 4 || two.Used() != 1 || two.Role != "kitchen" || two.Filter != food || two.Priority != domain.PreferredPriority {
		t.Fatalf("zone 2 %+v", two)
	}
	review := policy.PlanStockpileMaintenance(request)
	if len(review.Edits) != 1 || review.Edits[0].Kind != policy.StockpileGrow || review.Edits[0].Zone != "Zone_1" {
		t.Fatalf("review %+v", review)
	}
}

func TestStockpileMemoryTracksLowSincePerWorld(t *testing.T) {
	var m stockpileMemory
	low := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 8)}}
	m.observe("a", 100, low)
	if low[0].LowSince != 100 {
		t.Fatalf("first low %+v", low[0])
	}
	later := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 8)}}
	m.observe("a", 900, later)
	if later[0].LowSince != 100 {
		t.Fatalf("low since reset %+v", later[0])
	}
	read := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 8)}}
	m.fill("a", read)
	if read[0].LowSince != 100 {
		t.Fatalf("fill %+v", read[0])
	}
	full := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 2), Stored: make([]domain.Cell, 2)}}
	m.observe("a", 1000, full)
	again := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 8)}}
	m.observe("a", 2000, again)
	if again[0].LowSince != 2000 {
		t.Fatalf("filled zone kept its low tick %+v", again[0])
	}
	other := []policy.StockpileZone{{ID: "Zone_1", Cells: make([]domain.Cell, 8)}}
	m.fill("b", other)
	if other[0].LowSince != 0 {
		t.Fatal("memory crossed worlds")
	}
}

func TestStockpileEditActionsCarryTheZoneToken(t *testing.T) {
	cells := []domain.Cell{{X: 1, Z: 1}}
	for _, tc := range []struct {
		edit policy.StockpileEdit
		kind domain.ActionKind
	}{
		{policy.StockpileEdit{Kind: policy.StockpileGrow, Zone: "Zone_1", Cells: cells}, domain.ZoneCellEditAction},
		{policy.StockpileEdit{Kind: policy.StockpileShrink, Zone: "Zone_1", Cells: cells}, domain.ZoneCellEditAction},
		{policy.StockpileEdit{Kind: policy.StockpileRetarget, Zone: "Zone_1", Filter: domain.FoodFilter(), Priority: domain.ImportantPriority, Role: "kitchen"}, domain.StockpilePatchAction},
		{policy.StockpileEdit{Kind: policy.StockpileShelfPatch, Zone: "Shelf_1", Filter: domain.FoodFilter(), Priority: domain.ImportantPriority, Role: "kitchen"}, domain.StockpilePatchAction},
		{policy.StockpileEdit{Kind: policy.StockpileDelete, Zone: "Zone_1"}, domain.ZoneDeleteAction},
		{policy.StockpileEdit{Kind: policy.StockpileMerge, Zone: "Zone_1", Into: "Zone_2"}, domain.ZoneDeleteAction},
	} {
		a, err := stockpileEditAction("a-0", tc.edit)
		if err != nil || a.Kind() != tc.kind {
			t.Fatalf("%s: %v %v", tc.edit.Kind, a.Kind(), err)
		}
		if e, ok := a.ZoneCellEdit(); ok {
			want := domain.AddZoneCells
			if tc.edit.Kind == policy.StockpileShrink {
				want = domain.RemoveZoneCells
			}
			if e.Mode() != want {
				t.Fatalf("edit %+v", e)
			}
		}
		want := domain.StorageZoneTarget
		if tc.edit.Kind == policy.StockpileShelfPatch {
			want = domain.StorageBuildingTarget
		}
		if p, ok := a.StockpilePatch(); ok && (p.Role() != "kitchen" || p.TargetKind() != want || p.Target() != tc.edit.Zone) {
			t.Fatalf("patch %+v", p)
		}
	}
}

func TestStockpileRolesResolveByPrefix(t *testing.T) {
	RegisterStockpileRole("test-role", func(_ StockpileRoleInput, role string) (policy.StockpileRoleState, bool) {
		return policy.StockpileRoleState{Retired: role == "test-role:gone"}, true
	})
	roles := stockpileRoles(StockpileRoleInput{Projection: &observation.ColonyProjection{}})
	if state, ok := roles("test-role:gone"); !ok || !state.Retired {
		t.Fatal("prefixed role unresolved")
	}
	if state, ok := roles("test-role"); !ok || state.Retired {
		t.Fatal("bare role unresolved")
	}
	if _, ok := roles("test-roleX"); ok {
		t.Fatal("unregistered role resolved")
	}
	defer func() {
		if recover() == nil {
			t.Fatal("double registration accepted")
		}
	}()
	RegisterStockpileRole("test-role", func(StockpileRoleInput, string) (policy.StockpileRoleState, bool) {
		return policy.StockpileRoleState{}, false
	})
}

// The registered owners publish every role's desired state (#724/#725): a
// medicine zone retires with its hospital, an ingredients zone with its
// bench, the general store, covered fallbacks, gear and dumps keep fixed
// settings; an unknown census publishes nothing.
func TestStockpileRoleOwnersPublishDesiredState(t *testing.T) {
	projection := &observation.ColonyProjection{}
	projection.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{
		{ID: "Room_1", Role: domain.Known(policy.RoomRoleHospital)},
		{ID: "Room_2", Role: domain.Known(policy.RoomRoleKitchen)},
		{ID: "Room_3", Role: domain.Unknown[policy.RoomRole]()},
	}})
	projection.Facts.Comfort = domain.Known(policy.ComfortObservation{})
	roles := stockpileRoles(StockpileRoleInput{Projection: projection, Benches: domain.Known(map[string]bool{"Bench_1": true})})
	medicine := domain.MedicineFilter()
	for _, tc := range []struct {
		role      string
		published bool
		retired   bool
		filter    domain.StockpileFilter
		priority  domain.StockpilePriority
	}{
		{"medicine:Room_1", true, false, medicine, domain.ImportantPriority},
		{"medicine:Room_2", true, true, medicine, domain.ImportantPriority},
		{"medicine:Room_9", true, true, medicine, domain.ImportantPriority},
		{"medicine:Room_3", false, false, domain.StockpileFilter{}, ""},
		{"ingredients:Bench_1", false, false, domain.StockpileFilter{}, ""},
		{"ingredients:Bench_2", true, true, domain.StockpileFilter{}, ""},
		{domain.GeneralRole, true, false, domain.GeneralFilter(), domain.LowPriority},
		{domain.OpeningGeneralRole, true, false, domain.OpeningStoreFilter(), domain.NormalPriority},
		// The 2x2 gear zones the armory and wardrobe replaced retire (#1774); with
		// no armor in the catalog the new stores publish nothing.
		{domain.ApparelRole, true, true, domain.StockpileFilter{}, ""},
		{domain.WeaponsRole, true, true, domain.StockpileFilter{}, ""},
		{"armory:Room_1", false, false, domain.StockpileFilter{}, ""},
		// A zone left by the removed covered fallback retires (#1778).
		{"covered:WoodLog", true, true, domain.StockpileFilter{}, ""},
		{domain.WornDumpRole, true, false, domain.WornDumpFilter(), domain.LowPriority},
		{domain.RottenDumpRole, true, false, domain.RottenDumpFilter(), domain.LowPriority},
		{domain.CorpseDumpRole, true, false, domain.CorpseDumpFilter(), domain.LowPriority},
		{domain.FreshDumpRole, true, false, domain.CorpseLarderFilter(), domain.LowPriority},
		{"dump:other", false, false, domain.StockpileFilter{}, ""},
		// No dining table and no cold spot: the meal stockpile retires (#936).
		{"meals:Room_1", true, true, domain.StockpileFilter{}, ""},
		{"rawfood:Room_1", true, false, domain.RawFoodFilter(), domain.CriticalPriority},
	} {
		state, ok := roles(tc.role)
		if ok != tc.published || state.Retired != tc.retired || ok && !tc.retired && (state.Filter != tc.filter || state.Priority != tc.priority) {
			t.Errorf("%s: %+v %v", tc.role, state, ok)
		}
	}
	unknown := stockpileRoles(StockpileRoleInput{Projection: &observation.ColonyProjection{}})
	for _, role := range []string{"medicine:Room_1", "ingredients:Bench_2"} {
		if _, ok := unknown(role); ok {
			t.Errorf("%s published over an unknown census", role)
		}
	}
}

// Needs count poor stored apparel and worn-out garments for the worn dump, spoiled items and
// rotting animal corpses for the rotten dump and humanlike corpses for the
// corpse dump; buried corpses wait for nothing.
func TestStockpileNeedsFromColonyFacts(t *testing.T) {
	facts := policy.RoutineFacts{
		Gear: domain.Known(policy.GearObservation{
			Stored: domain.Known([]policy.GearStock{{Definition: "Apparel_Parka", Quality: 2, HPBand: 7, Count: 2}, {Definition: "Apparel_Pants", Quality: 1, HPBand: 9, Count: 1}}),
			Pawns:  []policy.GearPawn{{Apparel: domain.Known([]policy.GearApparel{{Definition: "Apparel_Shirt", Condition: 0.3}, {Definition: "Apparel_Pants", Condition: 0.9}})}},
		}),
		Waste: domain.Known([]policy.WasteItem{
			{Kind: "spoiled", State: policy.WasteExposed},
			{Kind: "corpse", CorpseOf: domain.CorpseAnimal, State: policy.WasteExposed},
			{Kind: "corpse", CorpseOf: domain.CorpseStranger, State: policy.WasteExposed},
			{Kind: "corpse", CorpseOf: domain.CorpseColonist, State: policy.WasteBuried},
		}),
	}
	needs := policy.DumpNeeds(facts)
	want := map[string]int{domain.WornDumpRole: 2, domain.RottenDumpRole: 2, domain.CorpseDumpRole: 1}
	for role, n := range want {
		if needs[role] != n {
			t.Errorf("%s: %d, want %d (%v)", role, needs[role], n, needs)
		}
	}
}

// The armory and wardrobe publish the catalog-split gear filters at Preferred
// once the catalog names armor (#1774).
func TestGearStoreRolesPublishTheCatalogSplit(t *testing.T) {
	projection := &observation.ColonyProjection{}
	projection.Facts.Items = policy.ItemFacts{Armor: []policy.Resource{"Apparel_FlakVest"}}
	armory, wardrobe, err := policy.GearFilters(projection.Facts.Items.Armor)
	if err != nil {
		t.Fatal(err)
	}
	roles := stockpileRoles(StockpileRoleInput{Projection: projection})
	for role, want := range map[string]domain.StockpileFilter{"armory:Room_1": armory, "wardrobe:Room_2": wardrobe} {
		state, ok := roles(role)
		if !ok || state.Retired || !state.Fixed || state.Filter != want || state.Priority != domain.PreferredPriority {
			t.Errorf("%s: %+v %v", role, state, ok)
		}
	}
}
