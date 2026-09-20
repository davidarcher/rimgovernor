package policy

import (
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ExtentWindowSource names what an ExtentWindow was anchored on, for the
// consumer's log.
type ExtentWindowSource string

const (
	// ExtentWindowExtent: the window is centred on the shared extent.
	ExtentWindowExtent ExtentWindowSource = "extent"
	// ExtentWindowFocus: the extent is unknown or empty, so the window is
	// centred on the consumer's own focus cell, its previous derivation.
	ExtentWindowFocus ExtentWindowSource = "focus"
)

// ExtentWindowRequest is the bounded candidate-area query a layout consumer
// makes of the shared extent. Extent is the current derivation or the
// established history; Areas are explicitly selected expansion areas and
// join the same bounding box. Focus is a cell the window must contain (a
// planner's Home cell); Half is the window's Chebyshev half-width, so the
// window is at most (2*Half+1) square before clipping to Bounds.
type ExtentWindowRequest struct {
	Extent domain.Fact[ColonyExtent]
	Areas  [][]domain.Cell
	Focus  domain.Cell
	Bounds Bounds
	Half   int32
}

// ExtentWindow returns the square of half-width Half centred on the extent's
// bounding-box centre, shifted only as far as keeps Focus inside, then
// clipped to the map. An unknown or empty extent (and no expansion areas)
// centres the window on Focus instead, so a consumer never reads territory
// the colony does not have. The window is read-only geometry: it selects
// which cells a consumer reads, grants no eligibility and paints no Home.
func ExtentWindow(r ExtentWindowRequest) (Rectangle, ExtentWindowSource, error) {
	b := r.Bounds
	if b.Width <= 0 || b.Height <= 0 || b.Width > 4096 || b.Height > 4096 {
		return Rectangle{}, "", errors.New("invalid extent window bounds")
	}
	if r.Half < 0 || r.Half > 2048 {
		return Rectangle{}, "", errors.New("invalid extent window half-width")
	}
	if r.Focus.X < 0 || r.Focus.Z < 0 || r.Focus.X >= b.Width || r.Focus.Z >= b.Height {
		return Rectangle{}, "", errors.New("extent window focus outside bounds")
	}
	var lo, hi domain.Cell
	seen := false
	span := func(c domain.Cell) {
		if !seen {
			lo, hi, seen = c, c, true
			return
		}
		lo.X, lo.Z = min(lo.X, c.X), min(lo.Z, c.Z)
		hi.X, hi.Z = max(hi.X, c.X), max(hi.Z, c.Z)
	}
	if e, known := r.Extent.Value(); known {
		for _, region := range e.Regions {
			for _, row := range region.Cells {
				span(row.Cell)
			}
		}
	}
	for _, area := range r.Areas {
		for _, c := range area {
			span(c)
		}
	}
	centre, source := r.Focus, ExtentWindowFocus
	if seen {
		// Truncation toward the low corner keeps the anchor deterministic;
		// the window may then be shifted, never widened, to hold Focus.
		centre = domain.Cell{X: lo.X + (hi.X-lo.X)/2, Z: lo.Z + (hi.Z-lo.Z)/2}
		centre.X = clampWindow(centre.X, r.Focus.X-r.Half, r.Focus.X+r.Half)
		centre.Z = clampWindow(centre.Z, r.Focus.Z-r.Half, r.Focus.Z+r.Half)
		source = ExtentWindowExtent
	}
	minX := clampWindow(centre.X-r.Half, 0, b.Width-1)
	minZ := clampWindow(centre.Z-r.Half, 0, b.Height-1)
	maxX := clampWindow(centre.X+r.Half, 0, b.Width-1)
	maxZ := clampWindow(centre.Z+r.Half, 0, b.Height-1)
	return Rectangle{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1}, source, nil
}

func clampWindow(v, lo, hi int32) int32 {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}
