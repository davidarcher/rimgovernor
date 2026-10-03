package policy

import "testing"

func gearTestPlan() LayoutPlan {
	return Grow(LayoutPlan{Zones: coreTestZones()}, 6, 1, BuildTierCamp)
}

func roomOf(p LayoutPlan, role ModuleRole) (LayoutRoom, bool) {
	for _, r := range p.Rooms {
		if r.Role == role {
			return r, true
		}
	}
	return LayoutRoom{}, false
}

// touches reports two rooms sharing a wall: their interiors a wall apart.
func touches(a, b Rectangle) bool {
	xGap := max(a.X-(b.X+b.Width), b.X-(a.X+a.Width))
	zGap := max(a.Z-(b.Z+b.Height), b.Z-(a.Z+a.Height))
	return xGap == 1 && zGap < 0 || zGap == 1 && xGap < 0
}

func TestGearRoomsNeedDemand(t *testing.T) {
	t.Parallel()
	plan := gearTestPlan()
	for _, role := range gearRooms {
		if _, ok := roomOf(plan, role); ok {
			t.Fatalf("%s planned with no demand", role)
		}
	}
	if owed := GearRoomsOwed(plan, RoomDemand{}); len(owed) != 0 {
		t.Fatalf("owed %v with no demand", owed)
	}
	if same, added, _ := growGearRooms(plan, RoomDemand{}); added || len(same.Rooms) != len(plan.Rooms) {
		t.Fatal("rooms added with no demand")
	}
}

func TestGearRoomsSitBesideTheirAnchorAndRoute(t *testing.T) {
	t.Parallel()
	plan := gearTestPlan()
	grown, added, _ := growGearRooms(plan, RoomDemand{Armory: true, Wardrobe: true})
	if !added || len(grown.Rooms) != len(plan.Rooms)+2 {
		t.Fatalf("added=%v rooms %d -> %d", added, len(plan.Rooms), len(grown.Rooms))
	}
	for i := range plan.Rooms {
		if grown.Rooms[i] != plan.Rooms[i] {
			t.Fatalf("room %d moved", i)
		}
	}
	for role, anchor := range map[ModuleRole]ModuleRole{ModuleArmory: ModuleBarracks, ModuleWardrobe: ModuleWorkshop} {
		room, ok := roomOf(grown, role)
		host, hok := roomOf(grown, anchor)
		if !ok || !hok {
			t.Fatalf("%s %v %s %v", role, ok, anchor, hok)
		}
		if !touches(room.Interior, host.Interior) {
			t.Errorf("%s %+v not beside %s %+v", role, room.Interior, anchor, host.Interior)
		}
		if room.Link != nil && !inWall(host.Interior, *room.Link) {
			t.Errorf("%s link %v is not in the %s wall", role, *room.Link, anchor)
		}
	}
	if _, err := CheckRoutes(grown); err != nil {
		t.Fatal(err)
	}
	if again, added, _ := growGearRooms(grown, RoomDemand{Armory: true, Wardrobe: true}); added || len(again.Rooms) != len(grown.Rooms) {
		t.Fatal("a standing room answers its demand for good")
	}
}

func TestGearRoomRolesAreRegistered(t *testing.T) {
	t.Parallel()
	for _, role := range gearRooms {
		if _, ok := coreRoomSize[role]; !ok {
			t.Errorf("%s has no size", role)
		}
		if moduleRoomRoles[role] != RoomRoleStoreroom || roomDistricts[role] != DistrictStorage {
			t.Errorf("%s role %v district %v", role, moduleRoomRoles[role], roomDistricts[role])
		}
		if _, ok := roomOverlay[role]; !ok {
			t.Errorf("%s has no overlay style", role)
		}
	}
}

func TestPlanStorageSignalsGearDemand(t *testing.T) {
	t.Parallel()
	items := ItemFacts{Armor: []Resource{"Apparel_FlakVest"}}
	stock := func(def Resource, quality, hp, count int) GearStock {
		return GearStock{Definition: def, Quality: quality, HPBand: hp, Count: count}
	}
	for _, tc := range []struct {
		name    string
		stored  []GearStock
		weapons int
		want    RoomDemand
	}{
		{"nothing", nil, 0, RoomDemand{Known: true}},
		{"room to spare", []GearStock{stock("Apparel_FlakVest", 2, 9, 1), stock("Apparel_Parka", 2, 9, 3)}, 2, RoomDemand{Known: true}},
		{"weapons and armor fill the armory", []GearStock{stock("Apparel_FlakVest", 2, 9, 2)}, 2, RoomDemand{Known: true, Armory: true}},
		{"clothing fills the wardrobe", []GearStock{stock("Apparel_Parka", 2, 9, 2), stock("Apparel_Duster", 3, 10, 2)}, 0, RoomDemand{Known: true, Wardrobe: true}},
		{"poor and worn gear is for the dump", []GearStock{stock("Apparel_Parka", 1, 9, 4), stock("Apparel_FlakVest", 2, 4, 4)}, 0, RoomDemand{Known: true}},
	} {
		gear, ok, err := NewGearStore(items, tc.stored, tc.weapons)
		if err != nil || !ok {
			t.Fatalf("%s: store %v %v", tc.name, ok, err)
		}
		if got := PlanStorage(StorageRequest{Gear: &gear}).RoomDemand; got != tc.want {
			t.Errorf("%s: %+v, want %+v", tc.name, got, tc.want)
		}
	}
	if _, ok, err := NewGearStore(ItemFacts{}, nil, 9); ok || err != nil {
		t.Errorf("a catalog without armor must give no store: %v %v", ok, err)
	}
	if got := PlanStorage(StorageRequest{}).RoomDemand; got != (RoomDemand{}) {
		t.Errorf("no gear store asks for rooms: %+v", got)
	}
}
