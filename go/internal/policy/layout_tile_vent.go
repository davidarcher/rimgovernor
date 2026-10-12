package policy

import (
	"fmt"
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Vents in the tiled plan (#2915). A vent is a wall cell with a room cell on
// one side and the outdoors, or a neighbouring room, on the other; it is a
// PlannedSite of kind NodeVent, one cell, standing in the wall ring. The
// tiler picks them after the doors are final (keepRoutes), from the wall cells
// no door, link or entrance holds:
//
//   - Outdoors: a wall cell whose outside cell is open ground (tileOpenGround),
//     inside, wall and outside in line.
//   - Room to room: a cell of the shared wall beside the link door.
//
// Each room is priced in sustained watts by the climate model (climate.go): the
// room's own stations' heat inside it, its band (everything under the
// full-rot temperature for a room that stores cold, else the colonists'
// comfortable range), the outdoor curve, and a neighbour held at the heater's
// default target (or, for a cold room, at the top of its band). The vents it
// would take (how many, coordinate by coordinate over the outdoors and each
// link) are priced with their build cost as watts (BuildW) against the room
// with every door closed, with its exterior door held open, and with each
// link door held open. A vent that is not the cheapest is not placed, and the
// decision says so. A vent whose research is missing, or whose availability is
// unknown, is an absent option: the decision names it when it would have won.
//
// The cooler and the vents want the same outside wall cells. Wall stations are
// not set into the ring yet, so the vents leave as many outside cells as the
// room's group has wall stations: neither is given first pick, and the count
// of vents a room takes is bounded by what the cells leave.

// NodeVent is a vent standing in a room's wall.
const NodeVent NodeKind = "vent"

// Verdicts and reasons of vent decisions.
const (
	// TileAbsent: the option was priced and not wanted.
	TileAbsent = "absent"

	ReasonTileVent               = "tile_vent"
	ReasonTileVentLink           = "tile_vent_link"
	ReasonTileVentUnavailable    = "tile_vent_unavailable"
	ReasonTileVentClimateUnknown = "tile_vent_climate_unknown"
	ReasonTileVentNotCheaper     = "tile_vent_not_cheaper"
)

type tileVent struct {
	cell        domain.Cell
	room, other int // room indices; other is -1 for the outdoors
	reason      string
}

// ventChannel is a way a room could be vented: the wall cells that lead to
// one side.
type ventChannel struct {
	cells []domain.Cell
	other int // the neighbour's room index; -1 for the outdoors
}

// ventBand is the band a room is held in: under the full-rot temperature when
// its group stores cold, else the colonists' comfortable range.
func (t *tiler) ventBand(g RoomGroup) (Band, bool) {
	if groupCold(g) {
		if t.in.FullRotRateC <= 0 {
			return Band{}, false
		}
		return Band{MinC: math.Inf(-1), MaxC: t.in.FullRotRateC}, true
	}
	return t.in.Catalog.Climate.Living, true
}

// heldAt is the temperature a room is held at, as a neighbour: the top of a
// band with no floor, else the heater's default target.
func (t *tiler) heldAt(b Band) float64 {
	if math.IsInf(b.MinC, -1) {
		return b.MaxC
	}
	return t.in.Catalog.Climate.HeldC
}

func (t *tiler) roomCells(i int) int { return int(t.rooms[i].rect.Width * t.rooms[i].rect.Height) }

// wallDemand is the wall stations the room's group will set into its ring.
func (t *tiler) wallDemand(g RoomGroup) int {
	n := 0
	for _, s := range g.Stations {
		if st, ok := t.in.Catalog.Station(s.Def); ok && st.Access == AccessWall {
			n += s.Count
		}
	}
	return n
}

// ventCandidates are the wall cells of room i that lead outdoors: inside, wall
// and outside in line, the outside cell open ground, the cell no door of the
// room and no vent holds.
func (t *tiler) ventCandidates(i int, used map[domain.Cell]bool) []domain.Cell {
	in := t.rooms[i].rect
	doors := t.doorsOf(i)
	var out []domain.Cell
	add := func(wall, outside domain.Cell) {
		if t.free[outside] && !used[wall] && !slices.Contains(doors, wall) {
			out = append(out, wall)
		}
	}
	for x := in.X; x < in.X+in.Width; x++ {
		add(domain.Cell{X: x, Z: in.Z - 1}, domain.Cell{X: x, Z: in.Z - 2})
		add(domain.Cell{X: x, Z: in.Z + in.Height}, domain.Cell{X: x, Z: in.Z + in.Height + 1})
	}
	for z := in.Z; z < in.Z+in.Height; z++ {
		add(domain.Cell{X: in.X - 1, Z: z}, domain.Cell{X: in.X - 2, Z: z})
		add(domain.Cell{X: in.X + in.Width, Z: z}, domain.Cell{X: in.X + in.Width + 1, Z: z})
	}
	sortCells(out)
	return out
}

// linkCandidates are the cells of the wall room pair k shares that stand beside
// its link door and are neither a door nor a vent.
func (t *tiler) linkCandidates(k [2]int, used map[domain.Cell]bool) []domain.Cell {
	door := t.links[k]
	doors := append(t.doorsOf(k[0]), t.doorsOf(k[1])...)
	var out []domain.Cell
	for _, c := range t.adj[k] {
		if abs32(c.X-door.X)+abs32(c.Z-door.Z) == 1 && !used[c] && !slices.Contains(doors, c) {
			out = append(out, c)
		}
	}
	sortCells(out)
	return out
}

// linked are the rooms room r has a link door to, in room order.
func (t *tiler) linked(r int) []int {
	var out []int
	for k := range t.links {
		if k[0] == r || k[1] == r {
			out = append(out, k[0]+k[1]-r)
		}
	}
	sort.Ints(out)
	return out
}

// ventPricer prices rooms in the tiler's climate (see the file comment): a
// room's mean power with its doors closed or one held open, the vents placed
// so far and the new vents asked of it.
type ventPricer struct {
	t      *tiler
	m      ClimateModel
	curve  []float64
	failed error
	memo   map[string]float64
}

func (p *ventPricer) band(r int) Band {
	if b, ok := p.t.ventBand(p.t.in.Groups[p.t.rooms[r].group]); ok {
		return b
	}
	return p.m.Living
}

// side is room r as a neighbour: held where its band holds it.
func (p *ventPricer) side(r int) ClimateSide {
	return ClimateSide{HeldC: p.t.heldAt(p.band(r)), Cells: p.t.roomCells(r)}
}

// watts is room r's mean power. open is the door held open (-2 none, -1 the
// exterior door, else the neighbour's room index); extra is the new vents toward
// each room (-1 the outdoors).
func (p *ventPricer) watts(r, open int, extra map[int]int) float64 {
	key := fmt.Sprint(r, open, extra)
	if w, ok := p.memo[key]; ok {
		return w
	}
	t := p.t
	room := ClimateRoom{Cells: t.roomCells(r), Band: p.band(r), Heat: t.in.Catalog.GroupHeat(t.in.Groups[t.rooms[r].group])}
	door := func(who int, side ClimateSide) ClimateLink {
		if who == open {
			return ClimateLink{Opening: OpeningDoorOpen, Side: side}
		}
		return ClimateLink{Opening: OpeningDoorClosed, Side: side}
	}
	outdoors := ClimateSide{Outdoors: true}
	if t.extDoor[r] != nil {
		room.Links = append(room.Links, door(-1, outdoors))
	}
	for _, j := range t.linked(r) {
		room.Links = append(room.Links, door(j, p.side(j)))
	}
	vent := func(side ClimateSide) { room.Links = append(room.Links, ClimateLink{Opening: OpeningVent, Side: side}) }
	for _, v := range t.vents {
		switch {
		case v.room == r && v.other < 0:
			vent(outdoors)
		case v.room == r:
			vent(p.side(v.other))
		case v.other == r:
			vent(p.side(v.room))
		}
	}
	for to, count := range extra {
		for range count {
			if to < 0 {
				vent(outdoors)
			} else {
				vent(p.side(to))
			}
		}
	}
	w, err := ClimateLoad(p.m, p.curve, room, 0)
	if err != nil {
		p.failed = err
		return math.Inf(1)
	}
	p.memo[key] = w.MeanW
	return w.MeanW
}

// chooseVents places the vents of every fenced room (see the file comment).
func (t *tiler) chooseVents() {
	m := t.in.Catalog.Climate
	curve, known := t.in.OutdoorCurve.Value()
	if !known || !m.HasLiving() || !finitePositive(t.in.SteelValue) {
		t.note(-1, -1, TileUnsatisfied, ReasonTileVentClimateUnknown)
		return
	}
	available, _ := t.in.VentAvailable.Value()
	ventW := BuildW(DefaultSiteTypeWeights(), m.Build.Vent, t.in.SteelValue)
	p := &ventPricer{t: t, m: m, curve: curve, memo: map[string]float64{}}
	used := map[domain.Cell]bool{}
	for i, r := range t.rooms {
		if t.open(i) || t.plan.Rooms[i].Role.IsOutdoor() {
			continue
		}
		g := t.in.Groups[r.group]
		if _, ok := t.ventBand(g); !ok {
			t.note(r.group, -1, TileUnsatisfied, ReasonTileVentClimateUnknown)
			continue
		}
		// The channels: the outdoors, then each link door's neighbour.
		var channels []ventChannel
		if cells := t.ventCandidates(i, used); len(cells) > 0 {
			cells = cells[:max(0, len(cells)-t.wallDemand(g))]
			if len(cells) > 0 {
				channels = append(channels, ventChannel{cells: cells, other: -1})
			}
		}
		neighbours := t.linked(i)
		for _, j := range neighbours {
			if cells := t.linkCandidates(pairOf(i, j), used); len(cells) > 0 {
				channels = append(channels, ventChannel{cells: cells, other: j})
			}
		}
		if len(channels) == 0 {
			continue
		}
		// total is what the room and the rooms beside it pay in watts, with n new
		// vents on each channel and a door held open: a vent between two rooms
		// loads both, and a room beside it that must be cooled for the heat the
		// vent brings it is no sink.
		total := func(n []int, open int) float64 {
			extra := map[int]int{}
			vents := 0
			for c, count := range n {
				if count > 0 {
					extra[channels[c].other] = count
				}
				vents += count
			}
			w := p.watts(i, open, extra) + float64(vents)*ventW
			for _, j := range neighbours {
				oj := -2
				if open == j {
					oj = i
				}
				w += p.watts(j, oj, map[int]int{i: extra[j]})
			}
			return w
		}
		none := make([]int, len(channels))
		doorsOnly := total(none, -2)
		if t.extDoor[i] != nil {
			doorsOnly = min(doorsOnly, total(none, -1))
		}
		for _, j := range neighbours {
			doorsOnly = min(doorsOnly, total(none, j))
		}
		// Coordinate descent over the vent counts: a few passes settle it, as each
		// channel's cost falls then rises with its count.
		n := slices.Clone(none)
		for pass := 0; pass < 3; pass++ {
			changed := false
			for c := range channels {
				held := n[c]
				best, bestCost, prev := held, total(n, -2), math.Inf(1)
				for count := 0; count <= len(channels[c].cells); count++ {
					n[c] = count
					cost := total(n, -2)
					if cost < bestCost-1e-9 {
						best, bestCost = count, cost
					}
					if count > 0 && cost > prev {
						break
					}
					prev = cost
				}
				changed = changed || best != held
				n[c] = best
			}
			if !changed {
				break
			}
		}
		if p.failed != nil {
			t.note(r.group, -1, TileUnsatisfied, ReasonTileVentClimateUnknown)
			p.failed = nil
			continue
		}
		wanted := 0
		for _, count := range n {
			wanted += count
		}
		if wanted == 0 || total(n, -2) >= doorsOnly-1e-9 {
			t.note(r.group, -1, TileAbsent, ReasonTileVentNotCheaper)
			continue
		}
		if !available {
			t.note(r.group, -1, TileUnsatisfied, ReasonTileVentUnavailable)
			continue
		}
		for c, count := range n {
			for _, cell := range channels[c].cells[:count] {
				used[cell] = true
				reason, other := ReasonTileVent, -1
				if channels[c].other >= 0 {
					reason, other = ReasonTileVentLink, t.rooms[channels[c].other].group
				}
				t.vents = append(t.vents, tileVent{cell: cell, room: i, other: channels[c].other, reason: reason})
				t.note(r.group, other, TilePlace, reason)
			}
		}
	}
}

// ventSites are the placed vents as plan nodes, in cell order.
func (t *tiler) ventSites() []PlannedSite {
	vents := slices.Clone(t.vents)
	sort.Slice(vents, func(a, b int) bool { return cellLess(vents[a].cell, vents[b].cell) })
	out := make([]PlannedSite, 0, len(vents))
	for _, v := range vents {
		site := newSite(fmt.Sprintf("vent_%d_%d", v.cell.X, v.cell.Z), NodeVent, RectRegion(Rectangle{X: v.cell.X, Z: v.cell.Z, Width: 1, Height: 1}), v.reason)
		site.Rotation, _ = doorSide(t.rooms[v.room].rect, v.cell)
		out = append(out, site)
	}
	return out
}
