package policy

import (
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PollutionSiteRequest asks which candidate footprints best host a building
// that pollutes or produces wastepacks (a PlanningDefinition with Pollutes
// known true: CompToxifier, CompPolluteOverTime, CompWasteProducer).
// The caller owns candidate legality (free, buildable ground); this rule only
// ranks them. Cells are map cells inside Bounds; an empty avoid set means the
// colony has none of it yet.
type PollutionSiteRequest struct {
	Bounds     Bounds
	Candidates []Rectangle
	// Avoid sets: the footprint stays far from each. Field zone cells,
	// bedroom cells and living-room cells (room cells whose role the caller
	// read natively) and cells the pollution grid marks polluted.
	FieldCells, BedroomCells, LivingCells, PollutedCells []domain.Cell
	// Disposal is where wastepacks leave the map: wastepack atomizer cells.
	// The footprint stays near it; empty means the colony has no disposal and
	// the disposal term does not rank.
	Disposal []domain.Cell
}

// PollutionSite is one ranked candidate. ClearanceSquared is the squared
// distance in cells from the footprint to the nearest avoided cell (the
// smallest across the four avoid sets; -1 when every set is empty).
// DisposalSquared is the squared distance to the nearest disposal cell (-1
// without disposal).
type PollutionSite struct {
	Site             Rectangle
	ClearanceSquared int64
	DisposalSquared  int64
}

// PollutionSites ranks the candidates: farthest from fields, bedrooms, living
// rooms and polluted cells first, then nearest wastepack disposal, then by
// cell order. It is a pure ranking over distance only: no wind term (no
// sourced rule), no reservation, no threshold, so a goal takes the first
// candidate its native placement preview accepts. Bad input fails loudly.
func PollutionSites(r PollutionSiteRequest) ([]PollutionSite, error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 {
		return nil, errors.New("invalid pollution site bounds")
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
	for name, set := range map[string][]domain.Cell{"field": r.FieldCells, "bedroom": r.BedroomCells, "living": r.LivingCells, "polluted": r.PollutedCells, "disposal": r.Disposal} {
		for _, c := range set {
			if !inBounds(c) {
				return nil, fmt.Errorf("pollution site %s cell out of bounds", name)
			}
		}
	}
	avoid := make([]domain.Cell, 0, len(r.FieldCells)+len(r.BedroomCells)+len(r.LivingCells)+len(r.PollutedCells))
	avoid = append(append(append(append(avoid, r.FieldCells...), r.BedroomCells...), r.LivingCells...), r.PollutedCells...)
	sites := make([]PollutionSite, 0, len(r.Candidates))
	for _, c := range r.Candidates {
		if c.Width <= 0 || c.Height <= 0 || c.X < 0 || c.Z < 0 || c.X+c.Width > r.Bounds.Width || c.Z+c.Height > r.Bounds.Height {
			return nil, errors.New("pollution site candidate out of bounds")
		}
		cells := rectCells(c)
		sites = append(sites, PollutionSite{Site: c, ClearanceSquared: nearestSquared(cells, avoid), DisposalSquared: nearestSquared(cells, r.Disposal)})
	}
	sort.SliceStable(sites, func(i, j int) bool {
		a, b := sites[i], sites[j]
		if a.ClearanceSquared != b.ClearanceSquared {
			return a.ClearanceSquared > b.ClearanceSquared
		}
		if a.DisposalSquared != b.DisposalSquared {
			return a.DisposalSquared < b.DisposalSquared
		}
		return cellLess(domain.Cell{X: a.Site.X, Z: a.Site.Z}, domain.Cell{X: b.Site.X, Z: b.Site.Z})
	})
	return sites, nil
}

// nearestSquared is the smallest squared distance from any cell to any
// target, -1 when there is no target. A target inside the footprint is 0.
func nearestSquared(cells, targets []domain.Cell) int64 {
	best := int64(-1)
	for _, t := range targets {
		for _, c := range cells {
			if d := squaredDistance(c, t); best < 0 || d < best {
				best = d
			}
		}
	}
	return best
}
