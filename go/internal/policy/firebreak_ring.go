package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FirebreakRing is the firebreak band's geometry alone: every in-bounds cell
// within FirebreakWidth of the base footprint (the home base of the colony
// extent with no margin, plus GrowingZones) and outside it, whatever its ground or treatment. It reads
// only Bounds, Construction, Claims, Home and GrowingZones; field siting
// protects these cells so new fields never take the ring.
func FirebreakRing(r FirebreakRequest) (domain.Fact[[]domain.Cell], error) {
	unknown := domain.Unknown[[]domain.Cell]()
	bounds, bk := r.Bounds.Value()
	zones, zk := r.GrowingZones.Value()
	if !bk || !zk {
		return unknown, nil
	}
	extent, err := DeriveColonyExtent(ColonyExtentRequest{Bounds: r.Bounds, Construction: r.Construction, Claims: r.Claims, Home: r.Home})
	if err != nil {
		return unknown, err
	}
	value, known := extent.Value()
	if !known {
		return unknown, nil
	}
	footprint := homeBase(value)
	for _, c := range zones {
		footprint[c] = true
	}
	band := map[domain.Cell]bool{}
	for c := range footprint {
		for dx := -FirebreakWidth; dx <= FirebreakWidth; dx++ {
			for dz := -FirebreakWidth; dz <= FirebreakWidth; dz++ {
				n := domain.Cell{X: c.X + dx, Z: c.Z + dz}
				if n.X >= 0 && n.Z >= 0 && n.X < bounds.Width && n.Z < bounds.Height && !footprint[n] {
					band[n] = true
				}
			}
		}
	}
	cells := make([]domain.Cell, 0, len(band))
	for c := range band {
		cells = append(cells, c)
	}
	sort.Slice(cells, func(i, j int) bool { return extentCellLess(cells[i], cells[j]) })
	return domain.Known(cells), nil
}
