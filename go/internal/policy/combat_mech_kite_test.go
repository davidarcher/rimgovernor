package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// fastRifleman sets rifleman b to 6.0 cells/s and the others to 4.6.
func fastRifleman(view CombatView) CombatView {
	for i := range view.Pawns {
		switch view.Pawns[i].ID {
		case "a", "c":
			view.Pawns[i].MoveSpeed = 4.6
		case "b":
			view.Pawns[i].MoveSpeed = 6.0
		}
	}
	return view
}

// centipedeView is holdView with the kite test's inner line, rifleman b
// fast, and two centipedes at 1.9 cells/s walking in from the south.
func centipedeView(extra ...combatMech) CombatView {
	view := holdView()
	layout, _ := view.Layout.Value()
	layout.Retreat = []domain.Cell{{X: 9, Z: 24}, {X: 8, Z: 26}, {X: 10, Z: 25}}
	view.Layout = domain.Known(layout)
	mechs := append([]combatMech{
		{id: "c1", kind: "Mech_CentipedeGunner", cell: domain.Cell{X: 9, Z: 5}, speed: 1.9},
		{id: "c2", kind: "Mech_CentipedeBlaster", cell: domain.Cell{X: 10, Z: 4}, speed: 1.9},
	}, extra...)
	return fastRifleman(withMechs(view, mechs...))
}

func targeting(view CombatView, hostile, pawn domain.PawnID) CombatView {
	for i := range view.Pawns {
		if view.Pawns[i].ID == hostile {
			view.Pawns[i].Target = pawn
		}
	}
	return view
}

// {centipedes 1.9, rifleman 6, hold with an inner line} -> the kiter
// attacks the nearest centipede, then leads to the rearmost inner-line
// cell once targeted.
func TestDecideCombatMechKitesSlowCentipedes(t *testing.T) {
	orders, m := decideStop(t, centipedeView(), StopEvent{}, CombatMemory{})
	if m.Tactic != TacticHold || m.Kiter != "b" || m.Leading {
		t.Fatalf("%+v", m)
	}
	if a := attacks(orders); a["b"] != "c1" {
		t.Fatalf("%+v", orders)
	}
	view := targeting(centipedeView(), "c1", "b")
	view.Tick = 160
	orders, m = decideStop(t, view, StopEvent{}, m)
	want := domain.Cell{X: 8, Z: 26}
	if r := role(m, "b"); !m.Leading || r.Cell == nil || *r.Cell != want {
		t.Fatalf("%+v %+v", m, r)
	}
	if got := moves(orders); got["b"] != want {
		t.Fatalf("%+v", orders)
	}
}

// {a scyther with the centipedes} -> no kiter.
func TestDecideCombatMechNoKiteWithScythers(t *testing.T) {
	view := centipedeView(combatMech{id: "s1", kind: "Mech_Scyther", cell: domain.Cell{X: 11, Z: 5}, speed: 4.7})
	if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Tactic != TacticHold || m.Kiter != "" {
		t.Fatalf("%+v", m)
	}
}

// {mech breachers, sapper tactic} -> the kiter baits, then leads to the
// breach's first gunner post inside the room.
func TestDecideCombatMechKitesBreachersInside(t *testing.T) {
	breachers := func() CombatView {
		view := withBrawlers(holdView(), combatBrawler("m", 0.5))
		view.Rooms = []CombatRoom{sapperRoom}
		view = withMechs(view,
			combatMech{id: "t1", kind: "Mech_Termite", cell: domain.Cell{X: 9, Z: 5}, speed: 2.1},
			combatMech{id: "t2", kind: "Mech_Termite", cell: domain.Cell{X: 10, Z: 4}, speed: 2.1})
		for i := range view.Pawns {
			view.Pawns[i].Sapper = view.Pawns[i].Kind == "Mech_Termite"
		}
		return fastRifleman(view)
	}
	_, m := decideStop(t, breachers(), StopEvent{}, CombatMemory{})
	if m.Tactic != TacticSapper || m.Kiter != "b" || m.Leading {
		t.Fatalf("%+v", m)
	}
	view := targeting(breachers(), "t1", "b")
	view.Tick = 160
	orders, m := decideStop(t, view, StopEvent{}, m)
	want := domain.Cell{X: 10, Z: 22}
	if r := role(m, "b"); !m.Leading || r.Cell == nil || *r.Cell != want || !sapperRoom.contains(want) {
		t.Fatalf("%+v %+v", m, r)
	}
	if got := moves(orders); got["b"] != want {
		t.Fatalf("%+v", orders)
	}
}
