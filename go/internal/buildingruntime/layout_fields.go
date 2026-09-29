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

// fieldBlockEdit is the next outdoor field step: Zone empty creates a
// growing zone of Crop on Cells; otherwise Cells are added to Zone, which
// grows Crop.
type fieldBlockEdit struct {
	Zone, Crop string
	Cells      []domain.Cell
}

// planFieldBlock walks the plan's field blocks nearest anchor first and
// returns the first block's step that still has free soil: grow the
// growing zone standing in the block that options[0] adopts (with the first option's fertility
// floor and need), or create one when the block has none. A new block
// takes the first option, in policy.BlockCropOrder, not growing within
// FieldBlockNeighbourRadius of the block (#1225). options are the viable
// crops in preference order. reason explains a refusal: no field blocks,
// or every block full.
func planFieldBlock(facts observation.ColonyProjection, anchor domain.Cell, options []policy.FieldBlockOption, protected []domain.Cell) (fieldBlockEdit, string, bool) {
	plan, ok := facts.LayoutPlan.Value()
	if !ok {
		return fieldBlockEdit{}, "no layout plan", false
	}
	blocks := plan.FieldBlocks(anchor)
	if len(blocks) == 0 {
		return fieldBlockEdit{}, "layout plan has no field blocks", false
	}
	if len(options) == 0 || options[0].Needed <= 0 {
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
	inedible := map[string]bool{}
	for _, d := range facts.Definitions {
		if e, k := d.Edible.Value(); k && !e {
			inedible[d.Name] = true
		}
	}
	freeFor := func(block map[domain.Cell]bool, crop policy.CropChoice) map[domain.Cell]bool {
		floor, _ := crop.FertilityMin.Value()
		free := map[domain.Cell]bool{}
		for cell := range block {
			if c, seen := census[cell]; seen && !blocked[cell] && freeFieldSoil(c, floor) {
				free[cell] = true
			}
		}
		return free
	}
	for _, block := range blocks {
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
			}
		}
		// One zone per block: a block holding only zones options[0] does not
		// adopt belongs to another family (#1226).
		zone, taken := "", false
		for id, cells := range zoneCells {
			if !adoptsZone(options[0].Crop, growing[id], inedible) {
				taken = true
				continue
			}
			if zone == "" || len(cells) > len(zoneCells[zone]) || len(cells) == len(zoneCells[zone]) && id < zone {
				zone = id
			}
		}
		if taken && zone == "" {
			continue
		}
		if zone != "" {
			if adds := growZoneCells(zoneCells[zone], freeFor(block, options[0].Crop), anchor, options[0].Needed); len(adds) > 0 {
				return fieldBlockEdit{Zone: zone, Crop: growing[zone], Cells: adds}, "", true
			}
			continue
		}
		neighbours := fieldBlockNeighbours(facts.Cells, block, growing)
		for _, o := range policy.BlockCropOrder(options, neighbours) {
			if free := freeFor(block, o.Crop); len(free) > 0 && o.Needed > 0 {
				return fieldBlockEdit{Crop: o.Crop.Name, Cells: connectedPick(free, anchor, o.Needed)}, "", true
			}
		}
	}
	return fieldBlockEdit{}, "every field block is full", false
}

// fieldBlockOptions is the crops a new field block may take: candidate
// first, then every other plantable outdoor soil candidate in score order.
func fieldBlockOptions(candidate policy.SiteTypeCandidate, all []policy.SiteTypeCandidate) []policy.FieldBlockOption {
	options := []policy.FieldBlockOption{{Crop: candidate.Crop, Needed: candidate.Needed}}
	seen := map[string]bool{candidate.Crop.Name: true}
	for _, c := range all {
		if c.Kind == policy.SiteOutdoor && len(c.Buildings) == 0 && c.Cells > 0 && !seen[c.Crop.Name] {
			seen[c.Crop.Name] = true
			options = append(options, policy.FieldBlockOption{Crop: c.Crop, Needed: c.Needed})
		}
	}
	return options
}

