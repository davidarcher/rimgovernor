package startersite

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
)

// squareSite is the size x size south-door square nearest the colony
// centre whose ring stands on open lit ground, natural rock (reused as
// wall) or a ruin, whose interior is open lit ground or natural rock, and
// whose door and threshold are open ground. Each ring rock cell counts as
// six cells nearer and each interior rock cell eight further (#700).
func squareSite(facts observation.ColonyProjection, size int32) (domain.RoomFootprint, bool) {
	type state struct{ open, rock, ruin bool }
	yes := func(f domain.Fact[bool]) bool { v, k := f.Value(); return k && v }
	no := func(f domain.Fact[bool]) bool { v, k := f.Value(); return k && !v }
	cells := make(map[domain.Cell]state, len(facts.Cells))
	for _, c := range facts.Cells {
		if yes(c.Zone) {
			continue
		}
		cells[c.Cell] = state{
			open: no(c.Indoors) && no(c.Roofed) && yes(c.Walkable) && no(c.Occupied) && yes(c.SupportsLight),
			rock: yes(c.NaturalRock),
			ruin: yes(c.Ruin) && yes(c.SupportsLight),
		}
	}
	var best domain.RoomFootprint
	bestScore, found := int64(0), false
	for _, c := range facts.Cells {
		if c.Cell.X+size > facts.Bounds.Width || c.Cell.Z+size > facts.Bounds.Height {
			continue
		}
		shell, err := domain.RectangleFootprint(domain.RoomBounds{X: c.Cell.X, Z: c.Cell.Z, Width: size, Height: size}, domain.South)
		if err != nil {
			continue
		}
		b := shell.Bounds()
		dx, dz := int64(b.X+b.Width/2-facts.Center.X), int64(b.Z+b.Height/2-facts.Center.Z)
		score, ok := dx*dx+dz*dz, cells[shell.Door()].open
		for _, p := range shell.Walls() {
			s := cells[p]
			switch {
			case p == shell.Door():
			case s.rock:
				score -= 6
			case !s.open && !s.ruin:
				ok = false
			}
		}
		for _, p := range shell.Interior() {
			if s := cells[p]; s.rock {
				score += 8
			} else if !s.open {
				ok = false
			}
		}
		if t, observed := cells[shell.Threshold()]; observed && !t.open {
			ok = false
		}
		if ok && (!found || score < bestScore) {
			best, bestScore, found = shell, score, true
		}
	}
	return best, found
}
