package policy

import (
	"slices"
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

func repairOrder(orders []CombatOrder) (CombatOrder, bool) {
	for _, o := range orders {
		if o.Kind == OrderRepair {
			return o, true
		}
	}
	return CombatOrder{}, false
}

// {potshot door damaged, pack beyond 6 cells} -> the nearest door gunner
// repairs it, once; a gunner already on the Repair job gets nothing.
func TestDecideCombatManhunterDoorRepairs(t *testing.T) {
	_, m := decideStop(t, doorView(5), StopEvent{}, CombatMemory{})
	view := doorView(5)
	view.Tick = 160
	view.DamagedDoors = []domain.Cell{{X: 15, Z: 19}}
	orders, next := decideStop(t, view, StopEvent{}, m)
	o, ok := repairOrder(orders)
	if !ok || o.Cell != (domain.Cell{X: 15, Z: 19}) || o.Reason != ReasonRepair {
		t.Fatalf("%+v", orders)
	}
	if !slices.ContainsFunc(next.Roles, func(r CombatRole) bool { return r.Pawn == o.Pawn && r.Duty == DutyDoorway }) {
		t.Fatalf("repairer %s is not a door gunner: %+v", o.Pawn, next.Roles)
	}
	for _, other := range orders {
		if other.Pawn == o.Pawn && other.Kind != OrderRepair {
			t.Fatalf("repairer also ordered: %+v", orders)
		}
	}
	for i := range view.Pawns {
		if view.Pawns[i].ID == o.Pawn {
			view.Pawns[i].Job = "Repair"
		}
	}
	view.Tick = 220
	orders, _ = decideStop(t, view, StopEvent{}, next)
	if _, ok := repairOrder(orders); ok {
		t.Fatalf("repair repeated: %+v", orders)
	}
}

// {potshot door damaged, an animal within 6 cells} -> no repair.
func TestDecideCombatManhunterDoorNoRepairUnderThreat(t *testing.T) {
	_, m := decideStop(t, doorView(5), StopEvent{}, CombatMemory{})
	view := doorView(14)
	view.Tick = 160
	view.DamagedDoors = []domain.Cell{{X: 15, Z: 19}}
	orders, next := decideStop(t, view, StopEvent{}, m)
	if _, ok := repairOrder(orders); ok || next.PotshotDoor.Repairer != "" {
		t.Fatalf("repair with a wolf at 5 cells: %+v", orders)
	}
}

// {a humanoid raid on squad defense, no killbox layout, a planned-room
// door} -> the same potshot (#1059): two gunners inside the door, the door
// held open; a raider within 3 cells closes it.
func TestDoorPotshotRaid(t *testing.T) {
	view := holdView()
	view.Layout = domain.Unknown[CombatLayout]()
	view.Rooms = []CombatRoom{{Interior: Rectangle{X: 10, Z: 20, Width: 10, Height: 10}, Doors: []domain.Cell{{X: 15, Z: 19}}}}
	view.Pawns = append(view.Pawns,
		CombatPawnState{ID: "r1", Cell: domain.Known(domain.Cell{X: 9, Z: 5})},
		CombatPawnState{ID: "r2", Cell: domain.Known(domain.Cell{X: 10, Z: 4})})
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if d, ok := doorOrder(orders); m.Tactic != TacticSquad || !ok || d.Cell != (domain.Cell{X: 15, Z: 19}) || d.Door != DoorHoldOpen {
		t.Fatalf("%s %+v %+v", m.Tactic, orders, m.Roles)
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
	s1, d1 := combatRaider("r1", domain.Cell{X: 15, Z: 17})
	view.Threats[0], view.Positional[0] = s1, d1
	view.Pawns[3].Cell = domain.Known(domain.Cell{X: 15, Z: 17})
	view.Tick = 160
	orders, _ = decideStop(t, view, StopEvent{}, m)
	if d, ok := doorOrder(orders); !ok || d.Door != DoorClose {
		t.Fatalf("%+v", orders)
	}
}

// A killbox hold fights from its firing line: no potshot door.
func TestDoorPotshotNotOnHold(t *testing.T) {
	view := holdView()
	view.Rooms = []CombatRoom{{Interior: Rectangle{X: 10, Z: 20, Width: 10, Height: 10}, Doors: []domain.Cell{{X: 15, Z: 19}}}}
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if _, ok := doorOrder(orders); ok || m.PotshotDoor != nil {
		t.Fatalf("%+v", orders)
	}
}
