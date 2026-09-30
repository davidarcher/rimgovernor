package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestMarkEnemyDoorsFacesKillbox(t *testing.T) {
	plan := LayoutPlan{Reservations: []LayoutReservation{{Kind: ReserveKillbox, Area: Rectangle{X: 20, Z: 0, Width: 3, Height: 5}}}}
	killbox := plan.KillboxCells()
	if len(killbox) != 15 {
		t.Fatal(killbox)
	}
	out := domain.Known(true)
	rooms := RoomObservation{Rooms: []Room{{ID: "r", Doors: []RoomDoor{
		{Cell: domain.Cell{X: 10, Z: 2}, Outside: domain.Cell{X: 11, Z: 2}, Outdoors: out}, // east, toward
		{Cell: domain.Cell{X: 5, Z: 2}, Outside: domain.Cell{X: 4, Z: 2}, Outdoors: out},   // west, away
		{Cell: domain.Cell{X: 8, Z: 2}, Outside: domain.Cell{X: 8, Z: 3}, Outdoors: out},   // north, perpendicular
		{Cell: domain.Cell{X: 10, Z: 4}, Outside: domain.Cell{X: 11, Z: 4}, Outdoors: domain.Known(false)},
	}}}}
	marked := MarkEnemyDoors(rooms, killbox).Rooms[0].Doors
	want := []bool{true, false, false, false}
	for i, d := range marked {
		if d.EnemyFacing != want[i] {
			t.Fatal(i, d)
		}
	}
	if rooms.Rooms[0].Doors[1].EnemyFacing {
		t.Fatal("input mutated")
	}
	if !MarkEnemyDoors(rooms, nil).Rooms[0].Doors[1].EnemyFacing {
		t.Fatal("no plan: every outdoor door faces the enemy")
	}
}
