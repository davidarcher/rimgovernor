package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// fullFillAbove is ThingDef.Fillage's boundary: a def with fillPercent above
// it is FillCategory.Full, which blocks sight (GenGrid.CanBeSeenOver).
const fullFillAbove = 0.99

// blocksSight reports whether the cell stops a line of sight, as
// GenGrid.CanBeSeenOver reads it: an edifice of Full fillage, an open door
// excepted. The census cover fill is the largest fillPercent of any thing on
// the cell (the game's cover grid), so an edifice with fill above
// fullFillAbove shows as a cover fill above it. A door is read closed: the
// census does not say whether one stands open, and doors are open only while
// someone walks through. Unknown for a cell the census lacks or fogs.
func (s defenseSite) blocksSight(c domain.Cell) domain.Fact[bool] {
	row, ok := s.cells[c]
	if !ok {
		return domain.Unknown[bool]()
	}
	if _, known := row.Passable.Value(); !known {
		return domain.Unknown[bool]()
	}
	fill, known := row.CoverFill.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(row.Edifice != "" && fill > fullFillAbove)
}

// lineOfSight is GenSight.LineOfSight(from, to, map, skipFirstCell: true): the
// game's integer line walk from the shooter's cell to the target's, which
// stops short of the target and does not test the shooter's own cell. Sight
// is blocked when any walked cell blocks it; otherwise it is clear only if
// every walked cell and both endpoints are known: the game answers nothing for
// a fogged endpoint, and an unknown cell on the line is no free air.
func (s defenseSite) lineOfSight(from, to domain.Cell) domain.Fact[bool] {
	b := s.r.Bounds
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < b.Width && c.Z < b.Height }
	if !inBounds(from) || !inBounds(to) {
		return domain.Known(false)
	}
	unknown := false
	for _, c := range []domain.Cell{from, to} {
		if _, known := s.blocksSight(c).Value(); !known {
			unknown = true
		}
	}
	sideOnEqual := from.X < to.X
	if from.X == to.X {
		sideOnEqual = from.Z < to.Z
	}
	dx, dz := abs32(to.X-from.X), abs32(to.Z-from.Z)
	x, z := from.X, from.Z
	stepX, stepZ := int32(-1), int32(-1)
	if to.X > from.X {
		stepX = 1
	}
	if to.Z > from.Z {
		stepZ = 1
	}
	// The game scales the error terms by four; only their signs matter.
	err := dx - dz
	dx, dz = dx*2, dz*2
	for n := 1 + abs32(to.X-from.X) + abs32(to.Z-from.Z); n > 1; n-- {
		if c := (domain.Cell{X: x, Z: z}); c != from {
			blocked, known := s.blocksSight(c).Value()
			switch {
			case !known:
				unknown = true
			case blocked:
				return domain.Known(false)
			}
		}
		if err > 0 || err == 0 && sideOnEqual {
			x += stepX
			err -= dz
		} else {
			z += stepZ
			err += dx
		}
	}
	if unknown {
		return domain.Unknown[bool]()
	}
	return domain.Known(true)
}

// LineOfSight is the game's line of sight between two cells of the request's
// census, for tests and callers that hold only a request.
func (r DefenseRequest) LineOfSight(from, to domain.Cell) (domain.Fact[bool], error) {
	s, err := newDefenseSite(r)
	if err != nil {
		return domain.Unknown[bool](), err
	}
	return s.lineOfSight(from, to), nil
}
