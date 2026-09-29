package buildingruntime

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Outdoor food fields fill the layout plan's field blocks at every tier
// (#1223, epic #1212): one growing zone per block, created by PickRect and
// grown with add-cells until the block is full before the next block opens.

// layoutFieldCells is the plan's field cells; false with no plan or no
// field zones.
func layoutFieldCells(facts observation.ColonyProjection) (map[domain.Cell]bool, bool) {
	plan, ok := facts.LayoutPlan.Value()
	if !ok {
		return nil, false
	}
	fields := plan.FieldCells()
	return fields, len(fields) > 0
}

// layoutFieldProtected protects every observed cell outside the plan's
// field zones, so outdoor fields grow only on the planned blocks.
func layoutFieldProtected(facts observation.ColonyProjection, protected []domain.Cell) []domain.Cell {
	fields, ok := layoutFieldCells(facts)
	if !ok {
		return protected
	}
	for _, c := range facts.Cells {
		if !fields[c.Cell] && len(protected) < protectedCellLimit {
			protected = append(protected, c.Cell)
		}
	}
	return protected
}

// fieldBlockEdit is the next outdoor field step: Zone empty creates a
// growing zone of Crop on Cells; otherwise Cells are added to Zone, which
// grows Crop.
type fieldBlockEdit struct {
	Zone, Crop string
	Cells      []domain.Cell
}

// planFieldBlock walks the plan's field blocks nearest anchor first and
// returns the first block's step that still has free soil for crop: grow
// the growing zone standing in the block, or create one when the block has
// none. want bounds the cells taken. reason explains a refusal: no field
// blocks, or every block full.
func planFieldBlock(facts observation.ColonyProjection, anchor domain.Cell, crop policy.CropChoice, want int, protected []domain.Cell) (fieldBlockEdit, string, bool) {
	plan, ok := facts.LayoutPlan.Value()
	if !ok {
		return fieldBlockEdit{}, "no layout plan", false
	}
	blocks := plan.FieldBlocks(anchor)
	if len(blocks) == 0 {
		return fieldBlockEdit{}, "layout plan has no field blocks", false
	}
	if want <= 0 {
		return fieldBlockEdit{}, "no field cells needed", false
	}
	blocked := make(map[domain.Cell]bool, len(protected))
	for _, c := range protected {
		blocked[c] = true
	}
	census := make(map[domain.Cell]policy.SiteCell, len(facts.Cells))
	for _, c := range facts.Cells {
		census[c.Cell] = c
	}
	growing := map[string]string{}
	for _, f := range facts.Farms {
		growing[f.ID] = f.Crop
	}
	floor, _ := crop.FertilityMin.Value()
	for _, block := range blocks {
		free := map[domain.Cell]bool{}
		zoneCells := map[string]map[domain.Cell]bool{}
		for cell := range block {
			c, seen := census[cell]
			if !seen {
				continue
			}
			if id, ok := c.ZoneID.Value(); ok && id != "" {
				if _, farm := growing[id]; farm {
					if zoneCells[id] == nil {
						zoneCells[id] = map[domain.Cell]bool{}
					}
					zoneCells[id][cell] = true
				}
				continue
			}
			if !blocked[cell] && freeFieldSoil(c, floor) {
				free[cell] = true
			}
		}
		if len(free) == 0 {
			continue
		}
		zone := ""
		for id, cells := range zoneCells {
			if zone == "" || len(cells) > len(zoneCells[zone]) || len(cells) == len(zoneCells[zone]) && id < zone {
				zone = id
			}
		}
		if zone == "" {
			return fieldBlockEdit{Crop: crop.Name, Cells: policy.PickRect(free, anchor, want)}, "", true
		}
		if adds := growZoneCells(zoneCells[zone], free, anchor, want); len(adds) > 0 {
			return fieldBlockEdit{Zone: zone, Crop: growing[zone], Cells: adds}, "", true
		}
	}
	return fieldBlockEdit{}, "every field block is full", false
}

// growZoneCells picks up to want free cells that extend zone as one
// contiguous rectangle-ish block nearest anchor; only cells connected to
// the zone through the result are kept, so native never splits the zone.
func growZoneCells(zone, free map[domain.Cell]bool, anchor domain.Cell, want int) []domain.Cell {
	union := make(map[domain.Cell]bool, len(zone)+len(free))
	for c := range zone {
		union[c] = true
	}
	for c := range free {
		union[c] = true
	}
	picked := map[domain.Cell]bool{}
	for _, c := range policy.PickRect(union, anchor, len(zone)+want) {
		picked[c] = true
	}
	for c := range zone {
		picked[c] = true
	}
	adds := connectedAdds(zone, picked, want)
	if len(adds) == 0 {
		// The picker's rectangle missed the zone: extend its frontier.
		adds = connectedAdds(zone, union, want)
	}
	return adds
}

// connectedAdds floods from zone through set and returns up to want
// non-zone cells reached, nearest the zone first, in row-major order.
func connectedAdds(zone, set map[domain.Cell]bool, want int) []domain.Cell {
	seen := map[domain.Cell]bool{}
	var frontier, out []domain.Cell
	for c := range zone {
		seen[c] = true
		frontier = append(frontier, c)
	}
	sortCells(frontier)
	for len(frontier) > 0 && len(out) < want {
		var next []domain.Cell
		for _, c := range frontier {
			for _, n := range []domain.Cell{{X: c.X, Z: c.Z - 1}, {X: c.X - 1, Z: c.Z}, {X: c.X + 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}} {
				if seen[n] || !set[n] {
					continue
				}
				seen[n] = true
				next = append(next, n)
			}
		}
		sortCells(next)
		for _, n := range next {
			if len(out) < want {
				out = append(out, n)
			}
		}
		frontier = next
	}
	sortCells(out)
	return out
}

func sortCells(cells []domain.Cell) {
	sort.Slice(cells, func(i, j int) bool {
		if cells[i].Z != cells[j].Z {
			return cells[i].Z < cells[j].Z
		}
		return cells[i].X < cells[j].X
	})
}

// freeFieldSoil is observed walkable, unoccupied, unzoned, unroofed soil at
// or above the crop's fertility floor.
func freeFieldSoil(c policy.SiteCell, floor float64) bool {
	walk, wk := c.Walkable.Value()
	occupied, ok := c.Occupied.Value()
	zone, zk := c.Zone.Value()
	roof, rk := c.Roofed.Value()
	soil, fk := c.Fertility.Value()
	return wk && walk && ok && !occupied && zk && !zone && rk && !roof && fk && soil > 0 && soil >= floor
}
