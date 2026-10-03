package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// pikemenView is holdView against pikemen r1 at (9,5) and r2 at (1,12),
// r2 the nearer to rifleman a (at (1,30)), who wears a shield belt.
func pikemenView() CombatView {
	view := holdView()
	view.Positional[1].Position = domain.Known(domain.Cell{X: 1, Z: 12})
	for i := range view.Threats {
		view.Threats[i].Humanlike, view.Positional[i].Humanlike = domain.Known(false), domain.Known(false)
		cell, _ := view.Positional[i].Position.Value()
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(view.Threats[i].ID), Kind: "Mech_Pikeman", Mech: true, Weapon: "Gun_NeedleGun", WeaponRange: 44.9, Cell: domain.Known(cell)})
	}
	view.Pawns[0].ShieldBelt = true
	return view
}

// {pikemen only, shielded rifleman a} -> a attacks the nearest pikeman
// and leaves its firing cell; the others hold the line.
func TestDecideCombatPikemenShieldedCharge(t *testing.T) {
	orders, memory := decideStop(t, pikemenView(), StopEvent{}, CombatMemory{})
	want := []CombatOrder{
		{Pawn: "a", Kind: OrderAttack, Target: "r2", Reason: ReasonFormation},
		{Pawn: "b", Kind: OrderMove, Cell: domain.Cell{X: 8, Z: 23}, Reason: ReasonFormation},
		{Pawn: "c", Kind: OrderMove, Cell: domain.Cell{X: 10, Z: 23}, Reason: ReasonFormation},
	}
	if !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v\nwant %+v", orders, want)
	}
	if r := memory.Roles[0]; r.Pawn != "a" || r.Cell != nil || r.Target != "r2" {
		t.Fatalf("%+v", memory.Roles)
	}
}

// {a scyther still alive} -> a holds its firing cell; once the scyther is
// down, a charges.
func TestDecideCombatPikemenChargeLast(t *testing.T) {
	view := pikemenView()
	s, d := combatRaider("r3", domain.Cell{X: 12, Z: 6})
	s.Humanlike, d.Humanlike = domain.Known(false), domain.Known(false)
	view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
	view.Pawns = append(view.Pawns, CombatPawnState{ID: "r3", Kind: "Mech_Scyther", Mech: true, Cell: domain.Known(domain.Cell{X: 12, Z: 6})})
	orders, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	if want := (CombatOrder{Pawn: "a", Kind: OrderMove, Cell: domain.Cell{X: 9, Z: 23}, Reason: ReasonFormation}); len(orders) == 0 || orders[0] != want {
		t.Fatalf("%+v", orders)
	}
	view.Tick = 200
	view.Pawns[len(view.Pawns)-1].Downed = true
	view.Threats[2].Downed, view.Positional[2].Downed = domain.Known(true), domain.Known(true)
	orders, _ = decideStop(t, view, StopEvent{Kind: "downed", Pawn: "r3"}, memory)
	if want := (CombatOrder{Pawn: "a", Kind: OrderAttack, Target: "r2", Reason: ReasonFormation}); len(orders) == 0 || orders[0] != want {
		t.Fatalf("%+v", orders)
	}
}
