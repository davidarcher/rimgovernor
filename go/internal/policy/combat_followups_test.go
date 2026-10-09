package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Snapshot tests for follow-up responses to the blocking, peel and tank
// roles. Item 1 (frame armor) is buildingruntime's
// TestCombatViewFillsDefenderArmorFromFrame.

// answerWithout answers view's formation ask with every named cell
// standable except the given ones.
func answerWithout(t *testing.T, view CombatView, reply GeometryReply, not ...domain.Cell) ([]CombatOrder, CombatMemory) {
	t.Helper()
	_, ask, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if ask == nil {
		t.Fatal("no formation ask")
	}
	reply.Answered = true
	reply.Standable = slices.DeleteFunc(slices.Clone(ask.Cells), func(c domain.Cell) bool { return slices.Contains(not, c) })
	orders, _, memory := DecideCombat(view, reply, StopEvent{}, CombatMemory{})
	return orders, memory
}

// Item 2: the Go-computed tank, pull-back and peeler-home cells are named
// in the ask and used only when the game reports them standable.
func TestFormationChecksGoComputedCells(t *testing.T) {
	view := tankView()
	_, ask, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	for _, c := range []domain.Cell{{X: 9, Z: 22}, {X: 9, Z: 24}, {X: 8, Z: 22}, {X: 10, Z: 24}} {
		if ask == nil || !slices.Contains(ask.Cells, c) {
			t.Fatalf("%v not named: %+v", c, ask)
		}
	}
	// (9,22), in front of a, is cover and (9,24), behind a, is not
	// standable: the first tank takes the nearest standable cell ahead of
	// a, (8,22), with no pull-back cell; b's front cell is then
	// taken, so the second stands at (7,22) with (8,24) behind b.
	_, memory := answerWithout(t, view, GeometryReply{}, domain.Cell{X: 9, Z: 22}, domain.Cell{X: 9, Z: 24})
	tanks := map[domain.PawnID]CombatRole{}
	for _, r := range memory.Roles {
		if r.Duty == DutyTank {
			tanks[r.Pawn] = r
		}
	}
	s, u := tanks["s"], tanks["t"]
	if s.Cell == nil || *s.Cell != (domain.Cell{X: 8, Z: 22}) || s.Home != nil ||
		u.Cell == nil || *u.Cell != (domain.Cell{X: 7, Z: 22}) || u.Home == nil || *u.Home != (domain.Cell{X: 8, Z: 24}) {
		t.Fatalf("%+v", tanks)
	}
	// Its shield broken, the tank without a pull-back cell holds where it
	// stands rather than walk to an unchecked cell.
	next := memory.clone()
	pullBackTank(StopEvent{Kind: StopShieldBroken, Pawn: "s"}, &next)
	for _, r := range next.Roles {
		if r.Pawn == "s" && (r.Cell != nil || r.Duty != "" || !r.Retreat) {
			t.Fatalf("%+v", r)
		}
	}
	// The peeler's home (9,25) unstandable: the peeler waits unplaced.
	_, memory = answerWithout(t, peelView(), GeometryReply{}, domain.Cell{X: 9, Z: 25})
	for _, r := range memory.Roles {
		if r.Duty == DutyPeeler && (r.Cell != nil || r.Home != nil) {
			t.Fatalf("%+v", r)
		}
	}
}

// Item 3: a melee attacker on a gunner stands on the cover row; with a
// peeler it is the peeler's contact, not a compromised hold, so no gunner
// falls back. Without a peeler the same contact pulls the line back.
func TestPeelableContactDoesNotCompromiseHold(t *testing.T) {
	contact := func(view CombatView) []CombatOrder {
		_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
		settle(&view, memory)
		view.Tick = 260
		view.Positional[0].Position = domain.Known(domain.Cell{X: 9, Z: 22})
		view.Positional[0].NearestColonistDistance = domain.Known(1.0)
		for i := range view.Pawns {
			if view.Pawns[i].ID == "r1" {
				view.Pawns[i].Stance, view.Pawns[i].Target = StanceMelee, "a"
			}
		}
		orders, next := decideStop(t, view, StopEvent{Kind: StopMeleeContact, Pawn: "r1", Target: "a"}, memory)
		if next.Tactic != TacticHold {
			t.Fatalf("tactic %s", next.Tactic)
		}
		return orders
	}
	for _, o := range contact(peelView()) {
		if o.Reason == ReasonRetreat {
			t.Fatalf("with a peeler, %+v", o)
		}
	}
	view := peelView()
	view.Defenders, view.Orderable = view.Defenders[:3], view.Orderable[:3]
	view.Pawns = view.Pawns[:3]
	retreats := 0
	for _, o := range contact(view) {
		if o.Reason == ReasonRetreat {
			retreats++
		}
	}
	if retreats != 3 {
		t.Fatalf("without a peeler, %d retreats", retreats)
	}
}

