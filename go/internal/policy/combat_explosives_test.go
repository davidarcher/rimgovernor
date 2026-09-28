package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// armRaider gives raider id a weapon and range at its positional cell.
func armRaider(view *CombatView, id domain.PawnID, weapon string, reach float64) {
	for _, d := range view.Positional {
		if domain.PawnID(d.ID) == id {
			cell, _ := d.Position.Value()
			view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Weapon: weapon, WeaponRange: reach, Cell: domain.Known(cell)})
		}
	}
}

// {grenadier r1, a long firing line, the far end best covered} -> gunners
// three apart from the line's start, cover not ranked, every gunner on
// the grenadier it outranges; {no explosives} -> the covered cell first.
func TestExplosivesSpread(t *testing.T) {
	at := func(xs ...int32) []domain.Cell {
		var out []domain.Cell
		for _, x := range xs {
			out = append(out, domain.Cell{X: x, Z: 23})
		}
		return out
	}
	reply := GeometryReply{Answered: true, Scored: []ScoredCell{{Cell: domain.Cell{X: 15, Z: 23}, Lines: []CoverLine{{Hostile: "r1", Cover: 0.75, LineOfFire: true}}}}}
	cells := func(view CombatView) ([]domain.Cell, CombatMemory) {
		_, ask, memory := DecideCombat(view, reply, StopEvent{}, CombatMemory{})
		if ask != nil || memory.Tactic != TacticHold {
			t.Fatalf("ask %+v refusal %q", ask, memory.Refusal)
		}
		var out []domain.Cell
		for _, r := range memory.Roles {
			out = append(out, *r.Cell)
		}
		return out, memory
	}
	plain := lineView(4, 15, false)
	if got, _ := cells(plain); got[0] != (domain.Cell{X: 15, Z: 23}) {
		t.Fatalf("no explosives: %v", got)
	}
	view := lineView(4, 15, false)
	armRaider(&view, "r1", "Weapon_GrenadeFrag", 12.9)
	for i := range view.Pawns[:3] {
		view.Pawns[i].WeaponRange = 30
	}
	got, memory := cells(view)
	if !reflect.DeepEqual(got, at(4, 7, 10)) {
		t.Fatalf("grenadier: %v", got)
	}
	for _, r := range memory.Roles {
		if r.Target != "r1" {
			t.Fatalf("%+v", memory.Roles)
		}
	}
}

// {rocketeers r1 at (9,5) and r2 at (1,12), shielded brawler d at (5,30),
// shielded rifleman a} -> d attacks the nearer rocketeer r2 and leaves
// any cell; the rifleman a keeps its firing cell.
func TestExplosivesShieldedCharge(t *testing.T) {
	view := holdView()
	view.Positional[1].Position = domain.Known(domain.Cell{X: 1, Z: 12})
	armRaider(&view, "r1", "Weapon_RocketswarmLauncher", 26.9)
	armRaider(&view, "r2", "Weapon_RocketswarmLauncher", 26.9)
	view.Defenders = append(view.Defenders, combatBrawler("d", 0.3))
	view.Pawns = append(view.Pawns, CombatPawnState{ID: "d", Cell: domain.Known(domain.Cell{X: 5, Z: 30}), Stance: StanceIdle, ShieldBelt: true})
	view.Orderable = append(view.Orderable, "d")
	view.Pawns[0].ShieldBelt = true
	orders, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	var d, a CombatRole
	for _, r := range memory.Roles {
		switch r.Pawn {
		case "d":
			d = r
		case "a":
			a = r
		}
	}
	if d.Pawn != "d" || d.Cell != nil || d.Target != "r2" || a.Cell == nil {
		t.Fatalf("%+v", memory.Roles)
	}
	want := CombatOrder{Pawn: "d", Kind: OrderAttack, Target: "r2", Reason: ReasonFormation}
	for _, o := range orders {
		if o.Pawn == "d" && o == want {
			return
		}
	}
	t.Fatalf("%+v", orders)

}

// {rocketeer r1 at (9,5) reaching 18.5, a line at z=23 with its inner
// line at z=24} -> the gunners take inner-line cells out of rocket range.
func TestGunnersOutsideRocketRange(t *testing.T) {
	view := lineView(4, 15, false)
	layout, _ := view.Layout.Value()
	for _, f := range layout.Firing {
		layout.Retreat = append(layout.Retreat, domain.Cell{X: f.X, Z: f.Z + 1})
	}
	view.Layout = domain.Known(layout)
	armRaider(&view, "r1", "Weapon_RocketswarmLauncher", 18.5)
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	// The first outside cells in order: (4,23) and (14,23) on the line
	// (18.7 tiles off), then (7,24) on the inner line.
	want := []domain.Cell{{X: 4, Z: 23}, {X: 14, Z: 23}, {X: 7, Z: 24}}
	var got []domain.Cell
	for _, r := range memory.Roles {
		got = append(got, *r.Cell)
		if d := distance2(*r.Cell, domain.Cell{X: 9, Z: 5}); float64(d) <= 18.5*18.5 {
			t.Fatalf("%v in rocket range", *r.Cell)
		}
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("%v", got)
	}
	// Without the rocketeer the line holds z=23.
	if got := firingCells(t, lineView(4, 15, false)); got[0].Z != 23 {
		t.Fatalf("%v", got)
	}
}
