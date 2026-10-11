package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A crop that grows in the dark needs a cell the sky never lights: roofed,
// with no lamp. An open cell is not dark between dawn and dusk, so it is
// refused whichever hour the cells were read.
func TestDarkCropNeedsARoofedCellWithoutALamp(t *testing.T) {
	fungus := CropChoice{MinGlow: domain.Known(0.0)}
	cell := func(roofed domain.Fact[bool], glow domain.Fact[float64]) SiteCell {
		return SiteCell{Roofed: roofed, Glow: glow}
	}
	for name, tc := range map[string]struct {
		cell SiteCell
		want bool
	}{
		"roofed, no lamp": {cell(domain.Known(true), domain.Known(0.0)), true},
		"open, no lamp":   {cell(domain.Known(false), domain.Known(0.0)), false},
		"roofed, lamp":    {cell(domain.Known(true), domain.Known(0.5)), false},
		"roof unknown":    {cell(domain.Unknown[bool](), domain.Known(0.0)), false},
		"glow unknown":    {cell(domain.Known(true), domain.Unknown[float64]()), false},
	} {
		if got := cropSoilCompatible(fungus, tc.cell); got != tc.want {
			t.Errorf("%s: compatible = %v, want %v", name, got, tc.want)
		}
	}
}
