package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DefenseArrival identifies one ground raid, rather than a pawn or a turret
// burst. Edge is the observed boundary crossing: the first sampled position
// of the raid inside the census. It is matched to the sector whose edge
// cell lies nearest, within defenseArrivalSnap cells; a distant map-edge
// coordinate does not establish which local opening the raid used.
type DefenseArrival struct {
	ID   string
	Edge domain.Cell
	Tick domain.Tick
}

// DefenseSector is a connected run on one side of the census boundary. Home
// reachability and Route use only observed passable cells. A bounded census
// establishes local approaches, not all possible routes across the map.
type DefenseSector struct {
	Edge            []domain.Cell
	PathLength      int
	RecentRaids     int
	LastArrivalTick domain.Tick
	// Route runs from the nearest opening in the sector to Entry, with the
	// proposed funnel walls treated as impassable.
	Route []domain.Cell
	// Finding reports a route/layout problem, never permission to demolish.
	Finding string
}

// DefenseCover is stewardship demand. Sector indexes the ranked Sectors;
// a Hold keeps the cell visible but forbids treating it as clearance work.
// Native identity, roof support, reach and dispatch safety still need reads.
type DefenseCover struct {
	Cell   domain.Cell
	Fill   float64
	Sector int
	Hold   string
}

type DefenseApproaches struct {
	Sectors []DefenseSector
	Cover   []DefenseCover
	// UnmatchedArrivals cannot be mapped through the observed boundary.
	UnmatchedArrivals []string
	Hold              string
}

const defenseArrivalWindow domain.Tick = 3 * 60000
const defenseRouteTail = 6

// defenseArrivalSnap bounds how far an arrival's crossing may lie from a
// sector's edge cell: the native trail is sampled every 60 ticks, a few
// cells of raider movement.
const defenseArrivalSnap = 8

func validateDefenseArrivals(r DefenseRequest) error {
	if r.Tick < 0 || len(r.Arrivals) > 128 {
		return errors.New("invalid defense arrival census")
	}
	seen := map[string]DefenseArrival{}
	for _, a := range r.Arrivals {
		if a.ID == "" || len(a.ID) > 256 || a.Tick < 0 || a.Tick > r.Tick || a.Edge.X < 0 || a.Edge.Z < 0 || a.Edge.X >= r.Bounds.Width || a.Edge.Z >= r.Bounds.Height {
			return errors.New("invalid defense arrival")
		}
		if old, ok := seen[a.ID]; ok && old != a {
			return errors.New("conflicting defense arrival")
		}
		seen[a.ID] = a
	}
	if v, known := r.CoverThreshold.Value(); known && (math.IsNaN(v) || math.IsInf(v, 0) || v < 0 || v >= 1) {
		return errors.New("invalid defense cover threshold")
	}
	return nil
}

func defenseCellLess(a, b domain.Cell) bool {
	if a.Z != b.Z {
		return a.Z < b.Z
	}
	return a.X < b.X
}

// paths is one bounded flood, shared by all sectors instead of one search
// per edge cell. Unknowns and projected walls cannot connect components.
func (s defenseSite) paths(start domain.Cell, closed map[domain.Cell]bool) (map[domain.Cell]domain.Cell, map[domain.Cell]int) {
	prev := map[domain.Cell]domain.Cell{}
	distance := map[domain.Cell]int{}
	if !s.passable(start) || closed[start] {
		return prev, distance
	}
	prev[start], distance[start] = start, 0
	queue := []domain.Cell{start}
	for i := 0; i < len(queue); i++ {
		c := queue[i]
		for _, d := range directions {
			n := addCell(c, d)
			if _, seen := prev[n]; seen || closed[n] || !s.passable(n) {
				continue
			}
			prev[n], distance[n] = c, distance[c]+1
			queue = append(queue, n)
		}
	}
	return prev, distance
}

func defensePath(prev map[domain.Cell]domain.Cell, from domain.Cell) []domain.Cell {
	var path []domain.Cell
	for {
		next, ok := prev[from]
		if !ok {
			return nil
		}
		path = append(path, from)
		if next == from {
			return path
		}
		from = next
	}
}

func (s defenseSite) borderSide(c domain.Cell) int {
	r := s.r.Region
	switch {
	case c.Z == r.Z:
		return 0
	case c.X == r.X+r.Width-1:
		return 1
	case c.Z == r.Z+r.Height-1:
		return 2
	default:
		return 3
	}
}

