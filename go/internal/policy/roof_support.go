package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Roof support over the cell mirror: a pure port of the
// native RoofSupportGeometry.Blocker, so a batch of removals is validated as
// one joint counterfactual without a native call. A roofed cell stays up while
// a connected run of roofed cells (cardinal steps, each within Radius of the
// run's root) touches a roof-holding edifice that the removal leaves standing.

// Roof blocker verdicts, the same words as the native check.
const (
	RoofBlockerNoSupportCells = "No occupied support cells"
	RoofBlockerUnknownGeom    = "Unknown building support geometry"
	RoofBlockerUnknownAlt     = "Alternate support geometry is unknown"
	RoofBlockerCollapse       = "Roof collapse is already pending"
	RoofBlockerUnsupported    = "Removing this building would leave unsupported roof"
)

// RoofSupportGrid is what the check reads. Cell returns the mirror row; a
// cell inside Bounds with no row is fogged. Radius is the catalog's
// roof_max_support_distance. CollapsePending is optional: the mirror does not
// carry the native collapse buffer, so a nil func reads as nothing pending.
// Structural lists fogged cells a bounded survey vouches for (a sealed
// shrine's interior): they are treated as known, never as a blanket bypass.
type RoofSupportGrid struct {
	Cell            func(domain.Cell) (SiteCell, bool)
	Bounds          Rectangle
	Radius          float64
	CollapsePending func(domain.Cell) bool
	Structural      map[domain.Cell]bool
	// AssumedHolders are open cells counted as holders the removal would
	// find standing (the backups a stone shell will have built).
	AssumedHolders map[domain.Cell]bool
}

// RemovedBuilding names one building to remove by any cell it occupies.
type RemovedBuilding struct {
	Cell domain.Cell
	ID   uint64
}

func (g RoofSupportGrid) inBounds(c domain.Cell) bool {
	b := g.Bounds
	return c.X >= b.X && c.X < b.X+b.Width && c.Z >= b.Z && c.Z < b.Z+b.Height
}

// row is the cell's mirror row and whether the geometry there is unknown
// (fogged and not surveyed, or the roof fact unknown).
func (g RoofSupportGrid) row(c domain.Cell) (SiteCell, bool) {
	row, ok := g.Cell(c)
	if !ok {
		return SiteCell{}, !g.Structural[c]
	}
	return row, false
}

func (g RoofSupportGrid) roofed(c domain.Cell) bool {
	row, ok := g.Cell(c)
	if !ok {
		// A fogged cell has no row; the caller classifies it unknown (or
		// structural) and a surveyed structural cell is roofed by construction.
		return true
	}
	v, known := row.Roofed.Value()
	return known && v
}

func (g RoofSupportGrid) holds(c domain.Cell) bool {
	if g.AssumedHolders[c] {
		return true
	}
	row, ok := g.Cell(c)
	if !ok {
		return false
	}
	t, ok := row.edifice()
	return ok && t.Has(FlagHoldsRoof)
}

func (g RoofSupportGrid) within(a, b domain.Cell) bool {
	dx, dz := float64(a.X-b.X), float64(a.Z-b.Z)
	return dx*dx+dz*dz <= g.Radius*g.Radius
}

var cardinal = [4][2]int32{{1, 0}, {-1, 0}, {0, 1}, {0, -1}}

// Footprint is every cell the building with id occupies, found by walking
// cardinally connected cells that carry the same thing id from origin (the
// mirror lists a multi-cell building on each of its cells). Nil when origin
// holds no such thing.
func (g RoofSupportGrid) Footprint(origin domain.Cell, id uint64) []domain.Cell {
	has := func(c domain.Cell) bool {
		row, ok := g.Cell(c)
		if !ok {
			return false
		}
		for _, t := range row.Things {
			if t.ID == id && t.Category == ThingBuilding {
				return true
			}
		}
		return false
	}
	if !has(origin) {
		return nil
	}
	seen := map[domain.Cell]bool{origin: true}
	out := []domain.Cell{origin}
	for i := 0; i < len(out); i++ {
		for _, d := range cardinal {
			n := domain.Cell{X: out[i].X + d[0], Z: out[i].Z + d[1]}
			if !seen[n] && has(n) {
				seen[n] = true
				out = append(out, n)
			}
		}
	}
	return out
}

