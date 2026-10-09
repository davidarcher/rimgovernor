package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Cover values below are the game's CoverUtility numbers as combat.geometry
// reports them: straight past one sandbag 0.40, diagonal past two
// 0.48, sandbag and wall at a right angle 0.83-0.9675.

func scored(x, z int32, cover, hostileCover float64) ScoredCell {
	return ScoredCell{Cell: domain.Cell{X: x, Z: z}, Lines: []CoverLine{
		{Hostile: "r1", Cover: cover, HostileCover: hostileCover, LineOfFire: true},
		{Hostile: "r2", Cover: cover, HostileCover: hostileCover, LineOfFire: true},
	}}
}

func TestCoverAngleScoresStraightPastOne(t *testing.T) {
	open, straight := scored(1, 1, 0, 0), scored(2, 1, 0.40, 0)
	if CoverScore(straight) <= CoverScore(open) {
		t.Fatalf("straight past one %.2f <= open %.2f", CoverScore(straight), CoverScore(open))
	}
	cells := []domain.Cell{open.Cell, straight.Cell}
	if got := RankByCover(cells, []ScoredCell{open, straight}); got[0] != straight.Cell {
		t.Fatalf("%v", got)
	}
}

func TestCoverAngleCornerBeatsSingleSandbag(t *testing.T) {
	single, diagonal, corner := scored(1, 1, 0.40, 0), scored(2, 1, 0.48, 0), scored(3, 1, 0.9675, 0)
	got := RankByCover([]domain.Cell{single.Cell, diagonal.Cell, corner.Cell}, []ScoredCell{single, diagonal, corner})
	if want := []domain.Cell{corner.Cell, diagonal.Cell, single.Cell}; !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v want %v", got, want)
	}
}

// Two cells with the same own cover: the one that leaves the enemy its
// diagonal cover near the approach ranks last.
func TestCoverAngleDeniesEnemyDiagonal(t *testing.T) {
	gives, denies := scored(1, 1, 0.40, 0.48), scored(2, 1, 0.40, 0)
	got := RankByCover([]domain.Cell{gives.Cell, denies.Cell}, []ScoredCell{gives, denies})
	if got[0] != denies.Cell {
		t.Fatalf("%v", got)
	}
	// Lines without a line of fire do not count while any line has one.
	blind := ScoredCell{Cell: denies.Cell, Lines: []CoverLine{{Cover: 0.40, LineOfFire: true}, {Cover: 0, HostileCover: 0.9, LineOfFire: false}}}
	if CoverScore(blind) != 0.40 {
		t.Fatalf("%.2f", CoverScore(blind))
	}
}

// Formation sends the riflemen to the best-scored cells: with room for
// three among four candidates, the uncovered line cell is left empty and
// the corner takes the first rifleman.
func TestDecideCombatPicksBestCoverCells(t *testing.T) {
	view := holdView()
	reply := GeometryReply{Answered: true, Proposals: []domain.Cell{{X: 11, Z: 24}}, Scored: []ScoredCell{
		scored(9, 23, 0, 0), scored(8, 23, 0.40, 0), scored(10, 23, 0.48, 0), scored(11, 24, 0.9675, 0),
	}}
	orders, _, memory := DecideCombat(view, reply, StopEvent{}, CombatMemory{})
	want := []CombatOrder{
		{Pawn: "a", Kind: OrderMove, Cell: domain.Cell{X: 11, Z: 24}, Reason: ReasonFormation},
		// Spacing outranks cover: (10,23) is a tile from the corner,
		// so the spaced (8,23) comes before it.
		{Pawn: "b", Kind: OrderMove, Cell: domain.Cell{X: 8, Z: 23}, Reason: ReasonFormation},
		{Pawn: "c", Kind: OrderMove, Cell: domain.Cell{X: 10, Z: 23}, Reason: ReasonFormation},
	}
	if memory.Tactic != TacticHold || !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v %+v", orders, memory)
	}
	// Without scores the layout's order stands.
	orders, _, _ = DecideCombat(view, GeometryReply{Answered: true}, StopEvent{}, CombatMemory{})
	if orders[0].Cell != (domain.Cell{X: 9, Z: 23}) {
		t.Fatalf("%+v", orders)
	}
}
