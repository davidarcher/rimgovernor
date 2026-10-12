package combatsim

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// angleDifference ports GenGeo.AngleDifferenceBetween: the smallest of the
// plain difference and the two wrapped by 360.
func angleDifference(a, b float64) float64 {
	return min(math.Abs(a-b), math.Abs(a+360-b), math.Abs(a-(b+360)))
}

func cardinalAdjacent(a, b domain.Cell) bool {
	return (a.Z == b.Z && abs32(a.X-b.X) == 1) || (a.X == b.X && abs32(a.Z-b.Z) == 1)
}

// adjustedCover ports CoverUtility.TryFindAdjustedCoverInCell: the block
// chance the thing on adj gives a target on cell against a shooter on
// shooter, or 0 when adj has no cover or sits too far off the firing line.
func (g *grid) adjustedCover(shooter, cell, adj domain.Cell) float64 {
	if !g.hasCover(adj) || shooter == cell {
		return 0
	}
	diff := angleDifference(
		angleFlat(domain.Cell{X: adj.X - cell.X, Z: adj.Z - cell.Z}),
		angleFlat(domain.Cell{X: shooter.X - cell.X, Z: shooter.Z - cell.Z}))
	if !cardinalAdjacent(cell, adj) {
		diff *= 1.75
	}
	chance := g.baseBlockChance(adj)
	switch {
	case diff < 15:
	case diff < 27:
		chance *= 0.8
	case diff < 40:
		chance *= 0.6
	case diff < 52:
		chance *= 0.4
	case diff < 65:
		chance *= 0.2
	default:
		return 0
	}
	dx, dz := float64(shooter.X-adj.X), float64(shooter.Z-adj.Z)
	switch dist := math.Sqrt(dx*dx + dz*dz); {
	case dist < 1.9:
		chance *= 0.3333
	case dist < 2.9:
		chance *= 0.66666
	}
	return chance
}

// CoverBlockChance ports CoverUtility.CalculateOverallBlockChance for a pawn
// standing on target shot from shooter: the chance the eight neighbours'
// cover, each weighed by its angle off the firing line and its distance from
// the shooter, blocks the shot. canBenefitFromCover is the target race's
// CanBenefitFromCover flag.
func (s *Sim) CoverBlockChance(target, shooter domain.Cell, canBenefitFromCover bool) float64 {
	if !canBenefitFromCover {
		return 0
	}
	g := &s.g
	total := 0.0
	for _, off := range adjacent {
		adj := add(target, off)
		if !g.inBounds(adj) || adj == shooter {
			continue
		}
		total += (1 - total) * g.adjustedCover(shooter, target, adj)
	}
	return total
}

// coverGivers ports CoverUtility.CalculateCoverGiverSet for a pawn on target
// shot from shooter: the eight neighbours whose cover has an adjusted block
// chance above 0. Unlike the overall chance, a neighbour on the shooter's own
// cell is not skipped.
func (s *Sim) coverGivers(target, shooter domain.Cell, out []coverGiver) []coverGiver {
	for _, off := range adjacent {
		adj := add(target, off)
		if !s.g.inBounds(adj) {
			continue
		}
		if c := s.g.adjustedCover(shooter, target, adj); c > 0 {
			out = append(out, coverGiver{cell: adj, chance: c})
		}
	}
	return out
}
