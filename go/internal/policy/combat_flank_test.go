package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// flankView is holdView with n riflemen on an n-cell line at z=23 and a
// choke at (9,18): the lab-open line with a walled approach.
func flankView(n int) CombatView {
	view := holdView()
	ids := []domain.PawnID{"a", "b", "c", "d", "e", "f"}[:n]
	view.Defenders, view.Pawns, view.Orderable = nil, nil, nil
	var firing []domain.Cell
	for i, id := range ids {
		view.Defenders = append(view.Defenders, combatRifleman(id))
		view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Cell: domain.Known(domain.Cell{X: int32(i), Z: 30}), Stance: StanceIdle, FireMode: FireAtWill})
		view.Orderable = append(view.Orderable, id)
		firing = append(firing, domain.Cell{X: int32(6 + i), Z: 23})
	}
	view.Pawns = append(view.Pawns, CombatPawnState{ID: "r1", Cell: domain.Known(domain.Cell{X: 9, Z: 5})}, CombatPawnState{ID: "r2", Cell: domain.Known(domain.Cell{X: 10, Z: 4})})
	view.Layout = domain.Known(CombatLayout{Firing: firing, Toward: domain.North, Choke: domain.Known(domain.Cell{X: 9, Z: 18})})
	return view
}

// flankStops runs the formation stop and the flank stop, the flank's
// firing_cells ask answered with proposals.
func flankStops(t *testing.T, view CombatView, proposals []domain.Cell) ([]CombatOrder, CombatMemory) {
	t.Helper()
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	view.Tick += 60
	orders, ask, next := DecideCombat(view, GeometryReply{}, StopEvent{}, m)
	if ask == nil || ask.Propose != RoleFiringCells || ask.From != (domain.Cell{X: 13, Z: 19}) || !reflect.DeepEqual(ask.Targets, []domain.Cell{{X: 9, Z: 18}}) {
		t.Fatalf("flank ask %+v", ask)
	}
	if len(orders) != 0 || !reflect.DeepEqual(next, m) {
		t.Fatal("a geometry ask came with orders or a memory change")
	}
	orders, again, next := DecideCombat(view, GeometryReply{Answered: true, Role: RoleFiringCells, Proposals: proposals, Standable: proposals}, StopEvent{}, m)
	if again != nil {
		t.Fatal("a second geometry round trip in one stop")
	}
	return orders, next
}

func flankProposals() []domain.Cell {
	return []domain.Cell{{X: 13, Z: 20}, {X: 13, Z: 19}, {X: 12, Z: 19}, {X: 15, Z: 19}}
}

// {5 riflemen, a choke, raiders south of it} -> d and e detach to spaced
// cells beside the approach with hold-fire and no attack; once a raider
// passes them, they open fire.
func TestFlankOpensAfterPass(t *testing.T) {
	view := flankView(5)
	orders, m := flankStops(t, view, flankProposals())
	if m.Flank == nil || !reflect.DeepEqual(m.Flank.Pawns, []domain.PawnID{"d", "e"}) || !reflect.DeepEqual(m.Flank.Cells, []domain.Cell{{X: 13, Z: 19}, {X: 15, Z: 19}}) || m.Flank.Open {
		t.Fatalf("%+v", m.Flank)
	}
	if got := moves(orders); got["d"] != (domain.Cell{X: 13, Z: 19}) || got["e"] != (domain.Cell{X: 15, Z: 19}) {
		t.Fatalf("%+v", orders)
	}
	held := map[domain.PawnID]bool{}
	for _, o := range orders {
		if o.Kind == OrderFireMode && o.FireMode == HoldFire {
			held[o.Pawn] = true
		}
	}
	if !held["d"] || !held["e"] || len(held) != 2 {
		t.Fatalf("%+v", orders)
	}
	if a := attacks(orders); a["d"] != "" || a["e"] != "" {
		t.Fatalf("%+v", a)
	}

	// The flankers in place and held; the raiders still short of them.
	for i := range view.Pawns {
		switch view.Pawns[i].ID {
		case "d":
			view.Pawns[i].Cell, view.Pawns[i].FireMode = domain.Known(domain.Cell{X: 13, Z: 19}), HoldFire
		case "e":
			view.Pawns[i].Cell, view.Pawns[i].FireMode = domain.Known(domain.Cell{X: 15, Z: 19}), HoldFire
		}
	}
	view.Tick = 220
	orders, m = decideStop(t, view, StopEvent{}, m)
	for _, o := range orders {
		if o.Pawn == "d" || o.Pawn == "e" {
			t.Fatalf("waiting flanker ordered: %+v", orders)
		}
	}

	// r1 passes the detachment: it opens fire.
	view.Positional[0].Position = domain.Known(domain.Cell{X: 9, Z: 20})
	for i := range view.Pawns {
		if view.Pawns[i].ID == "r1" {
			view.Pawns[i].Cell = domain.Known(domain.Cell{X: 9, Z: 20})
		}
	}
	view.Tick = 280
	orders, m = decideStop(t, view, StopEvent{}, m)
	if !m.Flank.Open {
		t.Fatalf("%+v", m.Flank)
	}
	opened := map[domain.PawnID]bool{}
	for _, o := range orders {
		if o.Kind == OrderFireMode && o.FireMode == FireAtWill {
			opened[o.Pawn] = true
		}
	}
	if !opened["d"] || !opened["e"] {
		t.Fatalf("%+v", orders)
	}
	for _, r := range m.Roles {
		if (r.Pawn == "d" || r.Pawn == "e") && r.Target == "" {
			t.Fatalf("%+v", m.Roles)
		}
	}
}

// {4 riflemen and a choke} -> no flank ask; the plain hold.
func TestNoFlankUnderFive(t *testing.T) {
	view := flankView(4)
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	view.Tick += 60
	orders, ask, next := DecideCombat(view, GeometryReply{}, StopEvent{}, m)
	if ask != nil && ask.Propose == RoleFiringCells {
		t.Fatalf("flank ask %+v", ask)
	}
	if next.Flank != nil || m.Tactic != TacticHold {
		t.Fatalf("%+v %+v", next, orders)
	}
}
