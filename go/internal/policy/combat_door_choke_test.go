package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"slices"
	"testing"
)

func meleeDoorFixture() CombatView {
	view := doorView(5)
	// Add a healthy brawler to the existing riflemen fixture.
	brawler := view.Defenders[0]
	brawler.ID = "brawler"
	brawler.RangedEquipped = domain.Known(false)
	brawler.MeleeEquipped = domain.Known(true)
	brawler.Armed = domain.Known(true)
	view.Defenders = append(view.Defenders, brawler)
	view.Pawns = append(view.Pawns, CombatPawnState{ID: "brawler", Cell: domain.Known(domain.Cell{X: 15, Z: 24})})
	view.Orderable = append(view.Orderable, "brawler")
	view.Rooms[0].Roofed = true
	view.Rooms[0].Role = domain.Known(RoomRoleBarracks)
	view.Rooms[0].Burning = domain.Known(false)
	door := RoomDoor{ID: "Door1", Cell: domain.Cell{X: 15, Z: 19}, Outside: domain.Cell{X: 15, Z: 18}, Outdoors: domain.Known(true), PlayerOwned: domain.Known(true), Open: domain.Known(false), HoldOpen: domain.Known(false), Forbidden: domain.Known(false), BlockedOpen: domain.Known(false)}
	view.DoorStates = domain.Known([]RoomDoor{door})
	return view
}

func decideMeleeDoor(t *testing.T, view CombatView, memory CombatMemory) ([]CombatOrder, CombatMemory) {
	t.Helper()
	orders, ask, next := DecideCombat(view, GeometryReply{}, StopEvent{}, memory)
	if ask != nil {
		orders, ask, next = DecideCombat(view, GeometryReply{Answered: true, Standable: slices.Clone(ask.Cells)}, StopEvent{}, memory)
	}
	if ask != nil {
		t.Fatal("unexpected second ask")
	}
	return orders, next
}

func TestMeleeDoorWaitsForFormationThenOpensByMovement(t *testing.T) {
	view := meleeDoorFixture()
	orders, memory := decideMeleeDoor(t, view, CombatMemory{})
	if memory.Tactic != TacticDoorChoke || memory.ChokeDoor == nil {
		t.Fatalf("no door formation: %+v", memory)
	}
	for _, order := range orders {
		if order.Kind == OrderDoor && order.Door == DoorHoldOpen {
			t.Fatal("opened before formation ready")
		}
	}
	for _, role := range memory.Roles {
		if role.Cell == nil {
			continue
		}
		if *role.Cell == memory.ChokeDoor.Cell {
			t.Fatal("defender placed inside choke")
		}
		for i := range view.Pawns {
			if view.Pawns[i].ID == role.Pawn {
				view.Pawns[i].Cell = domain.Known(*role.Cell)
				view.Pawns[i].Job = "Wait_Combat"
			}
		}
	}
	view.Tick += 60
	orders, memory = decideMeleeDoor(t, view, memory)
	if !slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Kind == OrderDoor && o.Door == DoorHoldOpen }) {
		t.Fatalf("ready formation never holds open: %+v", orders)
	}
	if memory.ChokeDoor.Opener == "" || !slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Kind == OrderMove && o.Cell == memory.ChokeDoor.Cell }) {
		t.Fatalf("closed latch mistaken for physical opening: %+v", orders)
	}
	opener := memory.ChokeDoor.Opener
	states, _ := view.DoorStates.Value()
	states = slices.Clone(states)
	states[0].HoldOpen = domain.Known(true)
	states[0].Open = domain.Known(true)
	states[0].BlockedOpen = domain.Known(true)
	view.DoorStates = domain.Known(states)
	for i := range view.Pawns {
		if view.Pawns[i].ID == opener {
			view.Pawns[i].Cell = domain.Known(memory.ChokeDoor.Cell)
			view.Pawns[i].Job = "Goto"
		}
	}
	view.Tick += 60
	orders, memory = decideMeleeDoor(t, view, memory)
	if memory.ChokeDoor.Opener != "" {
		t.Fatal("opener not released after physical open")
	}
	if !slices.ContainsFunc(orders, func(o CombatOrder) bool { return o.Pawn == opener && o.Kind == OrderMove && o.Cell != states[0].Cell }) {
		t.Fatalf("opener never returns to formation: %+v", orders)
	}
	if clears := CombatDoorClears(memory); len(clears) != 1 || clears[0].Door != DoorClose {
		t.Fatalf("missing cleanup: %+v", clears)
	}
}

func TestMeleeDoorRejectsUnsafeOrUnknownChoke(t *testing.T) {
	for _, test := range []struct {
		name   string
		change func(*CombatView)
	}{
		{"unknown doors", func(v *CombatView) { v.DoorStates = domain.Unknown[[]RoomDoor]() }},
		{"missing door", func(v *CombatView) { v.DoorStates = domain.Known([]RoomDoor{}) }},
		{"unknown room", func(v *CombatView) { v.Rooms[0].Role = domain.Unknown[RoomRole]() }},
		{"custody", func(v *CombatView) { v.Rooms[0].Role = domain.Known(RoomRoleContainmentCell) }},
		{"fire", func(v *CombatView) { v.Rooms[0].Burning = domain.Known(true) }},
		{"ranged enemy", func(v *CombatView) { v.Threats[0].RangedEquipped = domain.Known(true) }},
		{"unfit blocker", func(v *CombatView) { v.Defenders[len(v.Defenders)-1].NeedsTend = domain.Known(true) }},
	} {
		t.Run(test.name, func(t *testing.T) {
			view := meleeDoorFixture()
			test.change(&view)
			if door, ok := meleeDoorSite(view); ok {
				t.Fatalf("unsafe choke selected: %+v", door)
			}
		})
	}
	view := meleeDoorFixture()
	door, _ := meleeDoorSite(view)
	if roles := meleeDoorFormation(view, GeometryReply{Answered: true}, door, nil); len(roles) != 0 {
		t.Fatal("unknown geometry placed defenders")
	}
}

func TestMeleeDoorRetreatReconcilesLatch(t *testing.T) {
	view := meleeDoorFixture()
	door, _ := meleeDoorSite(view)
	geometry := meleeDoorAsk(view, door)
	roles := meleeDoorFormation(view, GeometryReply{Standable: geometry.Cells}, door, nil)
	memory := CombatMemory{Tactic: TacticDoorChoke, Roles: roles, ChokeDoor: &PodDoor{Cell: door.Cell, Mode: DoorHoldOpen, Sent: true}, HeldDoors: []domain.Cell{door.Cell}, Wait: true}
	meleeDoorTurn(view, &memory)
	if memory.ChokeDoor.Mode != DoorClose || memory.ChokeDoor.Sent {
		t.Fatalf("retreat kept choke open: %+v", memory.ChokeDoor)
	}
	memory = memory.RefuseDoor(door.Cell)
	if memory.ChokeDoor != nil {
		t.Fatal("refused choke retained")
	}
}
