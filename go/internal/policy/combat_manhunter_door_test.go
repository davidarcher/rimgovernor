package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// doorView is riflemen a, b, c at z=30, no layout, a room at x 10..19,
// z 20..29 with its door at (15,19) facing south, and two wolves south of
// it at distance.
func doorView(wolfZ int32) CombatView {
	view := holdView()
	view.Layout = domain.Unknown[CombatLayout]()
	view.Rooms = []CombatRoom{{Interior: Rectangle{X: 10, Z: 20, Width: 10, Height: 10}, Doors: []domain.Cell{{X: 15, Z: 19}}}}
	return withAnimals(view, animal("w1", "Wolf_Timber", domain.Cell{X: 15, Z: wolfZ}, 6.8), animal("w2", "Wolf_Timber", domain.Cell{X: 16, Z: wolfZ - 1}, 6.8))
}

func doorOrder(orders []CombatOrder) (CombatOrder, bool) {
	for _, o := range orders {
		if o.Kind == OrderDoor {
			return o, true
		}
	}
	return CombatOrder{}, false
}

// {pack beyond 3 cells of the door} -> two gunners on the cells just
// inside it, attacking, and the door held open.
func TestDecideCombatManhunterDoorPotshot(t *testing.T) {
	orders, m := decideStop(t, doorView(5), StopEvent{}, CombatMemory{})
	if d, ok := doorOrder(orders); !ok || d.Cell != (domain.Cell{X: 15, Z: 19}) || d.Door != DoorHoldOpen {
		t.Fatalf("%+v", orders)
	}
	posted := 0
	for _, r := range m.Roles {
		if r.Duty == DutyDoorway {
			posted++
			if r.Cell == nil || r.Cell.Z != 20 || r.Target == "" {
				t.Fatalf("%+v", r)
			}
		}
	}
	if posted != doorGunners {
		t.Fatalf("%+v", m.Roles)
	}
}

// {animal within 3 cells} -> door close, the door gunners' targets
// cleared; the order goes out once.
func TestDecideCombatManhunterDoorClosesOnApproach(t *testing.T) {
	_, m := decideStop(t, doorView(5), StopEvent{}, CombatMemory{})
	view := doorView(17)
	view.Tick = 160
	orders, m := decideStop(t, view, StopEvent{}, m)
	if d, ok := doorOrder(orders); !ok || d.Door != DoorClose {
		t.Fatalf("%+v", orders)
	}
	for _, r := range m.Roles {
		if r.Duty == DutyDoorway && r.Target != "" {
			t.Fatalf("door gunner still engaging: %+v", r)
		}
	}
	view.Tick = 220
	if orders, _ := decideStop(t, view, StopEvent{}, m); len(orders) != 0 {
		if _, ok := doorOrder(orders); ok {
			t.Fatalf("door order repeated: %+v", orders)
		}
	}
}

// {pack back beyond 6 cells} -> hold_open again; between 3 and 6 the door
// stays shut.
func TestDecideCombatManhunterDoorReopens(t *testing.T) {
	_, m := decideStop(t, doorView(5), StopEvent{}, CombatMemory{})
	view := doorView(17)
	view.Tick = 160
	_, m = decideStop(t, view, StopEvent{}, m)
	view = doorView(14)
	view.Tick = 220
	orders, m := decideStop(t, view, StopEvent{}, m)
	if _, ok := doorOrder(orders); ok {
		t.Fatalf("reopened inside 6 cells: %+v", orders)
	}
	view = doorView(8)
	view.Tick = 280
	orders, _ = decideStop(t, view, StopEvent{}, m)
	if d, ok := doorOrder(orders); !ok || d.Door != DoorHoldOpen {
		t.Fatalf("%+v", orders)
	}
}
