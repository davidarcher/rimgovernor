package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// twoGroupView is flankView(4) (no flank detachment) with a second raider
// pair east of the line at (40,20) and (41,21): lab-open with two spawns.
func twoGroupView() CombatView {
	view := flankView(4)
	for _, r := range []struct {
		id   PawnID
		cell domain.Cell
	}{{"r3", domain.Cell{X: 40, Z: 20}}, {"r4", domain.Cell{X: 41, Z: 21}}} {
		s, d := combatRaider(r.id, r.cell)
		view.Threats = append(view.Threats, s)
		view.Positional = append(view.Positional, d)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(r.id), Cell: domain.Known(r.cell)})
	}
	return view
}

func setCell(view *CombatView, id domain.PawnID, c domain.Cell) {
	for i := range view.Pawns {
		if view.Pawns[i].ID == id {
			view.Pawns[i].Cell = domain.Known(c)
		}
	}
	for i := range view.Positional {
		if domain.PawnID(view.Positional[i].ID) == id {
			view.Positional[i].Position = domain.Known(c)
		}
	}
}

// {4 riflemen on the line, r1+r2 at the choke's approach, r3+r4 east} ->
// the south group keeps the hold (a, b); the east group (half the
// hostiles) gets c and d at spaced cells around the midpoint (23,21) of
// its centroid (40,20) and home (7,23), attacking an east raider; when an
// east raider closes to 3 cells, c and d fall back one step toward home.
func TestTwoGroupsTwoSquads(t *testing.T) {
	view := twoGroupView()
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticHold {
		t.Fatalf("%+v", m)
	}
	view.Tick += 60
	orders, ask, next := DecideCombat(view, GeometryReply{}, StopEvent{}, m)
	if ask == nil || ask.Propose != RoleFiringCells || ask.From != (domain.Cell{X: 23, Z: 21}) || !reflect.DeepEqual(ask.Targets, []domain.Cell{{X: 40, Z: 20}}) {
		t.Fatalf("group ask %+v", ask)
	}
	if len(orders) != 0 || !reflect.DeepEqual(next, m) {
		t.Fatal("a geometry ask came with orders or a memory change")
	}
	proposals := []domain.Cell{{X: 24, Z: 21}, {X: 23, Z: 21}, {X: 25, Z: 21}, {X: 30, Z: 21}}
	orders, again, m := DecideCombat(view, GeometryReply{Answered: true, Role: RoleFiringCells, Proposals: proposals, Standable: proposals}, StopEvent{}, m)
	if again != nil {
		t.Fatal("a second geometry round trip in one stop")
	}
	if len(m.Groups) != 1 {
		t.Fatalf("%+v", m.Groups)
	}
	g := m.Groups[0]
	if !reflect.DeepEqual(g.Hostiles, []domain.PawnID{"r3", "r4"}) || !reflect.DeepEqual(g.Pawns, []domain.PawnID{"c", "d"}) ||
		!reflect.DeepEqual(g.Cells, []domain.Cell{{X: 23, Z: 21}, {X: 25, Z: 21}}) || !reflect.DeepEqual(g.Fallback, []domain.Cell{{X: 22, Z: 22}, {X: 24, Z: 22}}) {
		t.Fatalf("%+v", g)
	}
	if got := moves(orders); got["c"] != (domain.Cell{X: 23, Z: 21}) || got["d"] != (domain.Cell{X: 25, Z: 21}) {
		t.Fatalf("%+v", orders)
	}
	for _, r := range m.Roles {
		switch r.Pawn {
		case "a", "b":
			if r.Cell == nil || r.Cell.Z != 23 {
				t.Fatalf("main line left: %+v", r)
			}
		case "c", "d":
			if r.Target != "r3" && r.Target != "r4" {
				t.Fatalf("squad target %+v", r)
			}
		}
	}

	// A later stop asks nothing more for the answered group.
	view.Tick += 60
	if _, ask, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, m); ask != nil && ask.Propose == RoleFiringCells {
		t.Fatalf("asked again %+v", ask)
	}

	// r3 closes on the squad: it falls back one step toward home.
	setCell(&view, "c", domain.Cell{X: 23, Z: 21})
	setCell(&view, "d", domain.Cell{X: 25, Z: 21})
	setCell(&view, "r3", domain.Cell{X: 27, Z: 20})
	setCell(&view, "r4", domain.Cell{X: 28, Z: 20})
	orders, m = decideStop(t, view, StopEvent{}, m)
	if !m.Groups[0].FellBack {
		t.Fatalf("%+v", m.Groups)
	}
	if got := moves(orders); got["c"] != (domain.Cell{X: 22, Z: 22}) || got["d"] != (domain.Cell{X: 24, Z: 22}) {
		t.Fatalf("%+v", orders)
	}
}

// {one group of four} -> no group ask.
func TestOneGroupNoSquad(t *testing.T) {
	view := flankView(4)
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	view.Tick += 60
	if _, ask, next := DecideCombat(view, GeometryReply{}, StopEvent{}, m); ask != nil && ask.Propose == RoleFiringCells || len(next.Groups) != 0 {
		t.Fatalf("%+v", ask)
	}
}
