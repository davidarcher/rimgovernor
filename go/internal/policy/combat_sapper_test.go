package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// sapperRoom is a planned room with floor x 5..14, z 20..27.
var sapperRoom = CombatRoom{Interior: Rectangle{X: 5, Z: 20, Width: 10, Height: 8}}

// withSappers adds the live state of sapper raiders at cells, replacing
// holdView's raiders.
func withSappers(view CombatView, cells map[PawnID]domain.Cell) CombatView {
	view.Threats, view.Positional = nil, nil
	for _, id := range []PawnID{"r1", "r2", "r3"} {
		c, ok := cells[id]
		if !ok {
			continue
		}
		s, d := combatRaider(id, c)
		view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(id), Cell: domain.Known(c), Sapper: true})
	}
	return view
}

// sapperView is holdView with brawler m, a room north of the raiders, and
// r1, r2 sappers digging from the south.
func sapperView() CombatView {
	view := withBrawlers(holdView(), combatBrawler("m", 0.5))
	view.Rooms = []CombatRoom{sapperRoom}
	return withSappers(view, map[PawnID]domain.Cell{"r1": {X: 9, Z: 5}, "r2": {X: 10, Z: 4}})
}

func roleCell(t *testing.T, m CombatMemory, id domain.PawnID) domain.Cell {
	t.Helper()
	for _, r := range m.Roles {
		if r.Pawn == id && r.Cell != nil {
			return *r.Cell
		}
	}
	t.Fatalf("%s has no cell: %+v", id, m.Roles)
	return domain.Cell{}
}

// {sapper raiders} -> TacticSapper; ordinary raiders keep the hold.
func TestDecideCombatSapperPicksSapperTactic(t *testing.T) {
	if _, m := decideStop(t, sapperView(), StopEvent{}, CombatMemory{}); m.Tactic != TacticSapper || m.SapperBreach == nil {
		t.Fatalf("%+v", m)
	}
	view := holdView()
	view.Rooms = []CombatRoom{sapperRoom}
	if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Tactic != TacticHold {
		t.Fatalf("ordinary raid: %+v", m)
	}
}

// {sappers south of a room, 3 riflemen, 1 brawler} -> riflemen 3 cells
// inside the south wall cell nearest the sappers, 2 apart, on a sapper;
// the brawler just inside the wall with no attack.
func TestDecideCombatSapperPostsInsidePredictedBreach(t *testing.T) {
	orders, m := decideStop(t, sapperView(), StopEvent{}, CombatMemory{})
	if *m.SapperBreach != (domain.Cell{X: 10, Z: 19}) {
		t.Fatalf("breach %v", *m.SapperBreach)
	}
	for id, want := range map[domain.PawnID]domain.Cell{"a": {X: 10, Z: 22}, "b": {X: 12, Z: 22}, "c": {X: 8, Z: 22}, "m": {X: 10, Z: 20}} {
		if got := roleCell(t, m, id); got != want {
			t.Errorf("%s at %v, want %v", id, got, want)
		}
	}
	for _, r := range m.Roles {
		if r.Ranged && r.Target != "r1" || !r.Ranged && (r.Target != "" || r.Duty != DutyBlocker) {
			t.Errorf("role %+v", r)
		}
	}
	for _, o := range orders {
		if o.Kind == OrderAttack && o.Pawn == "m" {
			t.Fatalf("brawler walks out: %+v", o)
		}
	}
}

// {sappers turn toward the room's east wall} -> re-form onto it.
func TestDecideCombatSapperRepositionsWhenBreachMoves(t *testing.T) {
	_, m := decideStop(t, sapperView(), StopEvent{}, CombatMemory{})
	view := withBrawlers(holdView(), combatBrawler("m", 0.5))
	view.Rooms = []CombatRoom{sapperRoom}
	view = withSappers(view, map[PawnID]domain.Cell{"r1": {X: 30, Z: 24}, "r2": {X: 31, Z: 23}})
	_, next := decideStop(t, view, StopEvent{}, m)
	if *next.SapperBreach != (domain.Cell{X: 15, Z: 24}) || roleCell(t, next, "m") != (domain.Cell{X: 14, Z: 24}) || roleCell(t, next, "a") != (domain.Cell{X: 12, Z: 24}) {
		t.Fatalf("%v %+v", *next.SapperBreach, next.Roles)
	}
	// The same prediction keeps the formation.
	if reformSapper(view, next) {
		t.Fatal("unchanged breach re-forms")
	}
}

// {no rooms} -> the ordinary formation.
func TestDecideCombatSapperWithoutRoomsKeepsFormation(t *testing.T) {
	view := sapperView()
	view.Rooms = nil
	if _, m := decideStop(t, view, StopEvent{}, CombatMemory{}); m.Tactic == TacticSapper || m.SapperBreach != nil {
		t.Fatalf("%+v", m)
	}
}
