package combatsim

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// crew is duel() with every pawn a colonist: friendly pawns with combat specs.
func crew(t testing.TB, rows ...string) Scenario {
	t.Helper()
	sc := duel(t, testRifle, nil, rows...)
	for i := range sc.Pawns {
		sc.Pawns[i].Side = combatmetrics.Colonist
	}
	return sc
}

func pos(st State, id domain.PawnID) domain.Cell {
	for _, p := range st.Pawns {
		if p.ID == id {
			return p.Pos
		}
	}
	return domain.Cell{}
}

// runAll steps until every pawn in want has arrived, failing on a block.
func runAll(t *testing.T, s *Sim, orders []Order, limit int, want ...domain.PawnID) []Event {
	t.Helper()
	pending := map[domain.PawnID]bool{}
	for _, id := range want {
		pending[id] = true
	}
	var all []Event
	for range limit {
		_, ev, err := s.Step(orders)
		if err != nil {
			t.Fatal(err)
		}
		orders = nil
		for _, e := range ev {
			all = append(all, e)
			if e.Kind == EventBlocked || e.Kind == EventStopped {
				t.Fatalf("pawn %s was held back: %v", e.Pawn, all)
			}
			if e.Kind == EventArrived {
				delete(pending, e.Pawn)
			}
		}
		if len(pending) == 0 {
			return all
		}
	}
	t.Fatalf("not arrived after %d steps: %v; events %v", limit, pending, all)
	return nil
}

// Friendly pawns walk through one another: the tick-0 formation swap of the
// lab-ranged colonists (a corridor row, a and c trading ends) is not a
// deadlock (A35; Pawn_PathFollower blocks only on PawnBlockedBy, which a
// friendly walking on a Goto never satisfies).
func TestFriendliesSwapThroughEachOther(t *testing.T) {
	s := newSim(t, crew(t, "abc.."), 1)
	runAll(t, s, []Order{
		{Pawn: "a", To: cell(2, 0)},
		{Pawn: "c", To: cell(0, 0)},
		{Pawn: "b", To: cell(4, 0)},
	}, 600, "a", "b", "c")
	st := s.snapshot()
	if pos(st, "a") != cell(2, 0) || pos(st, "c") != cell(0, 0) || pos(st, "b") != cell(4, 0) {
		t.Fatalf("final cells a=%v b=%v c=%v", pos(st, "a"), pos(st, "b"), pos(st, "c"))
	}
}

// Pawns may stand on one cell: two friendlies sent to the same cell both
// arrive and both stay, and a scenario may start them stacked.
func TestFriendliesShareACell(t *testing.T) {
	s := newSim(t, crew(t, "a.b.."), 1)
	runAll(t, s, []Order{{Pawn: "a", To: cell(4, 0)}, {Pawn: "b", To: cell(4, 0)}}, 600, "a", "b")
	if st := s.snapshot(); pos(st, "a") != cell(4, 0) || pos(st, "b") != cell(4, 0) {
		t.Fatalf("a=%v b=%v, want both on (4,0)", pos(st, "a"), pos(st, "b"))
	}
	if got := chain(s, cell(4, 0)); len(got) != 2 || got[0] != 1 || got[1] != 0 {
		t.Fatalf("cell list %v, want b (nearer, entered first) then a", got)
	}

	stacked := crew(t, "a....")
	stacked.Pawns = append(stacked.Pawns, stacked.Pawns[0])
	stacked.Pawns[1].ID = "b"
	s = newSim(t, stacked, 1)
	if got := chain(s, cell(0, 0)); len(got) != 2 {
		t.Fatalf("stacked start: cell list %v", got)
	}
}

func chain(s *Sim, c domain.Cell) []int {
	var out []int
	for k := s.cells.head[s.g.idx(c)]; k != 0; k = s.pawns[k-1].here {
		out = append(out, int(k)-1)
	}
	return out
}

// Leaving a shared cell (death, leaving the map) keeps the others listed.
func TestCellListSurvivesALeaver(t *testing.T) {
	sc := crew(t, "abc..")
	for i := range sc.Pawns {
		sc.Pawns[i].Pos = cell(0, 0)
	}
	s := newSim(t, sc, 1)
	s.leave(s.g.idx(cell(0, 0)), 1)
	if got := chain(s, cell(0, 0)); len(got) != 2 || got[0] != 0 || got[1] != 2 {
		t.Fatalf("after removing the middle: %v", got)
	}
	s.leave(s.g.idx(cell(0, 0)), 0)
	s.leave(s.g.idx(cell(0, 0)), 2)
	s.leave(s.g.idx(cell(0, 0)), 2) // absent: no-op
	if got := chain(s, cell(0, 0)); len(got) != 0 {
		t.Fatalf("emptied cell lists %v", got)
	}
}