// sectors floods from origin: Home while it is passable, else the
// layout's Entry, since the colony centre drifts onto buildings as the base
// grows and the approaches are what reaches the corridor anyway.
func (s defenseSite) sectors(origin domain.Cell) ([]DefenseSector, []string) {
	_, distance := s.paths(origin, nil)
	edge := map[domain.Cell]bool{}
	var ordered []domain.Cell
	for c := range distance {
		if s.onBorder(c) && positive(s.cells[c].EdgeReachable) {
			edge[c] = true
			ordered = append(ordered, c)
		}
	}
	sort.Slice(ordered, func(i, j int) bool { return defenseCellLess(ordered[i], ordered[j]) })
	var sectors []DefenseSector
	seen := map[domain.Cell]bool{}
	for _, first := range ordered {
		if seen[first] {
			continue
		}
		seen[first] = true
		sector := DefenseSector{Edge: []domain.Cell{first}, PathLength: distance[first]}
		for i := 0; i < len(sector.Edge); i++ {
			c := sector.Edge[i]
			if distance[c] < sector.PathLength {
				sector.PathLength = distance[c]
			}
			for _, d := range directions {
				n := addCell(c, d)
				if edge[n] && !seen[n] && s.borderSide(n) == s.borderSide(first) {
					seen[n] = true
					sector.Edge = append(sector.Edge, n)
				}
			}
		}
		sort.Slice(sector.Edge, func(i, j int) bool { return defenseCellLess(sector.Edge[i], sector.Edge[j]) })
		sectors = append(sectors, sector)
	}
	byCell := map[domain.Cell]int{}
	for i, sector := range sectors {
		for _, c := range sector.Edge {
			byCell[c] = i
		}
	}
	counted := map[string]bool{}
	var unmatched []string
	for _, a := range s.r.Arrivals {
		if counted[a.ID] || s.r.Tick-a.Tick > defenseArrivalWindow {
			continue
		}
		counted[a.ID] = true
		i, ok := byCell[a.Edge]
		if !ok {
			// The crossing was sampled a few cells inside the border:
			// the nearest sector edge cell within the snap radius, the
			// lowest-ordered on a tie.
			nearest := int64(defenseArrivalSnap*defenseArrivalSnap + 1)
			for _, c := range ordered {
				if d := int64(squaredDistance(c, a.Edge)); d < nearest {
					i, ok, nearest = byCell[c], true, d
				}
			}
		}
		if !ok {
			unmatched = append(unmatched, a.ID)
			continue
		}
		sectors[i].RecentRaids++
		if a.Tick > sectors[i].LastArrivalTick {
			sectors[i].LastArrivalTick = a.Tick
		}
	}
	sort.Strings(unmatched)
	sort.Slice(sectors, func(i, j int) bool {
		a, b := sectors[i], sectors[j]
		if a.RecentRaids != b.RecentRaids {
			return a.RecentRaids > b.RecentRaids
		}
		if a.PathLength != b.PathLength {
			return a.PathLength < b.PathLength
		}
		return defenseCellLess(a.Edge[0], b.Edge[0])
	})
	return sectors, unmatched
}

// DefenseApproachesFor recomputes the approaches of an accepted layout
// against a fresh census: the layout's tiers, lanes and firing cells stay
// protected, and the request's arrivals and cover threshold decide the
// cover demand (#581). It validates the request as DefenseLayouts does.
func DefenseApproachesFor(r DefenseRequest, l DefenseLayout) (DefenseApproaches, error) {
	s, err := newDefenseSite(r)
	if err != nil {
		return DefenseApproaches{}, err
	}
	return s.defenseApproaches(l), nil
}