// fieldBlockNeighbours is the set of crops growing in a zone with a cell
// within FieldBlockNeighbourRadius (Chebyshev) of block's bounding box.
func fieldBlockNeighbours(cells []policy.SiteCell, block map[domain.Cell]bool, growing map[string]string) map[string]bool {
	first := true
	var lo, hi domain.Cell
	for c := range block {
		if first {
			lo, hi, first = c, c, false
			continue
		}
		lo.X, lo.Z = min(lo.X, c.X), min(lo.Z, c.Z)
		hi.X, hi.Z = max(hi.X, c.X), max(hi.Z, c.Z)
	}
	r := int32(policy.FieldBlockNeighbourRadius)
	out := map[string]bool{}
	for _, c := range cells {
		id, ok := c.ZoneID.Value()
		crop, farm := growing[id]
		if !ok || !farm || c.Cell.X < lo.X-r || c.Cell.X > hi.X+r || c.Cell.Z < lo.Z-r || c.Cell.Z > hi.Z+r {
			continue
		}
		out[crop] = true
	}
	return out
}

// adoptsZone reports whether a field of crop grows a zone of zoneCrop: its
// own crop always; a food field also adopts any food zone, keeping the
// zone's crop, but never a hay or social crop's zone (#1226).
func adoptsZone(crop policy.CropChoice, zoneCrop string, inedible map[string]bool) bool {
	if zoneCrop == crop.Name {
		return true
	}
	edible, known := crop.Edible.Value()
	return (!known || edible) && !inedible[zoneCrop]
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

// connectedPick picks up to want cells of free for a new growing zone as one
// 4-connected footprint (#1252): native refuses a disconnected zone, and a
// block's free soil is often split by rock, trees or buildings. It picks
// inside free's largest component (ties to the one nearest anchor), then
// keeps the picked cells connected to the picked cell nearest anchor.
func connectedPick(free map[domain.Cell]bool, anchor domain.Cell, want int) []domain.Cell {
	var best map[domain.Cell]bool
	bestNear := int64(-1)
	seen := map[domain.Cell]bool{}
	for _, c := range sortedCells(free) {
		if seen[c] {
			continue
		}
		comp := cellComponent(free, c)
		near := int64(-1)
		for m := range comp {
			seen[m] = true
			if d := cellDist(m, anchor); near < 0 || d < near {
				near = d
			}
		}
		if len(comp) > len(best) || len(comp) == len(best) && near < bestNear {
			best, bestNear = comp, near
		}
	}
	picked := map[domain.Cell]bool{}
	for _, c := range policy.PickRect(best, anchor, want) {
		picked[c] = true
	}
	if len(picked) == 0 {
		return nil
	}
	var start domain.Cell
	near := int64(-1)
	for _, c := range sortedCells(picked) {
		if d := cellDist(c, anchor); near < 0 || d < near {
			start, near = c, d
		}
	}
	return sortedCells(cellComponent(picked, start))
}

// cellComponent is the 4-connected cells of set reachable from start.
func cellComponent(set map[domain.Cell]bool, start domain.Cell) map[domain.Cell]bool {
	out := map[domain.Cell]bool{start: true}
	queue := []domain.Cell{start}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		for _, n := range []domain.Cell{{X: c.X, Z: c.Z - 1}, {X: c.X - 1, Z: c.Z}, {X: c.X + 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}} {
			if set[n] && !out[n] {
				out[n] = true
				queue = append(queue, n)
			}
		}
	}
	return out
}

func sortedCells(set map[domain.Cell]bool) []domain.Cell {
	out := make([]domain.Cell, 0, len(set))
	for c := range set {
		out = append(out, c)
	}
	sortCells(out)
	return out
}

func cellDist(a, b domain.Cell) int64 {
	dx, dz := int64(a.X-b.X), int64(a.Z-b.Z)
	return dx*dx + dz*dz
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
