package policy

import (
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// siteTravelWeight charges a picked cell per step from the anchor, as a
// fraction of one normal-soil cell's daily nutrition for the crop.
const siteTravelWeight = 0.015

// farmSitePatchLimit bounds a new greenhouse's lamps.
const farmSitePatchLimit = 32

// FarmSiteRequest is the cell census a site kind picks from (the
// square search is gone; every kind uses the rectangle picker).
type FarmSiteRequest struct {
	Bounds Bounds
	Anchor domain.Cell
	Cells  []SiteCell
	// Protected cells are never planted: accepted footprints,
	// walkways, entrances and reserved routes.
	Protected []domain.Cell
	// Fields, when non-nil, are the only cells an outdoor candidate
	// scores: the layout plan's field blocks.
	Fields map[domain.Cell]bool
}

type FarmSiteTerm struct {
	Name  string
	Value float64
}

// FarmSiteCandidate records why a patch scored as it did. Score is the sum of
// Terms; Density is Score per cell.
type FarmSiteCandidate struct {
	Patch          Rectangle
	Score, Density float64
	Terms          []FarmSiteTerm
}

type FarmSitePlan struct {
	Patches   []Rectangle
	Selected  []FarmSiteCandidate
	Cells     int
	Unplanted int
}

// Explain renders the selection for logs and acceptance evidence.
func (p FarmSitePlan) Explain() string {
	var b strings.Builder
	fmt.Fprintf(&b, "%d cells in %d patches (unplanted=%d)", p.Cells, len(p.Patches), p.Unplanted)
	for _, c := range p.Selected {
		fmt.Fprintf(&b, "\n  %dx%d@%d,%d score=%.4f density=%.4f", c.Patch.Width, c.Patch.Height, c.Patch.X, c.Patch.Z, c.Score, c.Density)
		for _, t := range c.Terms {
			fmt.Fprintf(&b, " %s=%.4f", t.Name, t.Value)
		}
	}
	return b.String()
}
