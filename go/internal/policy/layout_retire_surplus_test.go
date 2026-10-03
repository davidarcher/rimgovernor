package policy

import "testing"

func surplusRoom(role ModuleRole, x, w, h int32) LayoutRoom {
	return LayoutRoom{Role: role, Interior: Rectangle{X: x, Z: 10, Width: w, Height: h}}
}

func surplusRoles(p LayoutPlan, role ModuleRole) []int32 {
	var xs []int32
	for _, r := range p.roomsOf(role) {
		xs = append(xs, r.Interior.X)
	}
	return xs
}

// A title that outgrew its throne room leaves one throne room (#1825): an
// unbuilt small one goes at once; a built small one stays until a room
// holding the title's area is built.
func TestRetireSurplusThroneRooms(t *testing.T) {
	small, big, bigger := surplusRoom(ModuleThrone, 0, 4, 4), surplusRoom(ModuleThrone, 20, 6, 5), surplusRoom(ModuleThrone, 40, 7, 6)
	growth := RoomGrowth{ThroneMin: 30}
	plan := LayoutPlan{Rooms: []LayoutRoom{small, bigger, big}}

	got, changed := retireSurplusRooms(plan, growth, map[Rectangle]bool{})
	if xs := surplusRoles(got, ModuleThrone); !changed || len(xs) != 1 || xs[0] != big.Interior.X {
		t.Fatalf("unbuilt: kept %v, want the smallest holder", xs)
	}
	if _, changed := retireSurplusRooms(got, growth, map[Rectangle]bool{}); changed {
		t.Fatal("a reconciled plan changed")
	}

	built := map[Rectangle]bool{small.Interior: true}
	got, _ = retireSurplusRooms(plan, growth, built)
	if xs := surplusRoles(got, ModuleThrone); len(xs) != 2 || xs[0] != small.Interior.X || xs[1] != big.Interior.X {
		t.Fatalf("built small room: kept %v, want small and the smallest holder", xs)
	}

	built[bigger.Interior] = true
	got, _ = retireSurplusRooms(plan, growth, built)
	if xs := surplusRoles(got, ModuleThrone); len(xs) != 1 || xs[0] != bigger.Interior.X {
		t.Fatalf("built holder: kept %v, want it alone", xs)
	}

	if _, changed := retireSurplusRooms(LayoutPlan{Rooms: []LayoutRoom{small, big}}, RoomGrowth{}, nil); changed {
		t.Fatal("rooms retired with no title asking for a throne room")
	}
	if _, changed := retireSurplusRooms(LayoutPlan{Rooms: []LayoutRoom{small, surplusRoom(ModuleThrone, 20, 4, 5)}}, growth, nil); changed {
		t.Fatal("rooms retired with no room holding the title's area")
	}
}

// Gear rooms drop while unbuilt once their demand reads false; built ones and
// an unknown reading keep them.
func TestRetireSurplusGearRooms(t *testing.T) {
	armory, wardrobe := surplusRoom(ModuleArmory, 0, 4, 4), surplusRoom(ModuleWardrobe, 10, 4, 4)
	plan := LayoutPlan{Rooms: []LayoutRoom{armory, wardrobe}}
	if _, changed := retireSurplusRooms(plan, RoomGrowth{Demand: RoomDemand{}}, nil); changed {
		t.Fatal("an unread demand retired rooms")
	}
	got, changed := retireSurplusRooms(plan, RoomGrowth{Demand: RoomDemand{Known: true, Armory: true}}, map[Rectangle]bool{})
	if !changed || len(surplusRoles(got, ModuleArmory)) != 1 || len(surplusRoles(got, ModuleWardrobe)) != 0 {
		t.Fatalf("armory demanded: %+v", got.Rooms)
	}
	got, _ = retireSurplusRooms(plan, RoomGrowth{Demand: RoomDemand{Known: true}}, map[Rectangle]bool{wardrobe.Interior: true})
	if len(surplusRoles(got, ModuleArmory)) != 0 || len(surplusRoles(got, ModuleWardrobe)) != 1 {
		t.Fatalf("built wardrobe must stay: %+v", got.Rooms)
	}
}

// Extra storage rooms drop while unbuilt on an idle reading only; the core
// room and built rooms stay.
func TestRetireSurplusStorageRooms(t *testing.T) {
	core, built, spare := surplusRoom(ModuleStorage, 0, 3, 3), surplusRoom(ModuleStorage, 10, 3, 3), surplusRoom(ModuleStorage, 20, 3, 3)
	plan := LayoutPlan{Rooms: []LayoutRoom{core, built, spare}}
	standing := map[Rectangle]bool{built.Interior: true}
	if _, changed := retireSurplusRooms(plan, RoomGrowth{Demand: RoomDemand{Known: true, Storage: 0}}, standing); changed {
		t.Fatal("a pending reading retired storage")
	}
	got, _ := retireSurplusRooms(plan, RoomGrowth{Demand: RoomDemand{StorageIdle: true}}, standing)
	if xs := surplusRoles(got, ModuleStorage); len(xs) != 2 || xs[0] != 0 || xs[1] != 10 {
		t.Fatalf("kept %v, want the core and the built room", xs)
	}
	got, _ = retireSurplusRooms(plan, RoomGrowth{Demand: RoomDemand{StorageIdle: true}}, map[Rectangle]bool{})
	if xs := surplusRoles(got, ModuleStorage); len(xs) != 1 || xs[0] != 0 {
		t.Fatalf("kept %v, want the core alone (an unbuilt core stays too)", xs)
	}
}

// Spare room in a standing warehouse is a no-demand reading; a planned room
// not yet built is a wait, not a reading.
func TestStorageIdleSeparatesNoDemandFromAnUnbuiltRoom(t *testing.T) {
	first := Rectangle{X: 10, Z: 10, Width: 3, Height: 3}
	idle := func(req StorageRequest) bool { return PlanStorage(req).RoomDemand.StorageIdle }
	if !idle(storeRequest(2, 1, warehouseZone("a", "general", first, 7))) {
		t.Fatal("a warehouse under the threshold beside an unbuilt room is no demand")
	}
	if idle(storeRequest(2, 1, warehouseZone("a", "general", first, 9))) {
		t.Fatal("a full warehouse answered by an unbuilt room is not idle")
	}
	if idle(storeRequest(1, 0)) || idle(storeRequest(1, 1)) {
		t.Fatal("unbuilt or unserved rooms are not a reading")
	}
}
