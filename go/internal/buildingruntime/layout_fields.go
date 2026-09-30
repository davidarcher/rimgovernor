package buildingruntime

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Outdoor food fields fill the layout plan's field blocks at every tier
// (#1223, epic #1212). A plan field zone is a whole fertile patch (#1281);
// inside it each crop takes its own adjacent crop block, sized by its
// demand, and grows in place with add-cells (#1283).

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

// planFieldBlock walks the plan's field patches (policy FieldBlocks order)
// and returns the first patch's step that still has free soil: grow the
// growing zone in the patch that options[0] adopts by its need, or create
// a new crop block against the patch's standing zones. A new block takes
// the first option, in policy.BlockCropOrder, not growing within
// FieldBlockNeighbourRadius of that block (#1225). options are the viable
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
	rich := map[domain.Cell]bool{}
	for _, c := range facts.Cells {
		census[c.Cell] = c
		if f, ok := c.Fertility.Value(); ok && f > fieldRichFertility {
			rich[c.Cell] = true
		}
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
		// A patch holds adjacent crop blocks, one growing zone each (#1283):
		// options[0] grows the largest zone it adopts in place, a ring of
		// free cells around it; hay and social crops never share a food
		// zone (#1226). Zones never move.
		zone := ""
		occupied := map[domain.Cell]bool{}
		for id, cells := range zoneCells {
			for c := range cells {
				occupied[c] = true
			}
			if !adoptsZone(options[0].Crop, growing[id], inedible) {
				continue
			}
			if zone == "" || len(cells) > len(zoneCells[zone]) || len(cells) == len(zoneCells[zone]) && id < zone {
				zone = id
			}
		}
		if zone != "" {
			if adds := connectedAdds(zoneCells[zone], freeFor(block, options[0].Crop), options[0].Needed); len(adds) > 0 {
				return fieldBlockEdit{Zone: zone, Crop: growing[zone], Cells: adds}, "", true
			}
		}
		// A new crop block sits against the patch's standing blocks, with no
		// gap; its crop avoids one growing within FieldBlockNeighbourRadius
		// of the block itself (#1225), not of the whole patch.
		if first := connectedPick(freeFor(block, options[0].Crop), occupied, rich, anchor, options[0].Needed); len(first) > 0 {
			around := make(map[domain.Cell]bool, len(first))
			for _, c := range first {
				around[c] = true
			}
			neighbours := fieldBlockNeighbours(facts.Cells, around, growing)
			for _, o := range policy.BlockCropOrder(options, neighbours) {
				if o.Needed <= 0 {
					continue
				}
				if cells := connectedPick(freeFor(block, o.Crop), occupied, rich, anchor, o.Needed); len(cells) > 0 {
					return fieldBlockEdit{Crop: o.Crop.Name, Cells: cells}, "", true
				}
			}
		}
	}
	return fieldBlockEdit{}, "every field block is full", false
}

// fieldRichFertility is the fertility above which soil is rich, the
// policy survey's line (#1284).
const fieldRichFertility = 1.0

// fieldShortfall is one goal's open field-block demand in the cross-crop
// ledger (#1308): options lead with the goal's own crop and cells.
type fieldShortfall struct {
	Goal    store.GoalState
	Options []policy.FieldBlockOption
	What    string
}

// fieldShortfallWeight is needed cells weighted by urgency: goal priority
// 0 is the most urgent of the 0-4 classes.
func fieldShortfallWeight(s fieldShortfall) int {
	if len(s.Options) == 0 {
		return 0
	}
	return (5 - s.Goal.Goal.Priority) * s.Options[0].Needed
}

// rankFieldShortfalls orders the ledger heaviest first; the first placed
// block takes the patch's richest free cells. It decides order only, never
// how many cells a goal gets.
func rankFieldShortfalls(ledger []fieldShortfall) []fieldShortfall {
	out := append([]fieldShortfall(nil), ledger...)
	sort.SliceStable(out, func(i, j int) bool { return fieldShortfallWeight(out[i]) > fieldShortfallWeight(out[j]) })
	return out
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

// connectedAdds floods from zone through set and returns up to want
// non-zone cells reached, nearest the zone first, in row-major order: a
// zone grows in place by rings, so it stays compact and connected.
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
// patch's free soil is often split by rock, trees or buildings. It picks
// inside free's largest component (ties to the one nearest anchor). The
// block is sized to want and roughly square (#1283): it seeds at the free
// cell touching a standing block (occupied) nearest anchor, or the free
// cell nearest anchor when none touches, and takes the fullest of the four
// sqrt(want)-wide rectangles cornered at the seed, topped up by rings.
// Rich soil (#1308) breaks those ties: the seed prefers a rich cell, and
// the rectangle holding the most rich cells wins, so the block the field
// ledger places first takes the patch's richest free cells.
func connectedPick(free, occupied, rich map[domain.Cell]bool, anchor domain.Cell, want int) []domain.Cell {
	if want <= 0 {
		return nil
	}
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
	if len(best) == 0 {
		return nil
	}
	var seed domain.Cell
	near, touching, seedRich := int64(-1), false, false
	for _, c := range sortedCells(best) {
		t := occupied[domain.Cell{X: c.X, Z: c.Z - 1}] || occupied[domain.Cell{X: c.X - 1, Z: c.Z}] || occupied[domain.Cell{X: c.X + 1, Z: c.Z}] || occupied[domain.Cell{X: c.X, Z: c.Z + 1}]
		d, r := cellDist(c, anchor), rich[c]
		if near < 0 || t && !touching || t == touching && (r && !seedRich || r == seedRich && d < near) {
			seed, near, touching, seedRich = c, d, t, r
		}
	}
	w := int32(1)
	for int(w*w) < want {
		w++
	}
	h := int32((want + int(w) - 1) / int(w))
	var picked map[domain.Cell]bool
	pickedRich := 0
	for _, dir := range [][2]int32{{1, 1}, {-1, 1}, {1, -1}, {-1, -1}} {
		rect := map[domain.Cell]bool{}
		n := 0
		for dz := int32(0); dz < h && len(rect) < want; dz++ {
			for dx := int32(0); dx < w && len(rect) < want; dx++ {
				if c := (domain.Cell{X: seed.X + dir[0]*dx, Z: seed.Z + dir[1]*dz}); best[c] {
					rect[c] = true
					if rich[c] {
						n++
					}
				}
			}
		}
		if n > pickedRich || n == pickedRich && len(rect) > len(picked) {
			picked, pickedRich = rect, n
		}
	}
	picked = cellComponent(picked, seed)
	for _, c := range connectedAdds(picked, best, want-len(picked)) {
		picked[c] = true
	}
	return sortedCells(picked)
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
