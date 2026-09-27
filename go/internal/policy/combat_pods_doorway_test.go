package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// doorwayView is podsView with a door in the landing room's south wall at
// (10,11): its far side is (10,10), flanked by (9,10) and (11,10). a and b
// carry rifles (range 30), c a shotgun (range 16).
func doorwayView() CombatView {
	view := podsView()
	view.Rooms[0].Doors = []domain.Cell{{X: 10, Z: 11}}
	ranges := map[domain.PawnID]float64{"a": 30, "b": 30, "c": 16}
	for i := range view.Pawns {
		view.Pawns[i].WeaponRange = ranges[view.Pawns[i].ID]
	}
	return view
}

// Two responders flank the landing room's door on its far side and the
// door is held open; the shotgun takes the first slot, the nearer rifle
// the second, and the far rifleman is not drafted.
func TestDecideCombatPodDoorwayPairs(t *testing.T) {
	orders, memory := decideStop(t, doorwayView(), StopEvent{}, CombatMemory{})
	want := []CombatOrder{
		{Kind: OrderDoor, Cell: domain.Cell{X: 10, Z: 11}, Door: DoorHoldOpen, Reason: ReasonFormation},
		{Pawn: "a", Kind: OrderMove, Cell: domain.Cell{X: 11, Z: 10}, Reason: ReasonFormation},
		{Pawn: "c", Kind: OrderMove, Cell: domain.Cell{X: 9, Z: 10}, Reason: ReasonFormation},
		{Pawn: "n", Kind: OrderMove, Cell: domain.Cell{X: 33, Z: 0}, Reason: ReasonFormation},
	}
	if !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
	roles := podRoles(memory)
	if _, ok := roles["b"]; ok || roles["a"].Duty != DutyDoorway || roles["c"].Duty != DutyDoorway {
		t.Fatalf("%+v", memory.Roles)
	}
	// The next stop, everyone in place, does not repeat the door order.
	view := doorwayView()
	view.Tick = 160
	view.Pawns[0].Cell = domain.Known(domain.Cell{X: 11, Z: 10})
	view.Pawns[2].Cell = domain.Known(domain.Cell{X: 9, Z: 10})
	view.Pawns[3].Cell = domain.Known(domain.Cell{X: 33, Z: 0})
	if again, _ := decideStop(t, view, StopEvent{}, memory); len(again) != 0 {
		t.Fatalf("%+v", again)
	}
}

// A melee brawler takes a doorway slot before any gun, however far off.
func TestDecideCombatPodDoorwayPrefersCloseRange(t *testing.T) {
	view := doorwayView()
	view.Defenders = append(view.Defenders, combatBrawler("m", 0.5))
	view.Pawns = append(view.Pawns, CombatPawnState{ID: "m", Cell: domain.Known(domain.Cell{X: 40, Z: 0}), Stance: StanceIdle})
	view.Orderable = append(view.Orderable, "m")
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	roles := podRoles(memory)
	if m := roles["m"]; m.Duty != DutyDoorway || m.Cell == nil || *m.Cell != (domain.Cell{X: 9, Z: 10}) {
		t.Fatalf("%+v", memory.Roles)
	}
	if c := roles["c"]; c.Duty != DutyDoorway || *c.Cell != (domain.Cell{X: 11, Z: 10}) {
		t.Fatalf("%+v", memory.Roles)
	}
	if _, ok := roles["a"]; ok {
		t.Fatalf("a rifle took a slot over the shotgun: %+v", memory.Roles)
	}
}

// The flank cells are asked for their standability with no hostile out
// yet (#897), and a flank cell the game did not find standable takes no
// responder: only (9,10) is manned.
func TestDecideCombatPodDoorwayChecksStandable(t *testing.T) {
	view := doorwayView()
	_, ask, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if ask == nil || !reflect.DeepEqual(ask.Cells, []domain.Cell{{X: 9, Z: 10}, {X: 11, Z: 10}}) || len(ask.Hostiles) != 0 {
		t.Fatalf("%+v", ask)
	}
	orders, _, memory := DecideCombat(view, GeometryReply{Answered: true, Standable: []domain.Cell{{X: 9, Z: 10}}}, StopEvent{}, CombatMemory{})
	var doorway []CombatRole
	for _, r := range memory.Roles {
		if r.Duty == DutyDoorway {
			doorway = append(doorway, r)
		}
	}
	if len(doorway) != 1 || *doorway[0].Cell != (domain.Cell{X: 9, Z: 10}) || orders[0].Door != DoorHoldOpen {
		t.Fatalf("%+v %+v", memory.Roles, orders)
	}
}
