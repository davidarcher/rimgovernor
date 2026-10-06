package policy

import (
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var gearTestItems = ItemFacts{Armor: []Resource{"Apparel_FlakVest", "Apparel_PlateArmor"}}

// gearField is a 100x100 open map with a shelter, an armory and a wardrobe
// standing as roofed rooms (and a prison when set), under a gear store with
// the catalog's armor split.
func gearField(t *testing.T, prison *PlannedRoom) StoreView {
	t.Helper()
	layout := LayoutPlan{Rooms: []PlannedRoom{
		{Role: PlannedShelter, Interior: Rectangle{X: 40, Z: 40, Width: 7, Height: 5}},
		{Role: PlannedArmory, Interior: Rectangle{X: 52, Z: 40, Width: 5, Height: 5}},
		{Role: PlannedWardrobe, Interior: Rectangle{X: 40, Z: 50, Width: 5, Height: 5}},
	}}
	if prison != nil {
		layout.Rooms = append(layout.Rooms, *prison)
	}
	rooms := RoomObservation{Shapes: testShapes}
	roofed := map[domain.Cell]bool{}
	for i, r := range layout.Rooms {
		rooms.Rooms = append(rooms.Rooms, Room{ID: string(rune('a' + i)), Enclosed: domain.Known(true), Cells: rectCells(r.Interior)})
		for _, c := range rectCells(r.Interior) {
			roofed[c] = true
		}
	}
	var cells []SiteCell
	for _, c := range rectCells(Rectangle{Width: 100, Height: 100}) {
		cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Things: OccupantThings(false), Zone: domain.Known(false),
			Roofed: domain.Known(roofed[c]), Indoors: domain.Known(roofed[c]), StorageEmpty: domain.Known(true)})
	}
	gear, err := NewGearStore(gearTestItems, nil, 0)
	if err != nil {
		t.Fatal(err)
	}
	return StoreView{Bounds: Bounds{Width: 100, Height: 100}, Cells: cells, Layout: &layout, Rooms: &rooms, Gear: &gear}
}

func gearSite(r StoreView, prefix string) (Store, bool) {
	for _, store := range (militaryOwner{}).Stores(r) {
		if len(store.Role) > len(prefix) && store.Role[:len(prefix)] == prefix {
			return store, true
		}
	}
	return Store{}, false
}

// A standing armory is one zone over the whole room, weapons and armor above
// the gear floors, Preferred; the wardrobe the same for clothing. Neither
// stands without its room or without the catalog's armor split.
func TestStoresGearStoresFillTheirRooms(t *testing.T) {
	t.Parallel()
	r := gearField(t, nil)
	armory, ok := gearSite(r, domain.ArmoryRolePrefix)
	armoryFilter, wardrobeFilter, _ := GearFilters(gearTestItems.Armor)
	if !ok || armory.Filter != armoryFilter || armory.Priority != domain.PreferredPriority || armory.Further != PlannedArmory || len(armory.footprint()) != 25 {
		t.Fatalf("armory %+v", armory)
	}
	wardrobe, ok := gearSite(r, domain.WardrobeRolePrefix)
	if !ok || wardrobe.Filter != wardrobeFilter || wardrobe.Priority != domain.PreferredPriority || wardrobe.Further != PlannedWardrobe || len(wardrobe.footprint()) != 25 {
		t.Fatalf("wardrobe %+v", wardrobe)
	}
	rooms := *r.Rooms
	rooms.Rooms = rooms.Rooms[:1]
	r.Rooms = &rooms
	if _, ok := gearSite(r, domain.ArmoryRolePrefix); ok {
		t.Fatal("armory zone planned before the room stands")
	}
	r = gearField(t, nil)
	r.Gear = nil
	if _, ok := gearSite(r, domain.ArmoryRolePrefix); ok {
		t.Fatal("armory zone planned without the catalog's armor split")
	}
}

