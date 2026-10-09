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

func TestCombatDoorDestroyedBetweenStops(t *testing.T) {
	view := doorView(5)
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	if memory.PotshotDoor == nil {
		t.Fatal("fixture has no potshot door")
	}
	// The saved layout and earlier stop still refer to the door; the
	// current room boundary has lost it after destruction.
	view.Rooms[0].Doors = nil
	for _, tick := range []domain.Tick{160, 220, 280} {
		view.Tick = tick
		var orders []CombatOrder
		orders, memory = decideStop(t, view, StopEvent{}, memory)
		if order, ok := doorOrder(orders); ok {
			t.Fatalf("missing door ordered at %d: %+v", tick, order)
		}
		if memory.PotshotDoor != nil {
			t.Fatal("missing door retained")
		}
	}
}

func TestCombatDoorRefusalRetiresSelection(t *testing.T) {
	cell := domain.Cell{X: 15, Z: 19}
	memory := CombatMemory{PotshotDoor: &PodDoor{Cell: cell, Mode: DoorHoldOpen}, PodDoors: []PodDoor{{Cell: cell}}, WaitDoors: []PodDoor{{Cell: cell}}}
	got := memory.RefuseDoor(cell)
	if got.PotshotDoor != nil || len(got.PodDoors) != 0 || len(got.WaitDoors) != 0 {
		t.Fatalf("retained refused door: %+v", got)
	}
	if memory.PotshotDoor == nil || len(memory.PodDoors) != 1 {
		t.Fatal("mutated input memory")
	}
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
// door} -> the same potshot: two gunners inside the door, the door
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

// huntDoorView is huntView with a perimeter room door and one rhino at
// rhinoZ south of the door, hunting a colonist when provoked.
func huntDoorView(rhinoZ int32, provoked bool) CombatView {
	view := huntView(animal("rh", "Rhino", domain.Cell{X: 15, Z: rhinoZ}, 6))
	view.Rooms = []CombatRoom{{Interior: Rectangle{X: 10, Z: 20, Width: 10, Height: 10}, Doors: []domain.Cell{{X: 15, Z: 19}}}}
	view.Threats[0].Hunting = domain.Known(provoked)
	return view
}

// {hunt whose prey targets a colonist} -> the door potshot:
// door gunners inside the door on the prey, the door open; within 3 cells it
// closes and the gunners hold; backed off beyond 6 it reopens, and a damaged
// door is repaired.
func TestHuntDoorPotshotProvokedPrey(t *testing.T) {
	orders, m := decideStop(t, huntDoorView(5, true), StopEvent{}, CombatMemory{})
	if d, ok := doorOrder(orders); m.Tactic != TacticHunt || !ok || d.Cell != (domain.Cell{X: 15, Z: 19}) || d.Door != DoorHoldOpen {
		t.Fatalf("%s %+v", m.Tactic, orders)
	}
	posted := 0
	for _, r := range m.Roles {
		if r.Duty == DutyDoorway {
			posted++
			if r.Cell == nil || r.Cell.Z != 20 || r.Target != "rh" {
				t.Fatalf("%+v", r)
			}
		}
	}
	if posted != doorGunners {
		t.Fatalf("%+v", m.Roles)
	}
	view := huntDoorView(17, true)
	view.Tick = 160
	orders, m = decideStop(t, view, StopEvent{}, m)
	if d, ok := doorOrder(orders); !ok || d.Door != DoorClose {
		t.Fatalf("%+v", orders)
	}
	for _, r := range m.Roles {
		if r.Duty == DutyDoorway && r.Target != "" {
			t.Fatalf("door gunner still engaging: %+v", r)
		}
	}
	view = huntDoorView(5, true)
	view.Tick = 220
	view.DamagedDoors = []domain.Cell{{X: 15, Z: 19}}
	orders, _ = decideStop(t, view, StopEvent{}, m)
	if d, ok := doorOrder(orders); !ok || d.Door != DoorHoldOpen {
		t.Fatalf("%+v", orders)
	}
	if _, ok := repairOrder(orders); !ok {
		t.Fatalf("no repair: %+v", orders)
	}
}

// {calm prey} -> a plain hunt: no potshot door.
func TestHuntNoDoorForCalmPrey(t *testing.T) {
	orders, m := decideStop(t, huntDoorView(5, false), StopEvent{}, CombatMemory{})
	if _, ok := doorOrder(orders); ok || m.PotshotDoor != nil || m.Tactic != TacticHunt {
		t.Fatalf("%s %+v", m.Tactic, orders)
	}
}

// {one prey manhunter, one calm} -> the hunt takes the door; the door stays
// once the prey calms.
func TestHuntMixedPreyTakesAndKeepsDoor(t *testing.T) {
	view := withAnimals(huntDoorView(5, false), animal("rh", "Rhino", domain.Cell{X: 15, Z: 5}, 6), animal("el", "Elk", domain.Cell{X: 17, Z: 5}, 6))
	view.Rooms = []CombatRoom{{Interior: Rectangle{X: 10, Z: 20, Width: 10, Height: 10}, Doors: []domain.Cell{{X: 15, Z: 19}}}}
	view.Threats[0].Manhunter = domain.Known(true)
	view.Threats[1].Manhunter = domain.Known(false)
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if _, ok := doorOrder(orders); !ok || m.Tactic != TacticHunt {
		t.Fatalf("%s %+v", m.Tactic, orders)
	}
	view.Threats[0].Manhunter = domain.Known(false)
	view.Tick = 160
	if orders, m = decideStop(t, view, StopEvent{}, m); m.PotshotDoor == nil {
		t.Fatalf("door dropped: %+v", orders)
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