// A hostile standing in the way does block (PawnBlockedBy: hostile), and with
// fewer than 15 nodes left the walker gives up on the second blocked tick
// (Pawn_PathFollower: attack the blocker, or EndCurrentJob; no melee here).
func TestHostileBlocksAndTheMoveEnds(t *testing.T) {
	s := newSim(t, duel(t, testRifle, nil, "a.b.."), 1)
	_, _, _ = s.Step([]Order{{Pawn: "a", To: cell(4, 0)}})
	var kinds []EventKind
	for range 400 {
		_, ev, err := s.Step(nil)
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range ev {
			if e.Pawn == "a" {
				kinds = append(kinds, e.Kind)
			}
		}
		if len(kinds) > 0 && kinds[len(kinds)-1] == EventStopped {
			break
		}
	}
	want := []EventKind{EventMoved, EventBlocked, EventStopped}
	if len(kinds) != len(want) {
		t.Fatalf("events %v, want %v", kinds, want)
	}
	for i := range want {
		if kinds[i] != want[i] {
			t.Fatalf("events %v, want %v", kinds, want)
		}
	}
	if st := s.snapshot(); pos(st, "a") != cell(1, 0) || st.Pawns[0].Moving {
		t.Fatalf("a at %v moving=%v, want standing at (1,0)", pos(st, "a"), st.Pawns[0].Moving)
	}
}

// With room to go round, the pathfinder prices the hostile's cell 175 more and
// walks round it: no block at all.
func TestPathGoesRoundAHostile(t *testing.T) {
	s := newSim(t, duel(t, testRifle, nil,
		".....",
		"a.b..",
		"....."), 1)
	runAll(t, s, []Order{{Pawn: "a", To: cell(4, 1)}}, 600, "a")
	if st := s.snapshot(); pos(st, "a") != cell(4, 1) {
		t.Fatalf("a ended at %v", pos(st, "a"))
	}
}

// A downed or dead hostile does not collide (ShouldCollideWithPawns), so a
// walker passes over the body.
func TestDownedHostileDoesNotBlock(t *testing.T) {
	s := newSim(t, duel(t, testRifle, nil, "a.b.."), 1)
	s.pawns[1].downed = true
	runAll(t, s, []Order{{Pawn: "a", To: cell(4, 0)}}, 600, "a")
}

// Two small-bodied pawns more than 3.57x apart in size share a cell
// (PawnsCanShareCellBecauseOfBodySize), so even a hostile does not block.
func TestBodySizeLetsPawnsShare(t *testing.T) {
	sc := duel(t, testRifle, func(id domain.PawnID, c *CombatSpec) {
		if id == "b" {
			c.BodySize = domain.Known(0.2)
		}
	}, "a.b..")
	s := newSim(t, sc, 1)
	runAll(t, s, []Order{{Pawn: "a", To: cell(4, 0)}}, 600, "a")
}

// Buildings block regardless of who is about: a wall across the corridor is
// not a path, and a friendly beyond it changes nothing.
func TestBuildingsStillBlockFriendlies(t *testing.T) {
	s := newSim(t, crew(t, "a.#.b"), 1)
	_, ev, err := s.Step([]Order{{Pawn: "a", To: cell(4, 0)}})
	if err != nil {
		t.Fatal(err)
	}
	if len(ev) != 1 || ev[0].Kind != EventPathFailed {
		t.Fatalf("events %v, want one path_failed", ev)
	}
}

// The holders' Autonomy runs after the policy's orders apply: a colonist just
// told to walk does not open fire at the same stop (a Goto replaces
// Wait_Combat before it can auto-attack).
func TestAutonomySeesTheOrdersJustApplied(t *testing.T) {
	sc := duel(t, testRifle, nil, "a.b...")
	a := adapterFor(t, sc)
	if got := a.Autonomy(); len(got) != 1 {
		t.Fatalf("idle colonist: %v, want one attack", got)
	}
	if err := a.sim.Apply([]Order{{Pawn: "a", Kind: OrderMove, To: cell(1, 0)}}); err != nil {
		t.Fatal(err)
	}
	if got := a.Autonomy(); len(got) != 0 {
		t.Fatalf("colonist told to walk: %v, want none", got)
	}
	// Events raised by Apply reach the next Step's events.
	_, ev, err := a.sim.Step(nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(ev) == 0 || ev[0].Kind != EventMoveStarted {
		t.Fatalf("events %v, want the applied order's move_started first", ev)
	}
}
