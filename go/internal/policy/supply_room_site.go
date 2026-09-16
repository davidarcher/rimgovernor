package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SupplyRoomEnclosureRequest ports upkeep_sites.enclosure_site's free-cell
// census for SecureSupplies' supply_storeroom fallback: a fixed 6x6 room
// shell (wall='Wall', door='Door', empty_interior=True) built only once
// CoveredStorageSites finds no reusable roofed patch. Perimeter cells must be
// walkable, unoccupied, unzoned and light-supporting like the animal pen
// enclosure; the 4x4 interior additionally must observe empty native storage,
// so the room is not spent on an interior that cannot accept its guarded
// stockpile.
type SupplyRoomEnclosureRequest struct {
	Bounds    Bounds
	Anchor    domain.Cell
	Cells     []SiteCell
	Protected []domain.Cell
}

const supplyRoomEnclosureSize int32 = 6

// SupplyRoomEnclosureSites returns a bounded, deterministically ordered list
// of 6x6 candidate rectangles nearest the colony anchor. It is a proposal
// only: native placement previews still decide legality, and only an
// admitted method reserves geometry. Missing or unknown cells are never
// treated as free or empty.
func SupplyRoomEnclosureSites(r SupplyRoomEnclosureRequest) ([]Rectangle, error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 || len(r.Cells) > 65536 || len(r.Protected) > 65536 {
		return nil, errors.New("invalid supply room enclosure site bounds")
	}
	inBounds := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < r.Bounds.Width && c.Z < r.Bounds.Height }
	if !inBounds(r.Anchor) {
		return nil, errors.New("invalid supply room enclosure anchor")
	}
	cells := make(map[domain.Cell]SiteCell, len(r.Cells))
	ordered := make([]domain.Cell, 0, len(r.Cells))
	for _, c := range r.Cells {
		if !inBounds(c.Cell) {
			return nil, errors.New("supply room enclosure site cell out of bounds")
		}
		if _, exists := cells[c.Cell]; exists {
			return nil, errors.New("duplicate supply room enclosure site cell")
		}
		cells[c.Cell] = c
		ordered = append(ordered, c.Cell)
	}
	sort.Slice(ordered, func(i, j int) bool { return cellLess(ordered[i], ordered[j]) })
	protected := map[domain.Cell]bool{}
	for _, c := range r.Protected {
		if !inBounds(c) {
			return nil, errors.New("protected supply room enclosure cell out of bounds")
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
	empty := func(p domain.Cell) bool {
		c, exists := cells[p]
		return exists && positive(c.StorageEmpty)
	}
	type site struct {
		score int64
		cell  domain.Cell
	}
	var sites []site
	for _, c := range ordered {
		if c.X+supplyRoomEnclosureSize > r.Bounds.Width || c.Z+supplyRoomEnclosureSize > r.Bounds.Height {
			continue
		}
		legal := true
		for _, p := range rectCells(Rectangle{c.X, c.Z, supplyRoomEnclosureSize, supplyRoomEnclosureSize}) {
			if !free(p) {
				legal = false
				break
			}
		}
		if legal {
		interior:
			for x := c.X + 1; x < c.X+supplyRoomEnclosureSize-1; x++ {
				for z := c.Z + 1; z < c.Z+supplyRoomEnclosureSize-1; z++ {
					if !empty(domain.Cell{X: x, Z: z}) {
						legal = false
						break interior
					}
				}
			}
		}
		if !legal {
			continue
		}
		half := supplyRoomEnclosureSize / 2
		score := squaredDistance(domain.Cell{X: c.X + half, Z: c.Z + half}, r.Anchor)
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
		result[i] = Rectangle{s.cell.X, s.cell.Z, supplyRoomEnclosureSize, supplyRoomEnclosureSize}
	}
	return result, nil
}
