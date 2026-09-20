package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// ColonyGrid is the shared layout geometry every site search snaps to once
// the colony builds in stone (#605): an origin cell, a pitch, and the two
// axis directions. This file carries the aisle geometry the protected-cell
// path consumes (#606); #605 fills in derivation, persistence and the
// remaining snap helpers.
//
// Every module is Pitch cells on each axis measured from Origin: walls at
// offsets 0 and 12, an 11x11 interior at 1..11, and a three-wide aisle at
// 13, 14 and 15 that separates it from the next module. Aisles run along
// both axes, so the grid's walkways form a lattice of corridors.
type ColonyGrid struct {
	Origin domain.Cell
	Pitch  int32
	Axes   [2]domain.Cell
}

// GridPitch is the module period at every tier: an 11x11 interior, its two
// walls and a three-wide aisle.
const GridPitch int32 = 16

// gridAisleWidth is how many cells of each period are walkway.
const gridAisleWidth int32 = 3

// Aisles lists every walkway cell of the grid inside bounds, x-major then
// z. A grid without a positive pitch has no aisles.
func (g ColonyGrid) Aisles(bounds Bounds) []domain.Cell {
	if g.Pitch <= 0 || bounds.Width <= 0 || bounds.Height <= 0 {
		return nil
	}
	var aisles []domain.Cell
	for x := int32(0); x < bounds.Width; x++ {
		onX := g.aisleOffset(x - g.Origin.X)
		for z := int32(0); z < bounds.Height; z++ {
			if onX || g.aisleOffset(z-g.Origin.Z) {
				aisles = append(aisles, domain.Cell{X: x, Z: z})
			}
		}
	}
	return aisles
}

// AislesWithin lists the grid's walkway cells inside region, clipped to
// bounds.
func (g ColonyGrid) AislesWithin(bounds Bounds, region Rectangle) []domain.Cell {
	if g.Pitch <= 0 || region.Width <= 0 || region.Height <= 0 {
		return nil
	}
	var aisles []domain.Cell
	for x := max32(region.X, 0); x < region.X+region.Width && x < bounds.Width; x++ {
		onX := g.aisleOffset(x - g.Origin.X)
		for z := max32(region.Z, 0); z < region.Z+region.Height && z < bounds.Height; z++ {
			if onX || g.aisleOffset(z-g.Origin.Z) {
				aisles = append(aisles, domain.Cell{X: x, Z: z})
			}
		}
	}
	return aisles
}

// aisleOffset reports whether an axis offset from the origin falls in the
// aisle band of its period.
func (g ColonyGrid) aisleOffset(delta int32) bool {
	offset := delta % g.Pitch
	if offset < 0 {
		offset += g.Pitch
	}
	return offset >= g.Pitch-gridAisleWidth
}

func max32(a, b int32) int32 {
	if a > b {
		return a
	}
	return b
}
