package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CoverLine is combat.geometry's cover for one (cell, hostile) pair (#851):
// the game's CoverUtility block chance at the cell against the hostile's
// shot, and the block chance the hostile keeps against a shot from the
// cell (#862). Straight past one sandbag is 0.40, diagonal past two 0.48,
// sandbag and wall at a right angle 0.83-0.9675; Go only compares them.
type CoverLine struct {
	Hostile      domain.PawnID
	Cover        float64
	HostileCover float64
	LineOfFire   bool
}

// ScoredCell is one candidate cell with its cover against each hostile.
type ScoredCell struct {
	Cell  domain.Cell
	Lines []CoverLine
}

// CoverScore is a firing cell's worth against the approach (#862): the
// mean cover the cell gets against the hostiles it can shoot, less the
// mean cover those hostiles keep against it (the enemy's diagonal cover
// near the approach). A cell with no line of fire scores on every hostile.
func CoverScore(s ScoredCell) float64 {
	lines := slices.DeleteFunc(slices.Clone(s.Lines), func(l CoverLine) bool { return !l.LineOfFire })
	if len(lines) == 0 {
		lines = s.Lines
	}
	if len(lines) == 0 {
		return 0
	}
	var own, theirs float64
	for _, l := range lines {
		own += l.Cover
		theirs += l.HostileCover
	}
	return (own - theirs) / float64(len(lines))
}

// RankByCover orders cells best cover score first. Cells the geometry did
// not score keep their order after the scored ones; ties keep input order,
// so without a geometry reply the order is unchanged.
func RankByCover(cells []domain.Cell, scored []ScoredCell) []domain.Cell {
	if len(scored) == 0 {
		return cells
	}
	score := make(map[domain.Cell]float64, len(scored))
	for _, s := range scored {
		score[s.Cell] = CoverScore(s)
	}
	out := slices.Clone(cells)
	slices.SortStableFunc(out, func(a, b domain.Cell) int {
		sa, oka := score[a]
		sb, okb := score[b]
		switch {
		case oka != okb:
			if oka {
				return -1
			}
			return 1
		case sa > sb:
			return -1
		case sa < sb:
			return 1
		}
		return 0
	})
	return out
}