// RoofBlocker is the verdict for removing every building in the batch at
// once: "" when every roof near them keeps a holder, else the first blocker.
// A building whose def holds no roof removes nothing the roof depends on and
// is skipped; a batch of only such buildings is "" too. Two buildings each
// safe alone can still block together, since each excludes the other as a
// holder.
func (g RoofSupportGrid) RoofBlocker(batch []RemovedBuilding) (blocker string, checkedRoofs int) {
	excluded := map[domain.Cell]bool{}
	var removed []domain.Cell
	for _, b := range batch {
		row, ok := g.Cell(b.Cell)
		if !ok {
			return RoofBlockerUnknownGeom, 0
		}
		var holder bool
		for _, t := range row.Things {
			if t.ID == b.ID && t.Category == ThingBuilding {
				holder = t.Has(FlagHoldsRoof)
			}
		}
		if !holder {
			continue
		}
		for _, c := range g.Footprint(b.Cell, b.ID) {
			if !excluded[c] {
				excluded[c] = true
				removed = append(removed, c)
			}
		}
	}
	if len(removed) == 0 {
		return "", 0
	}
	return g.Blocker(removed)
}

// Blocker is the native RoofSupportGeometry.Blocker over explicit removed
// cells: every cell is excluded as a holder. "" means no unsupported roof.
func (g RoofSupportGrid) Blocker(removed []domain.Cell) (blocker string, checkedRoofs int) {
	if len(removed) == 0 {
		return RoofBlockerNoSupportCells, 0
	}
	excluded := make(map[domain.Cell]bool, len(removed))
	for _, c := range removed {
		excluded[c] = true
	}
	var roots []domain.Cell
	rootSet := map[domain.Cell]bool{}
	r := int32(g.Radius)
	for _, c := range removed {
		for dz := -r - 1; dz <= r+1; dz++ {
			for dx := -r - 1; dx <= r+1; dx++ {
				near := domain.Cell{X: c.X + dx, Z: c.Z + dz}
				if !g.within(near, c) {
					continue
				}
				if !g.inBounds(near) {
					return RoofBlockerUnknownGeom, 0
				}
				if _, unknown := g.row(near); unknown {
					return RoofBlockerUnknownGeom, 0
				}
				if g.roofed(near) && !rootSet[near] {
					rootSet[near] = true
					roots = append(roots, near)
				}
			}
		}
	}
	for _, root := range roots {
		checkedRoofs++
		if g.CollapsePending != nil && g.CollapsePending(root) {
			return RoofBlockerCollapse, checkedRoofs
		}
		seen := map[domain.Cell]bool{root: true}
		queue := []domain.Cell{root}
		supported, unknown := false, false
		for len(queue) > 0 && !supported {
			cell := queue[0]
			queue = queue[1:]
			// adjacent-and-inside: the cell and its four neighbours.
			for i := -1; i < len(cardinal) && !supported; i++ {
				near := cell
				if i >= 0 {
					near = domain.Cell{X: cell.X + cardinal[i][0], Z: cell.Z + cardinal[i][1]}
				}
				if !g.inBounds(near) || !g.within(near, root) {
					continue
				}
				if _, u := g.row(near); u {
					unknown = true
					continue
				}
				if !excluded[near] && g.holds(near) {
					supported = true
				}
			}
			for _, d := range cardinal {
				next := domain.Cell{X: cell.X + d[0], Z: cell.Z + d[1]}
				if !g.inBounds(next) || !g.within(next, root) || !g.roofed(next) {
					continue
				}
				if _, u := g.row(next); u {
					unknown = true
					continue
				}
				if !seen[next] {
					seen[next] = true
					queue = append(queue, next)
				}
			}
		}
		if !supported {
			if unknown {
				return RoofBlockerUnknownAlt, checkedRoofs
			}
			return RoofBlockerUnsupported, checkedRoofs
		}
	}
	return "", checkedRoofs
}