// Item 4: a re-formation after a blocker swap keeps the rotation: the
// relieved blocker ranks last among the brawlers, so it stays in reserve.
func TestReformationKeepsBlockerRotation(t *testing.T) {
	view := chokeView()
	_, memory := decideChoke(t, view, StopEvent{}, CombatMemory{})
	view.Tick = 400
	view.Pawns[4].Stance = StanceCooldown // e, the best-armored blocker
	_, next := decideChoke(t, view, StopEvent{Kind: StopSeriousInjury, Pawn: "e", Target: "r1"}, memory)
	if !reflect.DeepEqual(next.Relieved, []domain.PawnID{"e"}) {
		t.Fatalf("relieved %v", next.Relieved)
	}
	_, roles, _ := formation(view, GeometryReply{Answered: true, Proposals: chokeCells}, next.Relieved, nil)
	duties := map[domain.PawnID]CombatDuty{}
	for _, r := range roles {
		duties[r.Pawn] = r.Duty
	}
	if duties["e"] != DutyReserve || duties["g"] != DutyBlocker || duties["f"] != DutyBlocker || duties["d"] != DutyBlocker {
		t.Fatalf("%+v", duties)
	}
}

// Item 5: a blocking hold spends its proposal on the choke but names the
// cells around the line, and a rifleman takes one the game scored covered
// and standable.
func TestBlockingHoldNamesCoverAroundLine(t *testing.T) {
	view := chokeView()
	corner := domain.Cell{X: 11, Z: 24}
	reply := GeometryReply{Proposals: chokeCells, Scored: []ScoredCell{
		scored(9, 23, 0, 0), scored(8, 23, 0.40, 0), scored(10, 23, 0.48, 0), scored(11, 24, 0.9675, 0),
	}}
	orders, memory := answerWithout(t, view, reply)
	if memory.Tactic != TacticHold || len(orders) == 0 || orders[0].Pawn != "a" || orders[0].Cell != corner {
		t.Fatalf("%+v", orders)
	}
	// Unstandable, the corner is not used.
	orders, _ = answerWithout(t, view, reply, corner)
	for _, o := range orders {
		if o.Cell == corner {
			t.Fatalf("%+v", o)
		}
	}
}

// A firing cell native refused as unreachable is not given to anyone again:
// the next formation picks other cells instead of re-sending the same move.
func TestFormationSkipsUnreachableCells(t *testing.T) {
	view := chokeView()
	_, roles, _ := formation(view, GeometryReply{Answered: true, Proposals: chokeCells}, nil, nil)
	var bad domain.Cell
	for _, r := range roles {
		if r.Ranged && r.Cell != nil {
			bad = *r.Cell
			break
		}
	}
	if bad == (domain.Cell{}) {
		t.Skip("fixture forms no ranged cell")
	}
	_, roles, _ = formation(view, GeometryReply{Answered: true, Proposals: chokeCells}, nil, []domain.Cell{bad})
	for _, r := range roles {
		if r.Cell != nil && *r.Cell == bad {
			t.Fatalf("%s still posted on the unreachable cell %v", r.Pawn, bad)
		}
	}
	memory := CombatMemory{Roles: []CombatRole{{Pawn: "a", Cell: &bad, Ranged: true}}}.RefuseCell(bad)
	if !reform(view, StopEvent{}, memory) {
		t.Fatal("a role on an unreachable cell must re-form")
	}
}
