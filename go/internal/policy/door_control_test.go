package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func logisticsDoorFixture() (RoomObservation, domain.Fact[RoutesObservation]) {
	door := RoomDoor{ID: "Door1", Cell: domain.Cell{X: 5, Z: 5}, Outside: domain.Cell{X: 6, Z: 5}, Outdoors: domain.Known(false), PlayerOwned: domain.Known(true), Open: domain.Known(false), HoldOpen: domain.Known(false), BlockedOpen: domain.Known(false), Forbidden: domain.Known(false)}
	room := func(id string, role RoomRole, cell domain.Cell) Room {
		return Room{ID: id, Role: domain.Known(role), Enclosed: domain.Known(true), Roofed: domain.Known(true), Temperature: domain.Known(22.0), Burning: domain.Known(false), TemperatureControl: domain.Known(false), PerishableContents: domain.Known(false), Cells: []domain.Cell{cell}}
	}
	near := room("a", RoomRoleWorkshop, domain.Cell{X: 4, Z: 5})
	near.Doors = []RoomDoor{door}
	far := room("b", RoomRoleStoreroom, door.Outside)
	return RoomObservation{Rooms: []Room{near, far}}, domain.Known(RoutesObservation{Traffic: []TrafficCell{{Cell: door.Cell, Layer: TrafficColonist, Samples: 20}}})
}

func TestDoorLogisticsSettingAndPhysicalPassage(t *testing.T) {
	rooms, traffic := logisticsDoorFixture()
	p := DefaultRoundsPolicy()
	got := DoorChanges(domain.Known(rooms), traffic, p)
	if len(got) != 1 || !got[0].HoldOpen || got[0].Heat || !got[0].Passage {
		t.Fatalf("opening: %+v", got)
	}
	rooms.Rooms[0].Doors[0].HoldOpen = domain.Known(true)
	got = DoorChanges(domain.Known(rooms), traffic, p)
	if len(got) != 1 || !got[0].Passage {
		t.Fatalf("latch mistaken for passage: %+v", got)
	}
	rooms.Rooms[0].Doors[0].Open = domain.Known(true)
	if got = DoorChanges(domain.Known(rooms), traffic, p); len(got) != 0 {
		t.Fatalf("repeats correct setting: %+v", got)
	}
	rooms.Rooms[1].PerishableContents = domain.Known(true)
	got = DoorChanges(domain.Known(rooms), traffic, p)
	if len(got) != 1 || got[0].HoldOpen {
		t.Fatalf("changed use left held open: %+v", got)
	}
}

func TestDoorLogisticsProtectedBoundaries(t *testing.T) {
	tests := []struct {
		name   string
		change func(*RoomObservation)
	}{
		{"prison", func(r *RoomObservation) { r.Rooms[1].Role = domain.Known(RoomRolePrisonCell) }},
		{"containment", func(r *RoomObservation) { r.Rooms[1].Role = domain.Known(RoomRoleContainmentCell) }},
		{"freezer", func(r *RoomObservation) {
			r.Rooms[1].TemperatureControl = domain.Known(true)
			r.Rooms[1].Temperature = domain.Known(-5.0)
		}},
		{"fire", func(r *RoomObservation) { r.Rooms[1].Burning = domain.Known(true) }},
		{"perishable", func(r *RoomObservation) { r.Rooms[1].PerishableContents = domain.Known(true) }},
		{"unknown role", func(r *RoomObservation) { r.Rooms[1].Role = domain.Unknown[RoomRole]() }},
		{"unknown fire", func(r *RoomObservation) { r.Rooms[1].Burning = domain.Unknown[bool]() }},
		{"perimeter", func(r *RoomObservation) { r.Rooms[0].Doors[0].Outdoors = domain.Known(true) }},
		{"unknown ownership", func(r *RoomObservation) { r.Rooms[0].Doors[0].PlayerOwned = domain.Unknown[bool]() }},
		{"missing destination", func(r *RoomObservation) { r.Rooms = r.Rooms[:1] }},
		{"missing door", func(r *RoomObservation) { r.Rooms[0].Doors = nil }},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			rooms, traffic := logisticsDoorFixture()
			test.change(&rooms)
			if got := DoorChanges(domain.Known(rooms), traffic, DefaultRoundsPolicy()); len(got) != 0 {
				t.Fatalf("unsafe opening: %+v", got)
			}
		})
	}
	rooms, _ := logisticsDoorFixture()
	if got := DoorChanges(domain.Known(rooms), domain.Unknown[RoutesObservation](), DefaultRoundsPolicy()); len(got) != 0 {
		t.Fatalf("invented traffic: %+v", got)
	}
}

func TestDoorHeatReliefAndRecovery(t *testing.T) {
	rooms, _ := logisticsDoorFixture()
	rooms.Rooms[0].Role = domain.Known(RoomRoleBedroom)
	rooms.Rooms[0].Pawns = []domain.PawnID{"a"}
	rooms.Rooms[0].Temperature = domain.Known(40.0)
	p := DefaultRoundsPolicy()
	empty := domain.Known(RoutesObservation{})
	got := DoorChanges(domain.Known(rooms), empty, p)
	if len(got) != 1 || !got[0].Heat || !got[0].Passage || got[0].To != (domain.Cell{X: 6, Z: 5}) {
		t.Fatalf("heat relief: %+v", got)
	}
	rooms.Rooms[0].Doors[0].HoldOpen = domain.Known(true)
	rooms.Rooms[0].Doors[0].Open = domain.Known(true)
	rooms.Rooms[0].Temperature = domain.Known(30.0)
	if got = DoorChanges(domain.Known(rooms), empty, p); len(got) != 0 {
		t.Fatalf("churn between heat bands: %+v", got)
	}
	rooms.Rooms[0].Temperature = domain.Known(27.0)
	if got = DoorChanges(domain.Known(rooms), empty, p); len(got) != 1 || got[0].HoldOpen {
		t.Fatalf("not restored after recovery: %+v", got)
	}
}

func TestDoorHeatRejectsUnsafeDestination(t *testing.T) {
	for _, test := range []struct {
		name   string
		change func(*RoomObservation)
	}{
		{"hotter", func(r *RoomObservation) { r.Rooms[1].Temperature = domain.Known(45.0) }},
		{"unknown", func(r *RoomObservation) { r.Rooms[1].Temperature = domain.Unknown[float64]() }},
		{"fire", func(r *RoomObservation) { r.Rooms[1].Burning = domain.Known(true) }},
		{"climate isolation", func(r *RoomObservation) { r.Rooms[1].TemperatureControl = domain.Known(true) }},
		{"custody", func(r *RoomObservation) { r.Rooms[1].Role = domain.Known(RoomRolePrisonBarracks) }},
		{"empty", func(r *RoomObservation) { r.Rooms[0].Pawns = nil }},
	} {
		t.Run(test.name, func(t *testing.T) {
			rooms, _ := logisticsDoorFixture()
			rooms.Rooms[0].Role = domain.Known(RoomRoleBedroom)
			rooms.Rooms[0].Pawns = []domain.PawnID{"a"}
			rooms.Rooms[0].Temperature = domain.Known(40.0)
			test.change(&rooms)
			if got := DoorChanges(domain.Known(rooms), domain.Known(RoutesObservation{}), DefaultRoundsPolicy()); len(got) != 0 {
				t.Fatalf("unsafe heat relief: %+v", got)
			}
		})
	}
}
