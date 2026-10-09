package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CoverLine is combat.geometry's cover for one (cell, hostile) pair:
// the game's CoverUtility block chance at the cell against the hostile's
// shot, and the block chance the hostile keeps against a shot from the
// cell. Straight past one sandbag is 0.40, diagonal past two 0.48,
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

// CoverScore is a firing cell's worth against the approach: the
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

// aroundLine are the in-bounds cells within one step (8-way) of the line,
// not on it: the cells cover_behind_line considers, named instead when the
// stop's proposal goes to the choke.
func aroundLine(line []domain.Cell) []domain.Cell {
	var out []domain.Cell
	for _, l := range line {
		for dx := int32(-1); dx <= 1; dx++ {
			for dz := int32(-1); dz <= 1; dz++ {
				c := domain.Cell{X: l.X + dx, Z: l.Z + dz}
				if c.X >= 0 && c.Z >= 0 && !slices.Contains(line, c) && !slices.Contains(out, c) {
					out = append(out, c)
				}
			}
		}
	}
	return out
}

// coveredAround are the cells around the line that the game reported
// standable with some cover against a hostile: the cover_behind_line rule
// applied to the game's own scores.
func coveredAround(line []domain.Cell, geometry GeometryReply) []domain.Cell {
	covered := map[domain.Cell]bool{}
	for _, s := range geometry.Scored {
		for _, l := range s.Lines {
			if l.Cover > 0 {
				covered[s.Cell] = true
			}
		}
	}
	var out []domain.Cell
	for _, c := range aroundLine(line) {
		if covered[c] && geometry.stands(c) {
			out = append(out, c)
		}
	}
	return out
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
