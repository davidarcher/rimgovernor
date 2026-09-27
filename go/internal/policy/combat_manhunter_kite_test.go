package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// kiteView is holdView with an inner line at z=24..26 behind the firing
// line, rifleman b fast (6.0 cells/s), a and c at a colonist's 4.6, and
// muffalo at speed facing the line from z=5.
func kiteView(speed float64) CombatView {
	view := holdView()
	layout, _ := view.Layout.Value()
	layout.Retreat = []domain.Cell{{X: 9, Z: 24}, {X: 8, Z: 26}, {X: 10, Z: 25}}
	view.Layout = domain.Known(layout)
	for i := range view.Pawns {
		view.Pawns[i].MoveSpeed = 4.6
		if view.Pawns[i].ID == "b" {
			view.Pawns[i].MoveSpeed = 6.0
		}
	}
	return withAnimals(view, animal("m1", "Muffalo", domain.Cell{X: 9, Z: 5}, speed), animal("m2", "Muffalo", domain.Cell{X: 10, Z: 4}, speed))
}

func role(m CombatMemory, id domain.PawnID) CombatRole {
	for _, r := range m.Roles {
		if r.Pawn == id {
			return r
		}
	}
	return CombatRole{}
}

// {slow pack, a defender at >=1.2x the animals' speed, not targeted} ->
// that defender baits: an attack from where it stands.
func TestDecideCombatManhunterKiterBaits(t *testing.T) {
	orders, m := decideStop(t, kiteView(3.5), StopEvent{}, CombatMemory{})
	if m.Kiter != "b" || m.Leading {
		t.Fatalf("%+v", m)
	}
	if r := role(m, "b"); r.Duty != DutyKiter || r.Cell != nil || r.Target == "" {
		t.Fatalf("%+v", r)
	}
	for _, o := range orders {
		if o.Pawn == "b" && o.Kind != OrderAttack {
			t.Fatalf("%+v", orders)
		}
	}
}

// {an animal targets the kiter} -> it retreats to the rearmost inner-line
// cell, past the aim guard.
func TestDecideCombatManhunterKiterLeadsPastLine(t *testing.T) {
	_, m := decideStop(t, kiteView(3.5), StopEvent{}, CombatMemory{})
	view := kiteView(3.5)
	view.Tick = 160
	for i := range view.Pawns {
		if view.Pawns[i].ID == "m1" {
			view.Pawns[i].Target = "b"
		}
		if view.Pawns[i].ID == "b" {
			view.Pawns[i].Stance = StanceWarmup
		}
	}
	orders, m := decideStop(t, view, StopEvent{}, m)
	want := domain.Cell{X: 8, Z: 26}
	if r := role(m, "b"); !m.Leading || r.Cell == nil || *r.Cell != want {
		t.Fatalf("%+v %+v", m, r)
	}
	found := false
	for _, o := range orders {
		found = found || o.Pawn == "b" && o.Kind == OrderMove && o.Cell == want && o.Reason == ReasonRetreat
	}
	if !found {
		t.Fatalf("%+v", orders)
	}
}

// {a fast animal, or no defender fast enough} -> no kiter.
func TestDecideCombatManhunterNoKiterForFastAnimals(t *testing.T) {
	for _, c := range []struct{ animal, b float64 }{{6.8, 6.0}, {4.5, 5.0}} {
		view := kiteView(c.animal)
		for i := range view.Pawns {
			if view.Pawns[i].ID == "b" {
				view.Pawns[i].MoveSpeed = c.b
			}
		}
		if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Kiter != "" {
			t.Fatalf("%+v: %+v", c, m)
		}
	}
}
