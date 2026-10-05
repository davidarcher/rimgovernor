package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Whole-map zoning (#778, A2): every surveyed cell is classified from
// terrain. Zones overlap on purpose: core candidates are every cell a room
// could stand on (buildable ground or rock to dig out), and fields, mining
// and wood lie over the same cells; the core planner (A3) takes its
// footprint out of the candidates and the other zones yield to it. No-go
// cells belong to no other zone. Unsurveyed (fogged) cells get no zone.

// ZoneCore is ground the core may grow over.
const ZoneCore ZoneKind = "core"

const (
	// zoneFieldFertility is the least fertility a field cell needs
	// (plain soil).
	zoneFieldFertility = 1.0
	// zoneFieldMin is the fewest cells a fertile patch needs to be a
	// field; smaller patches are pasture.
	zoneFieldMin = 16
)

// Zone classifies every surveyed cell into whole-map zones, in order: core
// candidates, fields (one zone per 4-connected fertile patch, largest first),
// pasture, mining (ore first, then the other rock), wood, no-go. Kinds
// with no cells are left out.
func Zone(s MapSurvey) []LayoutZone {
	w, h := s.Bounds.Width, s.Bounds.Height
	if w < 1 || h < 1 {
		return nil
	}
	cells := make([]*SurveyCell, int(w*h))
	for i := range s.Cells {
		c := &s.Cells[i]
		if c.Cell.X >= 0 && c.Cell.Z >= 0 && c.Cell.X < w && c.Cell.Z < h {
			cells[c.Cell.Z*w+c.Cell.X] = c
		}
	}
	e := LayoutEdgeMargin
	noGo := func(i int32) bool {
		c := cells[i]
		x, z := i%w, i/w
		return c != nil && (c.Hazard || !c.Rock && c.Footing != FootingFirm) || x < e || z < e || x >= w-e || z >= h-e
	}
	has := func(pred func(c *SurveyCell) bool) func(int32) bool {
		return func(i int32) bool { return cells[i] != nil && !noGo(i) && pred(cells[i]) }
	}
	soil := has(func(c *SurveyCell) bool { return !c.Rock && c.Walkable && c.Fertility >= zoneFieldFertility })
	fields := components(w, h, soil)
	inField := make([]bool, len(cells))
	var zones []LayoutZone
	add := func(kind ZoneKind, in func(int32) bool) {
		if runs := zoneRuns(w, h, in); len(runs) > 0 {
			zones = append(zones, LayoutZone{Kind: kind, Runs: runs})
		}
	}
	addOre := func(in func(int32) bool) {
		if runs := zoneRuns(w, h, in); len(runs) > 0 {
			zones = append(zones, LayoutZone{Kind: ZoneMining, Runs: runs, Ore: true})
		}
	}
	add(ZoneCore, has(func(c *SurveyCell) bool { return (c.Rock || c.Walkable || c.Ruin) && !c.Prop }))
	for _, f := range fields {
		if len(f) < zoneFieldMin {
			break
		}
		for _, i := range f {
			inField[i] = true
		}
		set := make(map[int32]bool, len(f))
		for _, i := range f {
			set[i] = true
		}
		add(ZoneField, func(i int32) bool { return set[i] })
	}
	add(ZonePasture, has(func(c *SurveyCell) bool {
		return !c.Rock && c.Walkable && c.Fertility > 0 && !inField[c.Cell.Z*w+c.Cell.X]
	}))
	addOre(has(func(c *SurveyCell) bool { return c.Rock && c.Ore }))
	add(ZoneMining, has(func(c *SurveyCell) bool { return c.Rock && !c.Ore }))
	add(ZoneWood, has(func(c *SurveyCell) bool { return c.Tree }))
	add(ZoneNoGo, func(i int32) bool { return cells[i] != nil && noGo(i) })
	return zones
}

// components returns in's 4-connected components, largest first (ties by
// first cell).
func components(w, h int32, in func(int32) bool) [][]int32 {
	seen := make([]bool, int(w*h))
	var out [][]int32
	for start := int32(0); start < w*h; start++ {
		if seen[start] || !in(start) {
			continue
		}
		seen[start] = true
		comp := []int32{start}
		for k := 0; k < len(comp); k++ {
			i := comp[k]
			x, z := i%w, i/w
			for _, n := range [4][2]int32{{x - 1, z}, {x + 1, z}, {x, z - 1}, {x, z + 1}} {
				if n[0] < 0 || n[1] < 0 || n[0] >= w || n[1] >= h {
					continue
				}
				j := n[1]*w + n[0]
				if !seen[j] && in(j) {
					seen[j] = true
					comp = append(comp, j)
				}
			}
		}
		out = append(out, comp)
	}
	for a := 1; a < len(out); a++ { // stable insertion sort by size, descending
		for b := a; b > 0 && len(out[b]) > len(out[b-1]); b-- {
			out[b], out[b-1] = out[b-1], out[b]
		}
	}
	return out
}

// zoneRuns encodes in's cells as row runs, row by row.
func zoneRuns(w, h int32, in func(int32) bool) []RowRun {
	var runs []RowRun
	for z := int32(0); z < h; z++ {
		for x := int32(0); x < w; x++ {
			if !in(z*w + x) {
				continue
			}
			start := x
			for x+1 < w && in(z*w+x+1) {
				x++
			}
			runs = append(runs, RowRun{Z: z, X: start, Length: x - start + 1})
		}
	}
	return runs
}

// zoneRichFertility is the fertility above which soil is rich (#1284).
const zoneRichFertility = 1.0

// FieldCells is every cell of the plan's field zones (turbine lanes
// included, since they are zoned as fields).
func (p LayoutPlan) FieldCells() map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, z := range p.Zones {
		if z.Kind != ZoneField {
			continue
		}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				out[domain.Cell{X: x, Z: r.Z}] = true
			}
		}
	}
	return out
}
