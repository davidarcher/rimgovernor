package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func safeRoom(id string, x0, z0, w, h int32, roofed bool, doors ...RoomDoor) Room {
	r := Room{ID: id, Enclosed: domain.Known(true), Roofed: domain.Known(roofed), Doors: doors}
	for x := x0; x < x0+w; x++ {
		for z := z0; z < z0+h; z++ {
			r.Cells = append(r.Cells, domain.Cell{X: x, Z: z})
		}
	}
	return r
}

func TestSafeAreaCellsExclusions(t *testing.T) {
	rooms := RoomObservation{Rooms: []Room{
		safeRoom("bedroom", 0, 0, 2, 2, true),
		safeRoom("workshop", 10, 0, 2, 1, true),
		safeRoom("gate", 20, 0, 1, 1, true, RoomDoor{EnemyFacing: true}),
		safeRoom("barn", 30, 0, 1, 1, false),
		{ID: "open", Enclosed: domain.Known(false), Roofed: domain.Known(true), Cells: []domain.Cell{{X: 40, Z: 0}}},
	}}
	got := SafeAreaCells(rooms, []domain.Cell{{X: 11, Z: 0}})
	want := []domain.Cell{{X: 0, Z: 0}, {X: 0, Z: 1}, {X: 1, Z: 0}, {X: 1, Z: 1}, {X: 10, Z: 0}}
	if len(got) != len(want) {
		t.Fatal(got)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatal(got)
		}
	}
}

func TestSafeAreaOwedRaisesMaintainShelter(t *testing.T) {
	f := stableRoutine()
	f.SafeAreaOwed = domain.Known(true)
	r := needs(t, f, RoutineLatches{})
	for _, g := range r.Goals {
		if g.ID == MaintainShelter {
			return
		}
	}
	t.Fatal("owed safe area raised no MaintainShelter goal", r.Goals)
}

// A threat raises an emergency that holds development; the owed Safe area
// must not wait behind it, or PlanSheltering has no area to move pawns into.
func TestSafeAreaOwedUnderThreatIsUrgent(t *testing.T) {
	f := stableRoutine()
	f.SafeAreaOwed = domain.Known(true)
	f.Hostiles = domain.Known[int64](3)
	r := needs(t, f, RoutineLatches{})
	for _, g := range r.Goals {
		if g.ID == MaintainShelter {
			if g.Priority >= 3 {
				t.Fatal("MaintainShelter under a threat stays a development goal", g)
			}
			return
		}
	}
	t.Fatal("owed safe area raised no MaintainShelter goal", r.Goals)
}

func TestPlanSafeAreaResetThenDiffs(t *testing.T) {
	rooms := RoomObservation{Rooms: []Room{safeRoom("a", 0, 0, 2, 1, true)}}
	ops, cells, err := PlanSafeArea(rooms, nil, nil, false)
	if err != nil || len(ops) != 2 || ops[0].Operation() != domain.AreaDelete || ops[1].Operation() != domain.AreaCreate || len(ops[1].Cells()) != 2 || ops[1].Key() != SafeAreaKey {
		t.Fatal(ops, err)
	}
	// Stable base: no edits.
	if ops, _, err = PlanSafeArea(rooms, nil, cells, true); err != nil || len(ops) != 0 {
		t.Fatal(ops, err)
	}
	// A room is built: set_cells with only its cells.
	rooms.Rooms = append(rooms.Rooms, safeRoom("b", 5, 5, 1, 1, true))
	ops, next, err := PlanSafeArea(rooms, nil, cells, true)
	if err != nil || len(ops) != 1 || ops[0].Operation() != domain.AreaSetCells || len(ops[0].Cells()) != 1 || ops[0].Cells()[0] != (domain.Cell{X: 5, Z: 5}) || len(next) != 3 {
		t.Fatal(ops, err)
	}
	// Room a loses its roof: clear_cells for its cells.
	rooms.Rooms[0].Roofed = domain.Known(false)
	if ops, _, err = PlanSafeArea(rooms, nil, next, true); err != nil || len(ops) != 1 || ops[0].Operation() != domain.AreaClearCells || len(ops[0].Cells()) != 2 {
		t.Fatal(ops, err)
	}
}

// The lab hut is the only roofed room and its door faces the killbox: the
// Safe area keeps it rather than end empty, which sheltered no one (#1560).
func TestSafeAreaCellsKeepsExposedRoomWhenNoOther(t *testing.T) {
	rooms := RoomObservation{Rooms: []Room{safeRoom("hut", 0, 0, 2, 1, true, RoomDoor{EnemyFacing: true})}}
	if got := SafeAreaCells(rooms, nil); len(got) != 2 {
		t.Fatal(got)
	}
	rooms.Rooms = append(rooms.Rooms, safeRoom("inner", 10, 0, 1, 1, true))
	if got := SafeAreaCells(rooms, nil); len(got) != 1 || got[0] != (domain.Cell{X: 10, Z: 0}) {
		t.Fatal(got)
	}
}
