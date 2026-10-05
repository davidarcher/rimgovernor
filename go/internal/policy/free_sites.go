package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FreeSiteRequest is FreeSites' free-cell census:
// only walkable, unoccupied, unzoned, light-supporting cells outside any
// protected footprint may host a footprint (FreeSites).
type FreeSiteRequest struct {
	Bounds    Bounds
	Anchor    domain.Cell
	Cells     []SiteCell
	Protected []domain.Cell
}

// FreeSites returns a bounded, deterministically ordered list of width x height
// candidate rectangles nearest the colony anchor (a plain grave's 1x2, #857).
// It is a proposal only: native placement previews still decide legality.
// Missing or unknown cells are never treated as free.
func FreeSites(r FreeSiteRequest, width, height int32) ([]Rectangle, error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 {
		return nil, errors.New("invalid free site bounds")
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
	if !inBounds(r.Anchor) {
		return nil, errors.New("invalid free site anchor")
	}
	cells := make(map[domain.Cell]SiteCell, len(r.Cells))
	ordered := make([]domain.Cell, 0, len(r.Cells))
	for _, c := range r.Cells {
		if !inBounds(c.Cell) {
			return nil, errors.New("free site cell out of bounds")
		}
		if _, exists := cells[c.Cell]; exists {
			return nil, errors.New("duplicate free site cell")
		}
		cells[c.Cell] = c
		ordered = append(ordered, c.Cell)
	}
	sort.Slice(ordered, func(i, j int) bool { return cellLess(ordered[i], ordered[j]) })
	protected := map[domain.Cell]bool{}
	for _, c := range r.Protected {
		if !inBounds(c) {
			return nil, errors.New("protected free site cell out of bounds")
		}
		protected[c] = true
	}
	free := func(p domain.Cell) bool {
		c, exists := cells[p]
		if !exists || protected[p] {
			return false
		}
		return positive(c.Walkable) && positive(measured(c.Occupied, func(v bool) bool { return !v })) &&
			positive(measured(c.Zone, func(v bool) bool { return !v })) && positive(c.SupportsLight)
	}
	type site struct {
		score int64
		cell  domain.Cell
	}
	var sites []site
	for _, c := range ordered {
		if c.X+width > r.Bounds.Width || c.Z+height > r.Bounds.Height {
			continue
		}
		legal := true
		for _, p := range rectCells(Rectangle{c.X, c.Z, width, height}) {
			if !free(p) {
				legal = false
				break
			}
		}
		if !legal {
			continue
		}
		score := squaredDistance(domain.Cell{X: c.X + width/2, Z: c.Z + height/2}, r.Anchor)
		sites = append(sites, site{score, c})
	}
	sort.Slice(sites, func(i, j int) bool {
		if sites[i].score != sites[j].score {
			return sites[i].score < sites[j].score
		}
		return cellLess(sites[i].cell, sites[j].cell)
	})
	if len(sites) > 24 {
		sites = sites[:24]
	}
	result := make([]Rectangle, len(sites))
	for i, s := range sites {
		result[i] = Rectangle{s.cell.X, s.cell.Z, width, height}
	}
	return result, nil
}
