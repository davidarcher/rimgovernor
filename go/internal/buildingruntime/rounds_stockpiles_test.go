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
	projection := &observation.ColonyProjection{Bounds: policy.Bounds{Width: 20, Height: 20}, Facts: policy.RoundsFacts{Colonists: domain.Known(int64(2)), FoodStorage: domain.Known(true)}}
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
	request := stockpileRequest(projection, owned, patches, domain.Unknown[map[string]bool](), nil, nil, nil, nil)
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
// medicine zone retires with its hospital, the general store, covered fallbacks, gear and dumps keep fixed
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
		// Ingredient stores are the Industry department's declared stores.
		{"ingredients:Bench_2", false, false, domain.StockpileFilter{}, ""},
		{domain.GeneralRole, true, false, domain.GeneralFilter(), domain.LowPriority},
		{domain.OpeningGeneralRole, true, false, domain.OpeningStoreFilter(), domain.NormalPriority},
		// The 2x2 gear zones the armory and wardrobe replaced retire (#1774); with
		// no armor in the catalog the new stores publish nothing.
		{domain.ApparelRole, true, true, domain.StockpileFilter{}, ""},
		{domain.WeaponsRole, true, true, domain.StockpileFilter{}, ""},
		{"armory:Room_1", false, false, domain.StockpileFilter{}, ""},
		// A zone left by the removed covered fallback retires (#1778).
		{"covered:WoodLog", true, true, domain.StockpileFilter{}, ""},
		// The four retired dump roles retire; the one dump is a declared store.
		{"dump:worn", true, true, domain.StockpileFilter{}, ""},
		{"dump:other", true, true, domain.StockpileFilter{}, ""},
		// The meal stores are the Food department's declared stores.
		{"meals:Room_1", false, false, domain.StockpileFilter{}, ""},
		{"rawfood:Room_1", true, false, domain.RawFoodFilter(), domain.CriticalPriority},
	} {
		state, ok := roles(tc.role)
		if ok != tc.published || state.Retired != tc.retired || ok && !tc.retired && (state.Filter != tc.filter || state.Priority != tc.priority) {
			t.Errorf("%s: %+v %v", tc.role, state, ok)
		}
	}
	unknown := stockpileRoles(StockpileRoleInput{Projection: &observation.ColonyProjection{}})
	for _, role := range []string{"medicine:Room_1"} {
		if _, ok := unknown(role); ok {
			t.Errorf("%s published over an unknown census", role)
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

// A room shell already being worked must not hold the zone edits back: the
// food stockpile went uncreated for as long as the storage room's shell stood
// unbuilt.
func TestShellLeavesZoneEdits(t *testing.T) {
	for _, v := range []Verdict{BuildingReasonExistingWork, waitFor(WaitMethodUsed, "x"), noSpace("x"), fieldUnavailable("x")} {
		if !shellLeavesZoneEdits(v) {
			t.Errorf("verdict %v holds the zone edits back", v)
		}
	}
	if shellLeavesZoneEdits(BuildingReasonAdmitted) {
		t.Error("an admitted shell lets the zone edits go on the same step")
	}
}

// A room shell is days of building work and must not hold zone edits; only
// zone-edit plans do, whichever owner's method carries them.
func TestIsStockpileEditPlan(t *testing.T) {
	zone, err := domain.NewFilteredStockpileZone(domain.FoodFilter(), domain.ImportantPriority, []domain.Cell{{X: 1, Z: 1}})
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