// The filters split gear by the catalog: weapons and armor defs to the
// armory, every other apparel def to the wardrobe, the floors unchanged.
func TestGearFiltersSplitArmorFromClothingAndKeepTheFloors(t *testing.T) {
	t.Parallel()
	armory, wardrobe, err := GearFilters(gearTestItems.Armor)
	if err != nil {
		t.Fatal(err)
	}
	has := func(rows []domain.FilterSelector, s domain.FilterSelector) bool {
		for _, r := range rows {
			if r == s {
				return true
			}
		}
		return false
	}
	if !has(armory.Allow(), domain.CategoryDef("Weapons")) || !has(armory.Allow(), domain.ThingDef("Apparel_PlateArmor")) || has(armory.Allow(), domain.CategoryDef("Apparel")) {
		t.Fatalf("armory allows %v", armory.Allow())
	}
	if !has(wardrobe.Allow(), domain.CategoryDef("Apparel")) || !has(wardrobe.Disallow(), domain.ThingDef("Apparel_FlakVest")) || has(wardrobe.Allow(), domain.CategoryDef("Weapons")) {
		t.Fatalf("wardrobe allows %v disallows %v", wardrobe.Allow(), wardrobe.Disallow())
	}
	for _, f := range []domain.StockpileFilter{armory, wardrobe} {
		if lo, hi, ok := f.HitPoints(); !ok || lo != domain.GearHitPointFloor || hi != 1 {
			t.Fatalf("hit points %v %v %v", lo, hi, ok)
		}
		if lo, hi, ok := f.Quality(); !ok || lo != domain.GearQualityFloor || hi != "Legendary" {
			t.Fatalf("quality %v %v %v", lo, hi, ok)
		}
	}
	if !has(armory.Disallow(), domain.SpecialFilter("AllowBiocodedWeapons")) || !has(wardrobe.Disallow(), domain.SpecialFilter("AllowDeadmansApparel")) {
		t.Fatal("biocoded weapons and tainted apparel are refused")
	}
	for _, f := range []domain.StockpileFilter{armory, wardrobe} {
		if !has(f.Disallow(), domain.SpecialFilter(domain.BurnableFilterDef)) {
			t.Fatalf("a gear store takes burnable gear: disallows %v", f.Disallow())
		}
	}
	if !domain.IsArmoryFilter(armory) || domain.IsArmoryFilter(wardrobe) || !domain.IsWardrobeFilter(wardrobe) || domain.IsWardrobeFilter(armory) {
		t.Fatal("the label shapes tell the stores apart")
	}
}

// Weapons keep their away-from-prison rule: the armory zone leaves out every
// cell within the weapon clearance of a prison; the wardrobe is unaffected.
func TestStoresArmoryKeepsAwayFromPrisons(t *testing.T) {
	t.Parallel()
	prison := PlannedRoom{Role: PlannedPrison, Interior: Rectangle{X: 60, Z: 40, Width: 3, Height: 3}}
	r := gearField(t, &prison)
	armory, ok := gearSite(r, domain.ArmoryRolePrefix)
	if !ok {
		t.Fatal("no armory site")
	}
	pool := armory.footprint()
	if len(pool) == 0 || len(pool) >= 25 {
		t.Fatalf("armory pool %d cells: some, not all, are clear of the prison", len(pool))
	}
	if nearPrison(pool, PrisonCells(*r.Layout)) {
		t.Fatalf("armory cell near the prison: %v", pool)
	}
	wardrobe, _ := gearSite(r, domain.WardrobeRolePrefix)
	if len(wardrobe.footprint()) != 25 {
		t.Fatalf("wardrobe pool %d", len(wardrobe.footprint()))
	}
}

// An armory whose every free cell is within the weapon clearance of a prison
// is a named failure of the plan, not a silent absence of the zone (#1805).
func TestStoresNamesAnArmoryNearAPrison(t *testing.T) {
	t.Parallel()
	prison := PlannedRoom{Role: PlannedPrison, Interior: Rectangle{X: 58, Z: 40, Width: 3, Height: 3}}
	r := gearField(t, &prison)
	if err := (militaryOwner{}).StoreErr(r); !errors.Is(err, ErrArmoryNearPrison) {
		t.Fatalf("plan error %v", err)
	}
	if _, ok := gearSite(r, domain.ArmoryRolePrefix); ok {
		t.Fatal("an armory with no clear cell has no store")
	}
	if err := DeclareStores(r).Err; !errors.Is(err, ErrArmoryNearPrison) {
		t.Fatalf("declaration error %v", err)
	}
	if err := (militaryOwner{}).StoreErr(gearField(t, nil)); err != nil {
		t.Fatalf("plan error %v with no prison", err)
	}
}

// With the armory standing the maintenance pass fills it; the same for the
// wardrobe. A planned room not yet standing is owed its shell.
func TestMaintenanceFillsGearRooms(t *testing.T) {
	t.Parallel()
	r := gearField(t, nil)
	request := StockpileRequest{Tick: 100, Bounds: r.Bounds, Cells: r.Cells, Stores: (militaryOwner{}).Stores(r)}
	review := PlanStockpileMaintenance(request)
	created := map[string]int{}
	for _, e := range review.Edits {
		if e.Kind == StockpileCreate {
			created[stockpileRolePrefix(e.Role)] = len(e.Cells)
		}
	}
	if created["armory"] != 25 || created["wardrobe"] != 25 {
		t.Fatalf("edits %+v", review.Edits)
	}
	request.Zones = nil
	request.Stores = nil
	request.Rooms = []PlannedRole{PlannedArmory}
	review = PlanStockpileMaintenance(request)
	if !review.Active || len(review.Edits) != 0 || len(review.Rooms) != 1 || review.Rooms[0] != PlannedArmory {
		t.Fatalf("owed rooms %+v", review)
	}
}
