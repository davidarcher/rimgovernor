package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// harassView is siegeView camped past the sortie window: besiegers with
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
	return view, CombatMemory{SiegeCamp: view.Tick - siegeSortieWindow - 1}
}

// {camp past the window, a outranges, b and c do not} -> a moves to 27
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
