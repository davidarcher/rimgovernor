package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CoveredStorageRequest is the free-cell
// census for a 2x2 covered-storage patch: only roofed, walkable, unoccupied,
// unzoned cells with empty native storage may host the allow-list stockpile
// zone SecureSupplies falls back to once ordinary hauling has no destination.
type CoveredStorageRequest struct {
	Bounds    Bounds
	Anchor    domain.Cell
	Cells     []SiteCell
	Protected []domain.Cell
}

const coveredStorageSize int32 = 2

// CoveredStorageSites returns a bounded, deterministically ordered list of 2x2
// candidate rectangles nearest the colony anchor. It is a proposal only:
// native zone-create previews still decide legality, and only an admitted
// method reserves geometry. Missing or unknown cells are never treated as free.
func CoveredStorageSites(r CoveredStorageRequest) ([]Rectangle, error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 || len(r.Cells) > 65536 || len(r.Protected) > 65536 {
		return nil, errors.New("invalid covered storage site bounds")
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
	if !inBounds(r.Anchor) {
		return nil, errors.New("invalid covered storage anchor")
	}
	cells := make(map[domain.Cell]SiteCell, len(r.Cells))
	ordered := make([]domain.Cell, 0, len(r.Cells))
	for _, c := range r.Cells {
		if !inBounds(c.Cell) {
			return nil, errors.New("covered storage site cell out of bounds")
		}
		if _, exists := cells[c.Cell]; exists {
			return nil, errors.New("duplicate covered storage site cell")
		}
		cells[c.Cell] = c
		ordered = append(ordered, c.Cell)
	}
	sort.Slice(ordered, func(i, j int) bool { return cellLess(ordered[i], ordered[j]) })
	protected := map[domain.Cell]bool{}
	for _, c := range r.Protected {
		if !inBounds(c) {
			return nil, errors.New("protected covered storage cell out of bounds")
		}
		protected[c] = true
	}
	free := func(p domain.Cell) bool {
		c, exists := cells[p]
		if !exists || protected[p] {
			return false
		}
		return positive(c.Walkable) && positive(c.Roofed) && positive(c.StorageEmpty) &&
			positive(measured(c.Occupied, func(v bool) bool { return !v })) &&
			positive(measured(c.Zone, func(v bool) bool { return !v }))
	}
	type site struct {
		score int64
		cell  domain.Cell
	}
	var sites []site
	for _, c := range ordered {
		if c.X+coveredStorageSize > r.Bounds.Width || c.Z+coveredStorageSize > r.Bounds.Height {
			continue
		}
		legal := true
		for _, p := range rectCells(Rectangle{c.X, c.Z, coveredStorageSize, coveredStorageSize}) {
			if !free(p) {
				legal = false
				break
			}
		}
		if !legal {
			continue
		}
		score := squaredDistance(c, r.Anchor)
		sites = append(sites, site{score, c})
	}
	sort.Slice(sites, func(i, j int) bool {
		if sites[i].score != sites[j].score {
			return sites[i].score < sites[j].score
		}
		return cellLess(sites[i].cell, sites[j].cell)
	})
	if len(sites) > 8 {
		sites = sites[:8]
	}
	result := make([]Rectangle, len(sites))
	for i, s := range sites {
		result[i] = Rectangle{s.cell.X, s.cell.Z, coveredStorageSize, coveredStorageSize}
	}
	return result, nil
}
