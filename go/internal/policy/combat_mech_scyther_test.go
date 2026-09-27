package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// scytherView is chokeView (riflemen a, b, c; brawlers d, e, f, g) against
// two scythers r1 and r2 walking in from the south: mechs, not humanlike,
// under an assault lord.
func scytherView() CombatView {
	view := chokeView()
	for i := range view.Threats {
		view.Threats[i].Humanlike = domain.Known(false)
		view.Positional[i].Humanlike = domain.Known(false)
		cell, _ := view.Positional[i].Position.Value()
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(view.Threats[i].ID), Kind: "Mech_Scyther", Weapon: "MeleeWeapon_ScytherBlade", Cell: domain.Known(cell)})
	}
	for i := range view.Pawns[:3] {
		view.Pawns[i].WeaponRange = 30
	}
	return view
}

// decideAny runs one stop, answering its one geometry ask: the choke cells
// for an adjacent_to_choke ask, every named cell standable, lines unknown.
func decideAny(t *testing.T, view CombatView, stop StopEvent, memory CombatMemory) ([]CombatOrder, CombatMemory) {
	t.Helper()
	orders, ask, next := DecideCombat(view, GeometryReply{}, stop, memory)
	if ask == nil {
		return orders, next
	}
	reply := GeometryReply{Answered: true, Standable: ask.Cells}
	if ask.Propose == RoleAdjacentToChoke {
		reply.Proposals = chokeCells
	}
	orders, again, next := DecideCombat(view, reply, stop, memory)
	if again != nil {
		t.Fatal("a second geometry round trip in one stop")
	}
	return orders, next
}

// {2 scythers, choke layout, brawlers + riflemen} -> the hold: the three
// best-armored brawlers block just outside the choke and every gunner
// focuses the first scyther. Once scyther r1 is in melee with blocker e,
// the gunners stay focused on it and never drift to r2; they hold fire
// while the blocker fights it (#861), and shoot it again once it breaks off.
func TestDecideCombatScythersBlockedAndFocused(t *testing.T) {
	view := scytherView()
	orders, memory := decideAny(t, view, StopEvent{}, CombatMemory{})
	move := func(p domain.PawnID, x, z int32) CombatOrder {
		return CombatOrder{Pawn: p, Kind: OrderMove, Cell: domain.Cell{X: x, Z: z}, Reason: ReasonFormation}
	}
	want := []CombatOrder{move("a", 9, 23), move("b", 8, 23), move("c", 10, 23), move("d", 10, 18), move("e", 9, 18), move("f", 8, 18)}
	if memory.Tactic != TacticHold || !reflect.DeepEqual(orders, want) {
		t.Fatalf("%s %q\n%+v\nwant %+v", memory.Tactic, memory.Refusal, orders, want)
	}

	// In place, the scythers still coming: every gunner attacks r1.
	view.Tick = 200
	at := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 23}, "b": {X: 8, Z: 23}, "c": {X: 10, Z: 23}, "d": {X: 10, Z: 18}, "e": {X: 9, Z: 18}, "f": {X: 8, Z: 18}}
	for i := range view.Pawns {
		if c, ok := at[view.Pawns[i].ID]; ok {
			view.Pawns[i].Cell, view.Pawns[i].FireMode = domain.Known(c), FireAtWill
		}
	}
	orders, memory = decideAny(t, view, StopEvent{Kind: "entered_range"}, memory)
	attack := func(p domain.PawnID) CombatOrder {
		return CombatOrder{Pawn: p, Kind: OrderAttack, Target: "r1", Reason: ReasonFormation}
	}
	if want := []CombatOrder{attack("a"), attack("b"), attack("c")}; !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v\nwant %+v", orders, want)
	}

	// r1 reaches the choke and fights blocker e; r2 comes on behind it.
	view.Tick = 260
	r1, r2 := domain.Cell{X: 9, Z: 17}, domain.Cell{X: 10, Z: 12}
	for i := range view.Pawns {
		switch view.Pawns[i].ID {
		case "a", "b", "c":
			view.Pawns[i].Target, view.Pawns[i].Stance = "r1", StanceIdle
		case "e":
			view.Pawns[i].Target, view.Pawns[i].Stance = "r1", StanceMelee
		case "r1":
			view.Pawns[i].Cell, view.Pawns[i].Target, view.Pawns[i].Stance = domain.Known(r1), "e", StanceMelee
		case "r2":
			view.Pawns[i].Cell = domain.Known(r2)
		}
	}
	view.Positional[0].Position, view.Positional[0].NearestColonistDistance = domain.Known(r1), domain.Known(1.0)
	view.Positional[1].Position, view.Positional[1].NearestColonistDistance = domain.Known(r2), domain.Known(6.0)
	orders, memory = decideAny(t, view, StopEvent{Kind: StopMeleeContact, Pawn: "r1", Target: "e"}, memory)
	if memory.Tactic != TacticHold {
		t.Fatalf("the scyther at the choke dropped the hold: %+v", memory)
	}
	for _, r := range memory.Roles {
		if r.Ranged && r.Target != "r1" {
			t.Fatalf("gunner %s off the scyther in melee: %+v", r.Pawn, memory.Roles)
		}
		if r.Duty == DutyBlocker && (r.Cell == nil || !reflect.DeepEqual(*r.Cell, at[r.Pawn])) {
			t.Fatalf("blocker %s left the choke: %+v", r.Pawn, memory.Roles)
		}
	}
	hold := func(p domain.PawnID) CombatOrder {
		return CombatOrder{Pawn: p, Kind: OrderFireMode, FireMode: HoldFire, Reason: ReasonHoldFire}
	}
	if want := []CombatOrder{hold("a"), hold("b"), hold("c")}; !reflect.DeepEqual(orders, want) {
		t.Fatalf("gunners not holding fire on the blocker's melee: %+v", orders)
	}

	// r1 breaks off: fire at will, then every gunner attacks r1 again.
	view.Tick = 320
	for i := range view.Pawns {
		switch view.Pawns[i].ID {
		case "a", "b", "c":
			view.Pawns[i].Target, view.Pawns[i].FireMode = "", HoldFire
		case "e", "r1":
			view.Pawns[i].Stance = StanceIdle
		}
	}
	_, memory = decideAny(t, view, StopEvent{}, memory)
	view.Tick = 380
	for i := range view.Pawns[:3] {
		view.Pawns[i].FireMode = FireAtWill
	}
	orders, _ = decideAny(t, view, StopEvent{}, memory)
	if want := []CombatOrder{attack("a"), attack("b"), attack("c")}; !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v\nwant %+v", orders, want)
	}
}