func (s defenseSite) defenseApproaches(l DefenseLayout) DefenseApproaches {
	out := DefenseApproaches{}
	origin := s.r.Home
	if !s.passable(origin) {
		origin = l.Entry
	}
	out.Sectors, out.UnmatchedArrivals = s.sectors(origin)
	closed := map[domain.Cell]bool{}
	protected := map[domain.Cell]bool{}
	for c := range s.protect {
		protected[c] = true
	}
	for _, t := range l.Tiers {
		for _, c := range t.Reserved {
			protected[c] = true
		}
		for _, b := range t.Buildings {
			protected[b.Cell()] = true
			if b.Definition() == s.r.Definitions.Wall {
				closed[b.Cell()] = true
			}
		}
	}
	for _, c := range append(append([]domain.Cell{}, l.TrapLane...), l.SafeLane...) {
		protected[c] = true
		// Existing rock can be the corridor wall without a placement in any
		// tier. Removing it would open the flank of the accepted layout.
		for _, d := range directions {
			if n := addCell(c, d); s.blocking(n) {
				protected[n] = true
			}
		}
	}
	for _, f := range l.Firing {
		protected[f.Cell], protected[f.Cover], protected[f.Retreat] = true, true, true
	}
	prev, distance := s.paths(l.Entry, closed)
	withoutEntry := map[domain.Cell]bool{l.Entry: true}
	if len(l.SafeLane) > 0 {
		withoutEntry[l.SafeLane[0]] = true
	}
	for c := range closed {
		withoutEntry[c] = true
	}
	// A route to a firing cell with Entry closed is a separate geometry
	// finding. Clearing vegetation cannot repair that bypass.
	bypass := map[domain.Cell]bool{}
	for _, f := range l.Firing {
		paths, _ := s.paths(f.Cell, withoutEntry)
		for c := range paths {
			bypass[c] = true
		}
	}
	for i := range out.Sectors {
		sector := &out.Sectors[i]
		found := false
		var best domain.Cell
		for _, c := range sector.Edge {
			if bypass[c] {
				sector.Finding = "route_bypasses_entry"
			}
			if d, ok := distance[c]; ok && (!found || d < distance[best]) {
				best, found = c, true
			}
		}
		if found {
			sector.Route = defensePath(prev, best)
		} else {
			sector.Finding = "entry_unreachable"
		}
	}
	threshold, thresholdKnown := s.r.CoverThreshold.Value()
	rangeLimit, rangeKnown := s.r.MinRange.Value()
	if !thresholdKnown {
		out.Hold = "cover_threshold_unknown"
		return out
	}
	if !rangeKnown || len(l.Firing) == 0 {
		out.Hold = "engagement_range_unknown"
		return out
	}
	d := directionOf(l.Toward)
	for c, row := range s.cells {
		fill, known := row.CoverFill.Value()
		if protected[c] || !known || fill <= threshold {
			continue
		}
		inRange := false
		for _, f := range l.Firing {
			if float64(squaredDistance(c, f.Cell)) <= rangeLimit*rangeLimit {
				inRange = true
				break
			}
		}
		if !inRange {
			continue
		}
		forward := (c.X-l.Entry.X)*d.X+(c.Z-l.Entry.Z)*d.Z <= 0
		sectorIndex, nearest := -1, int64(math.MaxInt64)
		for i, sector := range out.Sectors {
			start := max(0, len(sector.Route)-defenseRouteTail)
			for _, at := range sector.Route[start:] {
				dist := int64(squaredDistance(c, at))
				if (forward || dist <= 2) && dist < nearest {
					sectorIndex, nearest = i, dist
				}
			}
		}
		if sectorIndex < 0 {
			continue
		}
		hold := ""
		if positive(row.NaturalRock) && (c.X == 0 || c.Z == 0 || c.X == s.r.Bounds.Width-1 || c.Z == s.r.Bounds.Height-1) {
			hold = "map_edge_rock"
		}
		if positive(row.NaturalRock) && hold == "" {
			exposed := false
			for _, delta := range directions {
				exposed = exposed || s.passable(addCell(c, delta))
			}
			if !exposed {
				hold = "mountain_interior"
			}
		}
		if positive(row.NaturalRock) && hold == "" {
			// A rock face is terrain: mining one cell of a mass exposes the
			// next, and the mass may be the wall the layout leans on. Only
			// a lone rock (no natural rock beside it) is orderable.
			for _, delta := range directions {
				if n := addCell(c, delta); positive(s.cells[n].NaturalRock) && !s.passable(n) {
					hold = "rock_face"
					break
				}
			}
		}
		out.Cover = append(out.Cover, DefenseCover{Cell: c, Fill: fill, Sector: sectorIndex, Hold: hold})
	}
	sort.Slice(out.Cover, func(i, j int) bool {
		a, b := out.Cover[i], out.Cover[j]
		if a.Sector != b.Sector {
			return a.Sector < b.Sector
		}
		// Nearest the entry first: the cover a raider would take at the
		// line is cleared before the cover farther up the approach.
		if da, db := squaredDistance(a.Cell, l.Entry), squaredDistance(b.Cell, l.Entry); da != db {
			return da < db
		}
		return defenseCellLess(a.Cell, b.Cell)
	})
	return out
}
