package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// harassView is siegeView camped past the first mortar frame: besiegers with
// range 20, rifleman a range 30, b and c range 20.
func harassView() (CombatView, CombatMemory) {
	view := siegeView(siegeCampToil)
	for i, p := range view.Pawns {
		switch p.ID {
		case "a":
			view.Pawns[i].WeaponRange = 30
		default:
			view.Pawns[i].WeaponRange = 20
		}
	}
	return view, CombatMemory{SiegeCamp: view.Tick - 1, SiegeMortar: true}
}

// {past the first mortar frame, a outranges, b and c do not} -> a moves to 27
// cells from its nearest besieger (r2) and attacks the camp (focus fire
// may pick r1); b and c no attack.
func TestDecideCombatSiegeHarassesFromOutrange(t *testing.T) {
	view, memory := harassView()
	_, m := decideStop(t, view, StopEvent{}, memory)
	if m.SiegeMode != SiegeHarass {
		t.Fatalf("%+v", m)
	}
	cell := roleCell(t, m, "a")
	if d := math.Hypot(float64(cell.X-10), float64(cell.Z+19)); math.Abs(d-27) > 1 {
		t.Fatalf("a at %v, %.1f from r2", cell, d)
	}
	for _, r := range m.Roles {
		if r.Pawn == "a" && (r.Duty != DutyHarasser || r.Target == "") {
			t.Fatalf("a role %+v", r)
		}
	}
	// a is still walking out: its attack goes once it stands on the cell.
	at := view
	for i := range at.Pawns {
		if at.Pawns[i].ID == "a" {
			at.Pawns[i].Cell = domain.Known(cell)
		}
	}
	at.Tick++
	orders, _ := decideStop(t, at, StopEvent{}, m)
	a := attacks(orders)
	if a["a"] == "" || a["b"] != "" || a["c"] != "" {
		t.Fatalf("attacks %v", a)
	}
}

// {nobody outranges the camp} -> no harasser, nobody attacks.
func TestDecideCombatSiegeHarassSkipsOutranged(t *testing.T) {
	view, memory := harassView()
	for i := range view.Pawns {
		view.Pawns[i].WeaponRange = 20
	}
	orders, m := decideStop(t, view, StopEvent{}, memory)
	if a := attacks(orders); len(a) != 0 {
		t.Fatalf("attacks %v", a)
	}
	for _, r := range m.Roles {
		if r.Duty == DutyHarasser {
			t.Fatalf("harasser %+v", r)
		}
	}
}

// shipPartView is holdView with a defoliator ship part 40 cells south of
// the line and rifleman a at range 30.
func shipPartView(mechs ...combatMech) CombatView {
	view := holdView()
	if len(mechs) > 0 {
		view = withMechs(view, mechs...)
	}
	view.Structures = []HostileStructure{{ID: "Thing_DefoliatorShipPart2", Def: "DefoliatorShipPart", Cell: domain.Cell{X: 9, Z: -10}}}
	for i := range view.Pawns {
		if view.Pawns[i].ID == "a" {
			view.Pawns[i].WeaponRange = 30
		}
	}
	return view
}

// TestHitAndRunShipPart: {a ship part, no live mech} -> the gunner stands
// off the part at 0.9 of its range and targets it; {a live scyther} ->
// no hit-and-run (#1061).
func TestHitAndRunShipPart(t *testing.T) {
	_, m := decideStop(t, shipPartView(), StopEvent{}, CombatMemory{})
	r := role(m, "a")
	if r.Duty != DutyHarasser || r.Target != "Thing_DefoliatorShipPart2" || r.Cell == nil {
		t.Fatalf("%+v", r)
	}
	if d := math.Hypot(float64(r.Cell.X-9), float64(r.Cell.Z+10)); math.Abs(d-27) > 1 {
		t.Fatalf("a at %v, %.1f from the part", *r.Cell, d)
	}
	_, m = decideStop(t, shipPartView(combatMech{id: "s1", kind: "Mech_Scyther", cell: domain.Cell{X: 9, Z: -8}, speed: 4.7}), StopEvent{}, CombatMemory{})
	for _, r := range m.Roles {
		if r.Duty == DutyHarasser {
			t.Fatalf("hit-and-run with a live scyther: %+v", r)
		}
	}
}
