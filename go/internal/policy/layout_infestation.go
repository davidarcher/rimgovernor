package policy

import (
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Infestation prevention. Insects dig out of open
// ground under overhead mountain that is dark: a small unused pocket of
// such ground beside the base is walled solid, and every planned room
// gets lamps lighting its whole floor above the darkness threshold.

// Each pocket is a NodePocketWall site node produced after the ring node
// (#2925): a fill of open overhead-mountain cells near the base the perimeter
// tier walls solid. It has no room, only the cells.

// ReasonPocketBesideBase: a small dark pocket touching the ring or within
// pocketReach of it, that nothing else uses.
const ReasonPocketBesideBase = "pocket_beside_base"

const (
	// pocketMaxCells is the largest pocket walled solid; a bigger open
	// cave is a room or the infestation response's business.
	pocketMaxCells = 20
	// pocketReach is how far outside the perimeter ring a pocket
	// still counts as beside the base. A judgment constant: the game offers no signal for it.
	pocketReach int32 = 6
	// TierPocketPrefix and TierLightPrefix name the pocket-wall and
	// room-lamp sections under the perimeter's prefix, so the perimeter
	// recut builds and removes them with the wall.
	TierPocketPrefix = TierPerimeterPrefix + "pocket-"
	TierLightPrefix  = TierPerimeterPrefix + "light-"
)

// PlanMountainPockets replaces plan's pocket-wall nodes with one
// per small pocket: a 4-connected patch of at most
// pocketMaxCells walkable, unbuilt cells under thick roof, touching the
// perimeter ring or within pocketReach of it, that no room, hallway or
// other site node or reservation uses. A plan without a perimeter holds none.
func PlanMountainPockets(plan LayoutPlan, s MapSurvey) LayoutPlan {
	plan = plan.withoutSites(NodePocketWall)
	if _, ok := plan.Ring(); !ok {
		return plan
	}
	used := map[domain.Cell]bool{}
	for _, r := range plan.Reserved() {
		for _, c := range r.Area.Cells() {
			used[c] = true
		}
	}
	for _, r := range plan.Rooms {
		for _, c := range rectCells(roomWalls(r)) {
			used[c] = true
		}
	}
	wi, _ := planInterior(plan, pocketReach)
	near := wi.near(pocketReach)
	open := map[domain.Cell]bool{}
	for _, c := range s.Cells {
		if c.ThickRoof && c.Walkable && !c.Rock && !c.Built {
			open[c.Cell] = true
		}
	}
	seen := map[domain.Cell]bool{}
	var starts []domain.Cell
	for c := range open {
		starts = append(starts, c)
	}
	sort.Slice(starts, func(i, j int) bool { return cellLess(starts[i], starts[j]) })
	for _, start := range starts {
		if seen[start] {
			continue
		}
		patch := []domain.Cell{start}
		seen[start] = true
		for i := 0; i < len(patch); i++ {
			c := patch[i]
			for _, n := range []domain.Cell{{X: c.X + 1, Z: c.Z}, {X: c.X - 1, Z: c.Z}, {X: c.X, Z: c.Z + 1}, {X: c.X, Z: c.Z - 1}} {
				if open[n] && !seen[n] {
					seen[n] = true
					patch = append(patch, n)
				}
			}
		}
		if len(patch) > pocketMaxCells {
			continue
		}
		beside, free := false, true
		for _, c := range patch {
			beside = beside || near(c) >= 0
			free = free && !used[c]
		}
		if !beside || !free {
			continue
		}
		id := fmt.Sprintf("%s-%02d", NodePocketWall, len(plan.LiveSites(NodePocketWall)))
		plan.Sites = append(plan.Sites, newSite(id, NodePocketWall, RegionOfRuns(cellRuns(patch)), ReasonPocketBesideBase))
	}
	return plan
}

// PocketSections is one section per pocket-wall node, each cell a
// wall with its stuff left for admission to fill.
func PocketSections(plan LayoutPlan, wall string) ([]PerimeterSection, error) {
	var out []PerimeterSection
	for _, r := range plan.LiveSites(NodePocketWall) {
		s := PerimeterSection{Name: DefenseTierName(fmt.Sprintf("%s%02d", TierPocketPrefix, len(out)))}
		for _, c := range r.Footprint.Cells() {
			b, err := domain.NewBuilding(wall, c, domain.North, "")
			if err != nil {
				return nil, err
			}
			s.Buildings = append(s.Buildings, b)
		}
		out = append(out, s)
	}
	return out, nil
}

// BaseRoomLamps is the lamp cells lighting every built room of plan (all
// but reserve rooms): a grid over each interior whose lamps sit no farther
// than reach (the standing lamp's glowReach from the catalog) from any floor
// cell. An unknown reach places none. Rooms keep plan order.
func BaseRoomLamps(plan LayoutPlan, reach float64) []domain.Cell {
	if !finitePositive(reach) {
		return nil
	}
	var out []domain.Cell
	for _, r := range plan.Rooms {
		if r.Role == PlannedReserve {
			continue
		}
		in := r.Region.Bounds()
		// A lamp covers a square of side reach*sqrt2 around it.
		span := int32(math.Floor(reach * math.Sqrt2))
		if !r.Region.IsRect() {
			out = append(out, shapedRoomLamps(r.Region, span, reach)...)
			continue
		}
		nx, nz := (in.Width+span-1)/span, (in.Height+span-1)/span
		for iz := int32(0); iz < nz; iz++ {
			for ix := int32(0); ix < nx; ix++ {
				out = append(out, domain.Cell{X: in.X + (2*ix+1)*in.Width/(2*nx), Z: in.Z + (2*iz+1)*in.Height/(2*nz)})
			}
		}
	}
	return out
}

// LightSections is one section holding every lamp placed by
// BaseRoomLamps, in plan order.
func LightSections(plan LayoutPlan, lamp string, reach float64) ([]PerimeterSection, error) {
	cells := BaseRoomLamps(plan, reach)
	if len(cells) == 0 {
		return nil, nil
	}
	s := PerimeterSection{Name: DefenseTierName(TierLightPrefix + "00")}
	for _, c := range cells {
		b, err := domain.NewBuilding(lamp, c, domain.North, "")
		if err != nil {
			return nil, err
		}
		s.Buildings = append(s.Buildings, b)
	}
	return []PerimeterSection{s}, nil
}

// shapedRoomLamps lights a non-rectangular region (an L, a ring): one lamp
// on the floor cell nearest the middle of each span-square of the bounds
// that holds floor, then a lamp on every floor cell still farther than
// reach from all lamps, row-major. Lamps never stand off the floor.
func shapedRoomLamps(region Region, span int32, reach float64) []domain.Cell {
	in := region.Bounds()
	cells := region.Cells()
	sort.Slice(cells, func(i, j int) bool {
		return cells[i].Z < cells[j].Z || cells[i].Z == cells[j].Z && cells[i].X < cells[j].X
	})
	nx, nz := (in.Width+span-1)/span, (in.Height+span-1)/span
	var out []domain.Cell
	for iz := int32(0); iz < nz; iz++ {
		for ix := int32(0); ix < nx; ix++ {
			mid := domain.Cell{X: in.X + (2*ix+1)*in.Width/(2*nx), Z: in.Z + (2*iz+1)*in.Height/(2*nz)}
			x0, z0 := in.X+ix*in.Width/nx, in.Z+iz*in.Height/nz
			x1, z1 := in.X+(ix+1)*in.Width/nx, in.Z+(iz+1)*in.Height/nz
			best, found := domain.Cell{}, false
			for _, c := range cells {
				if c.X < x0 || c.X >= x1 || c.Z < z0 || c.Z >= z1 {
					continue
				}
				if !found || distanceSquared(c, mid) < distanceSquared(best, mid) {
					best, found = c, true
				}
			}
			if found {
				out = append(out, best)
			}
		}
	}
	lit := func(c domain.Cell) bool {
		for _, l := range out {
			if distance(l, c) <= reach {
				return true
			}
		}
		return false
	}
	for _, c := range cells {
		if !lit(c) {
			out = append(out, c)
		}
	}
	return out
}
