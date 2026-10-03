package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// lineView is holdView with a firing line at z=23 from x=from to x=to, in
// order; with centipede set, raider r1 is a live centipede gunner (a mech,
// not humanlike).
func lineView(from, to int32, centipede bool) CombatView {
	view := holdView()
	var firing []domain.Cell
	for x := from; x <= to; x++ {
		firing = append(firing, domain.Cell{X: x, Z: 23})
	}
	view.Layout = domain.Known(CombatLayout{Firing: firing, Toward: domain.North})
	if centipede {
		cell, _ := view.Positional[0].Position.Value()
		view.Threats[0].Humanlike, view.Positional[0].Humanlike = domain.Known(false), domain.Known(false)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: "r1", Kind: "Mech_CentipedeGunner", Mech: true, Weapon: "Gun_HeavyChargeBlaster", Cell: domain.Known(cell)})
	}
	return view
}

// firingCells are the cells the formation gave the riflemen, in id order.
func firingCells(t *testing.T, view CombatView) []domain.Cell {
	t.Helper()
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	if memory.Tactic != TacticHold {
		t.Fatalf("no hold: %q", memory.Refusal)
	}
	var out []domain.Cell
	for _, r := range memory.Roles {
		out = append(out, *r.Cell)
	}
	return out
}

// {centipede raid, a long firing line} -> riflemen on cells at least 3
// apart; a short line packs tighter only past that; {ordinary raid} ->
// the one-tile spacing, unchanged.
func TestDecideCombatCentipedesSpreadGunners(t *testing.T) {
	at := func(xs ...int32) []domain.Cell {
		var out []domain.Cell
		for _, x := range xs {
			out = append(out, domain.Cell{X: x, Z: 23})
		}
		return out
	}
	if got := firingCells(t, lineView(4, 15, true)); !reflect.DeepEqual(got, at(4, 7, 10)) {
		t.Fatalf("centipede, long line: %v", got)
	}
	if got := firingCells(t, lineView(6, 11, true)); !reflect.DeepEqual(got, at(6, 9, 11)) {
		t.Fatalf("centipede, short line: %v", got)
	}
	if got := firingCells(t, lineView(4, 15, false)); !reflect.DeepEqual(got, at(4, 6, 8)) {
		t.Fatalf("ordinary raid: %v", got)
	}
	// A downed centipede spreads no one.
	view := lineView(4, 15, true)
	view.Pawns[len(view.Pawns)-1].Downed = true
	view.Threats = view.Threats[1:]
	view.Positional = view.Positional[1:]
	if got := firingCells(t, view); !reflect.DeepEqual(got, at(4, 6, 8)) {
		t.Fatalf("downed centipede: %v", got)
	}
}
