package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

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
	if owed := GearRoomsOwed(plan, GearRoomDemand{}); len(owed) != 0 {
		t.Fatalf("owed %v with no demand", owed)
	}
	if same, added := growGearRooms(plan, GearRoomDemand{}); added || len(same.Rooms) != len(plan.Rooms) {
		t.Fatal("rooms added with no demand")
	}
}

func TestGearRoomsSitBesideTheirAnchorAndRoute(t *testing.T) {
	t.Parallel()
	plan := gearTestPlan()
	grown, added := growGearRooms(plan, GearRoomDemand{Armory: true, Wardrobe: true})
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
	if again, added := growGearRooms(grown, GearRoomDemand{Armory: true, Wardrobe: true}); added || len(again.Rooms) != len(grown.Rooms) {
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
	cells := []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}
	zone := func(role string, stored int) StockpileZone {
		return StockpileZone{ID: role, Role: role, Cells: cells, Stored: cells[:stored]}
	}
	for _, tc := range []struct {
		name  string
		zones []StockpileZone
		want  GearRoomDemand
	}{
		{"no zones", nil, GearRoomDemand{}},
		{"room to spare", []StockpileZone{zone(domain.WeaponsRole, 1), zone(domain.ApparelRole, 0)}, GearRoomDemand{}},
		{"weapons full", []StockpileZone{zone(domain.WeaponsRole, 2), zone(domain.ApparelRole, 1)}, GearRoomDemand{Armory: true}},
		{"apparel full", []StockpileZone{zone(domain.ApparelRole, 2)}, GearRoomDemand{Wardrobe: true}},
		{"other full", []StockpileZone{zone(domain.GeneralRole, 2)}, GearRoomDemand{}},
	} {
		if got := PlanStorage(StorageRequest{Zones: tc.zones}).Gear; got != tc.want {
			t.Errorf("%s: %+v, want %+v", tc.name, got, tc.want)
		}
	}
}
