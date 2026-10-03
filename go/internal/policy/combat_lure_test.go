package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// combatMech is a live mech of kind at cell with a weapon range and speed.
type combatMech struct {
	id           PawnID
	kind         string
	cell         domain.Cell
	reach, speed float64
}

// withMechs replaces a view's hostiles with the mechs.
func withMechs(view CombatView, mechs ...combatMech) CombatView {
	view.Threats, view.Positional = nil, nil
	for _, h := range mechs {
		s, d := combatRaider(h.id, h.cell)
		s.Humanlike, d.Humanlike = domain.Known(false), domain.Known(false)
		view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(h.id), Cell: domain.Known(h.cell), Kind: h.kind, Mech: true, WeaponRange: h.reach, MoveSpeed: h.speed})
	}
	return view
}

// mechLureView is holdView with an inner line one step behind each firing
// cell, riflemen of range 30, and the mechs.
func mechLureView(mechs ...combatMech) CombatView {
	view := holdView()
	layout, _ := view.Layout.Value()
	layout.Retreat = []domain.Cell{{X: 9, Z: 24}, {X: 8, Z: 24}, {X: 10, Z: 24}}
	view.Layout = domain.Known(layout)
	for i := range view.Pawns {
		view.Pawns[i].WeaponRange = 30
	}
	return withMechs(view, mechs...)
}

// pikemen stand 40 cells south of the line with range 45.
func pikemen() []combatMech {
	return []combatMech{
		{id: "p1", kind: "Mech_Pikeman", cell: domain.Cell{X: 9, Z: -17}, reach: 45, speed: 2.5},
		{id: "p2", kind: "Mech_Pikeman", cell: domain.Cell{X: 10, Z: -18}, reach: 45, speed: 2.5},
	}
}

func moves(orders []CombatOrder) map[domain.PawnID]domain.Cell {
	out := map[domain.PawnID]domain.Cell{}
	for _, o := range orders {
		if o.Kind == OrderMove {
			out[o.Pawn] = o.Cell
		}
	}
	return out
}

// {pikemen range 45 at 40 cells, rifles range 30} -> gunners move to
// their inner-line cells, no attacks.
func TestDecideCombatMechLureOutrangedGunners(t *testing.T) {
	orders, m := decideStop(t, mechLureView(pikemen()...), StopEvent{}, CombatMemory{})
	if m.Tactic != TacticHold || !m.MechLure {
		t.Fatalf("%+v", m)
	}
	want := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 24}, "b": {X: 8, Z: 24}, "c": {X: 10, Z: 24}}
	if got := moves(orders); len(got) != 3 || got["a"] != want["a"] || got["b"] != want["b"] || got["c"] != want["c"] {
		t.Fatalf("%+v", orders)
	}
	if a := attacks(orders); len(a) != 0 {
		t.Fatalf("%+v", a)
	}
	for _, r := range m.Roles {
		if r.Target != "" {
			t.Fatalf("%+v", r)
		}
	}
}

// {a mech within 30 of the line} -> the gunners go back to their firing
// cells.
func TestDecideCombatMechLureEndsInRange(t *testing.T) {
	_, m := decideStop(t, mechLureView(pikemen()...), StopEvent{}, CombatMemory{})
	mechs := pikemen()
	mechs[0].cell = domain.Cell{X: 9, Z: 0}
	view := mechLureView(mechs...)
	view.Tick = 160
	orders, m := decideStop(t, view, StopEvent{}, m)
	if m.MechLure {
		t.Fatalf("%+v", m)
	}
	want := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 23}, "b": {X: 8, Z: 23}, "c": {X: 10, Z: 23}}
	if got := moves(orders); len(got) != 3 || got["a"] != want["a"] || got["b"] != want["b"] || got["c"] != want["c"] {
		t.Fatalf("%+v", orders)
	}
}

// sniperView is mechLureView with a human raid: snipers of range reach
// 40 cells south of the line.
func sniperView(reach float64) CombatView {
	view := mechLureView(
		combatMech{id: "s1", kind: "Pirate_Sniper", cell: domain.Cell{X: 9, Z: -17}, reach: reach, speed: 4.6},
		combatMech{id: "s2", kind: "Pirate_Sniper", cell: domain.Cell{X: 10, Z: -18}, reach: reach, speed: 4.6},
	)
	for i := range view.Threats {
		view.Threats[i].Humanlike, view.Positional[i].Humanlike = domain.Known(true), domain.Known(true)
	}
	return view
}

// {a sniper party of range 44.9, rifles range 30} -> gunners wait in
// cover on the inner line, no attacks.
func TestLureSniperParty(t *testing.T) {
	orders, m := decideStop(t, sniperView(44.9), StopEvent{}, CombatMemory{})
	if m.Tactic != TacticHold || !m.MechLure {
		t.Fatalf("%+v", m)
	}
	want := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 24}, "b": {X: 8, Z: 24}, "c": {X: 10, Z: 24}}
	if got := moves(orders); len(got) != 3 || got["a"] != want["a"] || got["b"] != want["b"] || got["c"] != want["c"] {
		t.Fatalf("%+v", orders)
	}
	if a := attacks(orders); len(a) != 0 {
		t.Fatalf("%+v", a)
	}
}

// {raiders of range 25 under our rifles' 30} -> the plain hold.
func TestNoLureWhenWeOutrange(t *testing.T) {
	orders, m := decideStop(t, sniperView(25), StopEvent{}, CombatMemory{})
	if m.Tactic != TacticHold || m.MechLure {
		t.Fatalf("%+v", m)
	}
	want := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 23}, "b": {X: 8, Z: 23}, "c": {X: 10, Z: 23}}
	if got := moves(orders); len(got) != 3 || got["a"] != want["a"] || got["b"] != want["b"] || got["c"] != want["c"] {
		t.Fatalf("%+v", orders)
	}
}

// {lancers, range under our rifles} -> the plain hold on the firing line.
func TestDecideCombatMechLancersHold(t *testing.T) {
	view := mechLureView(
		combatMech{id: "l1", kind: "Mech_Lancer", cell: domain.Cell{X: 9, Z: -17}, reach: 26.9, speed: 4.7},
		combatMech{id: "l2", kind: "Mech_Lancer", cell: domain.Cell{X: 10, Z: -18}, reach: 26.9, speed: 4.7},
	)
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticHold || m.MechLure {
		t.Fatalf("%+v", m)
	}
	want := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 23}, "b": {X: 8, Z: 23}, "c": {X: 10, Z: 23}}
	if got := moves(orders); len(got) != 3 || got["a"] != want["a"] || got["b"] != want["b"] || got["c"] != want["c"] {
		t.Fatalf("%+v", orders)
	}
}
