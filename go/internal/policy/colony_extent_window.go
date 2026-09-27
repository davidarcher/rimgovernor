package policy

import (
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
