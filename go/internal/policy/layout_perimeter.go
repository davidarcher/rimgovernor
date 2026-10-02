package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Perimeter, gates and killbox from terrain (#781, A5). The wall is a
// 3-thick ring traced around the core and the whole field patches beside
// it (#1286, layout_perimeter_enclosure.go), never across a patch; a
// stretch where rock or deep water already fills the whole thickness is
// left to the terrain. Soft ground a raider wades through (marsh, mud,
// shallow and moving water) is closed too (#949): the wall follows the
// shore inside the ring when that costs at most perimeterDetour times the
// straight stretch, and otherwise crosses it, a wooden wall on light
// footing, a bridge under the wall on water. Raider approaches from the map
// edges are traced with the defense planner's flood (defense_arrivals.go),
// and the dry ring position most of them cross becomes the one opening: a
// killbox behind it (its snake, kill zone and turrets are the defense
// layout's, #1544), and a bent approach lane outside.
// Airlock gates (a door on each face, #1060) stand along the dry ring. A
// cover-clear band runs 30 cells out, and the mortar spot is the firm cell
// farthest from the wall. Moisture pump sites inside the wall cover the soft
// ring cells a pump dries (#954), so a later re-survey straightens the wall.
// The colony's own buildings read as the ground under them, and a replan
// keeps the opening while it still opens: the defense layout is anchored
// on it.

// Reservation kinds A5 adds beside A1's.
const (
	ReserveKillboxApproach ReservationKind = "killbox_approach"
	// ReservePerimeterLight marks wall cells on light footing: the wall
	// there is wood.
	ReservePerimeterLight ReservationKind = "perimeter_light"
	// ReserveBridge marks wall cells on water: a bridge goes down first,
	// then the wall on it (wood on a plain bridge, stone on a heavy one).
	ReserveBridge ReservationKind = "perimeter_bridge"
	// ReservePerimeterGap marks walkable ring cells nothing can close.
	ReservePerimeterGap ReservationKind = "perimeter_gap"
	// ReserveMoisturePump marks a moisture pump site: soft ring cells it
	// dries lie within pumpRadius (#954).
	ReserveMoisturePump ReservationKind = "perimeter_pump"
)

// perimeterKinds are the reservations PlanPerimeter owns.
var perimeterKinds = map[ReservationKind]bool{ReservePerimeter: true, ReservePerimeterLight: true, ReserveBridge: true, ReservePerimeterGap: true, ReserveMoisturePump: true, ReserveGate: true, ReserveKillbox: true, ReserveKillboxApproach: true, ReserveCoverClear: true, ReserveMortar: true, ReservePocketWall: true, ReserveBaitRoom: true, ReserveBaitWall: true}

const (
	perimeterThick int32 = 3
	// perimeterGap is the yard between the core and the wall; it holds
	// the killbox and two cells behind its back wall.
	perimeterGap = killboxDepth + 2
	// perimeterFieldReach is how far from the core any cell of a field
	// patch may lie and still take the whole patch inside the wall.
	perimeterFieldReach int32 = 15
	perimeterGatePitch  int32 = 20
	// perimeterStep is the block a patch's part of the enclosure is
	// squared off in, so a diagonal edge steps in gateable sides.
	perimeterStep      int32 = 10
	perimeterCoverBand int32 = 30
	killboxHalf        int32 = 7
	killboxDepth             = killboxRows
	approachLeg        int32 = 8
	// perimeterDetour caps a shoreline detour's wall at this many times
	// the straight stretch it replaces (#949).
	perimeterDetour = 1.5
	// pumpRadiusSq is a moisture pump's reach, 6.9 cells, squared.
	pumpRadiusSq = 47
)

// PlanPerimeter adds the wall, gates, killbox, approach,
// cover-clear band and mortar spot to plan (which needs its core rooms),
// replacing any it held. A plan without rooms comes back unchanged.
func PlanPerimeter(plan LayoutPlan, s MapSurvey) LayoutPlan {
	w, h := s.Bounds.Width, s.Bounds.Height
	if len(plan.Rooms) == 0 || w < 1 || h < 1 {
		return plan
	}
	cells := map[domain.Cell]SurveyCell{}
	for _, c := range s.Cells {
		cells[c.Cell] = c
	}
	impassable := func(c domain.Cell) bool {
		sc, ok := cells[c]
		return ok && (sc.Rock || !sc.Walkable && !sc.Built)
	}
	soft := func(c domain.Cell) bool {
		sc, ok := cells[c]
		return ok && sc.Soft()
	}

	// The core box: rooms with walls and the hallway, then nearby fields.
	var core Rectangle
	grow := func(r Rectangle) {
		if core.Width == 0 {
			core = r
			return
		}
		x0, z0 := min(core.X, r.X), min(core.Z, r.Z)
		x1, z1 := max(core.X+core.Width, r.X+r.Width), max(core.Z+core.Height, r.Z+r.Height)
		core = Rectangle{X: x0, Z: z0, Width: x1 - x0, Height: z1 - z0}
	}
	for _, r := range plan.AllRooms() {
		grow(pad(r.Interior, 1))
	}
	for _, sg := range plan.Hallways() {
		grow(pad(rectOf(sg.From, sg.To), SpineWidth/2))
	}
	// The enclosure: the core box and whole field patches, the yard
	// around them; the ring traced outside it (#1286).
	rich := map[domain.Cell]bool{}
	for _, c := range s.Cells {
		if c.Fertility > zoneRichFertility {
			rich[c.Cell] = true
		}
	}
	enc := planEnclosure(plan, core, w, h, func(c domain.Cell) bool { return rich[c] })
	if enc.bbox.Width == 0 {
		return plan
	}
	sides, owner := enc.sides()
	inRing := enc.onRing
	sideOf := func(c domain.Cell) (int, int32) {
		if x, ok := owner[c]; ok {
			return x.side, x.pos
		}
		return -1, 0
	}
	// orphans are ring cells no side position holds (outline corners too
	// tight for a full column); each is walled on its own.
	var orphans []domain.Cell
	for _, c := range enc.ringCells() {
		if _, ok := owner[c]; !ok {
			orphans = append(orphans, c)
		}
	}
	ringBox := pad(enc.bbox, perimeterThick) // built is a cell the core's rooms or hallway hold.
	built := func(c domain.Cell) bool {
		for _, r := range plan.AllRooms() {
			if contains(pad(r.Interior, 1), c) {
				return true
			}
		}
		for _, sg := range plan.Hallways() {
			if contains(pad(rectOf(sg.From, sg.To), SpineWidth/2), c) {
				return true
			}
		}
		return false
	}

	// Soft ground on the ring (#949). Each connected stretch of it is
	// closed by a shoreline detour inside the ring where that stays short,
	// else crossed where the ring runs.
	wet := map[crossing]bool{}
	for k, sd := range sides {
		for p := sd.lo; p <= sd.hi; p++ {
			for t := int32(0); t < perimeterThick; t++ {
				if soft(sd.cell(p, t)) {
					wet[crossing{k, p}] = true
				}
			}
		}
	}
	box := clipRect(pad(ringBox, perimeterGap), Rectangle{Width: w, Height: h})
	detour := map[domain.Cell]bool{}  // wall cells of kept detours
	shut := map[domain.Cell]bool{}    // soft cells a kept detour leaves outside
	detoured := map[crossing]bool{}   // wet positions a kept detour closes
	failed := map[crossing]bool{}     // wet positions some stretch cannot detour
	flooded := map[domain.Cell]bool{} // soft cells already in some stretch
	for _, start := range enc.ringCells() {
		if !soft(start) || flooded[start] {
			continue
		}
		// The stretch: soft cells joined to this one inside box,
		// diagonals included, since a pawn steps diagonally.
		stretch, queue := []domain.Cell{start}, []domain.Cell{start}
		flooded[start] = true
		for len(queue) > 0 {
			c := queue[0]
			queue = queue[1:]
			for _, d := range neighbours8 {
				n := addCell(c, d)
				if contains(box, n) && soft(n) && !flooded[n] {
					flooded[n] = true
					stretch = append(stretch, n)
					queue = append(queue, n)
				}
			}
		}
		crossed := map[crossing]bool{}
		in := map[domain.Cell]bool{}
		for _, c := range stretch {
			in[c] = true
			if kk, pp := sideOf(c); kk >= 0 {
				crossed[crossing{kk, pp}] = true
			}
		}
		// The detour: every cell inside the ring within the wall's
		// thickness of the stretch. It fails on a room, the
		// hallway, an unread cell or other soft ground.
		wall, ok := map[domain.Cell]bool{}, true
		for _, c := range stretch {
			if enc.inside(c) && built(c) {
				ok = false
			}
			for dx := -perimeterThick; dx <= perimeterThick && ok; dx++ {
				for dz := -perimeterThick; dz <= perimeterThick && ok; dz++ {
					n := domain.Cell{X: c.X + dx, Z: c.Z + dz}
					if !enc.inside(n) && !inRing(n) || in[n] || wall[n] || impassable(n) {
						continue
					}
					if _, read := cells[n]; !read || built(n) || soft(n) {
						ok = false
					}
					wall[n] = true
				}
			}
		}
		extra := 0
		for c := range wall {
			if kk, pp := sideOf(c); kk < 0 || crossed[crossing{kk, pp}] {
				extra++
			}
		}
		if !ok || float64(extra) > perimeterDetour*float64(perimeterThick)*float64(len(crossed)) {
			for x := range crossed {
				failed[x] = true
			}
			continue
		}
		for c := range wall {
			detour[c] = true
		}
		for _, c := range stretch {
			shut[c] = true
		}
		for x := range crossed {
			detoured[x] = true
		}
	}

	// An opening stands on dry ring positions, its killbox clear of any
	// detour.
	openingAt := func(x crossing) (killbox Rectangle, at func(in, al int32) domain.Cell) {
		sd := sides[x.side]
		b := sd.base(x.pos)
		at = func(in, al int32) domain.Cell {
			return domain.Cell{X: b.X + sd.in.X*in + sd.al.X*al, Z: b.Z + sd.in.Z*in + sd.al.Z*al}
		}
		return clipRect(rectOf(at(perimeterThick, -killboxHalf), at(perimeterThick+killboxDepth-1, killboxHalf)), enc.bbox), at
	}
	// fields are the plan's field cells: a killbox on one would build over
	// crops, so an opening off the patches is preferred (#1287).
	fields := map[domain.Cell]bool{}
	for _, z := range plan.Zones {
		if z.Kind == ZoneField {
			for _, r := range z.Runs {
				for x := r.X; x < r.X+r.Length; x++ {
					fields[domain.Cell{X: x, Z: r.Z}] = true
				}
			}
		}
	}
	opens := func(x crossing, strict bool) bool {
		sd := sides[x.side]
		if x.pos < sd.lo+1 || x.pos > sd.hi-1 {
			return false
		}
		for p := x.pos - 1; p <= x.pos+1; p++ {
			if wet[crossing{x.side, p}] {
				return false
			}
			for t := int32(0); t < perimeterThick; t++ {
				if detour[sd.cell(p, t)] {
					return false
				}
			}
		}
		killbox, _ := openingAt(x)
		for _, c := range rectCells(killbox) {
			if !enc.inside(c) || strict && fields[c] {
				return false
			}
		}
		for c := range detour {
			if contains(killbox, c) {
				return false
			}
		}
		for c := range shut {
			if contains(killbox, c) {
				return false
			}
		}
		// Raiders fight through the killbox: no utility stands in it.
		for _, r := range plan.Reservations {
			if !perimeterKinds[r.Kind] && rectsOverlap(killbox, r.Area) {
				return false
			}
		}
		return true
	}
	// snap moves a crossing to the nearest position on its side that
	// opens: off the fields within half a killbox, else the nearest at all,
	// on a patch rather than no opening.
	snap := func(x crossing) (crossing, bool) {
		sd := sides[x.side]
		for _, strict := range []bool{true, false} {
			reach := sd.hi - sd.lo
			if strict {
				reach = min(reach, killboxHalf)
			}
			for d := int32(0); d <= reach; d++ {
				for _, p := range []int32{x.pos - d, x.pos + d} {
					if y := (crossing{x.side, p}); opens(y, strict) {
						return y, true
					}
				}
			}
		}
		return crossing{}, false
	}

	// Approaches: every edge sector's shortest route to the core, through
	// the defense flood; the ring position most routes cross opens.
	site := defenseSite{r: DefenseRequest{Bounds: s.Bounds, Region: Rectangle{Width: w, Height: h}}, cells: map[domain.Cell]DefenseCell{}}
	for _, c := range s.Cells {
		pass := !impassable(c.Cell)
		row := DefenseCell{Cell: c.Cell, Passable: domain.Known(pass)}
		if pass && site.onBorder(c.Cell) {
			row.EdgeReachable = domain.Known(true)
		}
		site.cells[c.Cell] = row
	}
	mid := plan.Rooms[0].Door
	if len(plan.Spine) > 0 {
		sg := plan.Spine[0]
		mid = domain.Cell{X: (sg.From.X + sg.To.X) / 2, Z: (sg.From.Z + sg.To.Z) / 2}
	}
	origin, found := mid, false
	for x := enc.bbox.X; x < enc.bbox.X+enc.bbox.Width; x++ {
		for z := enc.bbox.Z; z < enc.bbox.Z+enc.bbox.Height; z++ {
			c := domain.Cell{X: x, Z: z}
			if enc.inside(c) && site.passable(c) && (!found || squaredDistance(c, mid) < squaredDistance(origin, mid)) {
				origin, found = c, true
			}
		}
	}
	type meet struct {
		at   crossing
		dist int
	}
	var meets []meet
	if found {
		sectors, _ := site.sectors(origin)
		_, dist := site.paths(origin, nil)
		for _, sec := range sectors {
			best, ok := domain.Cell{}, false
			for _, c := range sec.Edge {
				if d, in := dist[c]; in && (!ok || d < dist[best]) {
					best, ok = c, true
				}
			}
			if !ok {
				continue
			}
			// The first ring cell on some shortest route, nearest the
			// edge: a raider meets the wall there.
			_, fromEdge := site.paths(best, nil)
			hit, hitOK := domain.Cell{}, false
			for c, de := range fromEdge {
				if do, in := dist[c]; !in || de+do != dist[best] || !inRing(c) {
					continue
				}
				if !hitOK || de < fromEdge[hit] || de == fromEdge[hit] && defenseCellLess(c, hit) {
					hit, hitOK = c, true
				}
			}
			if !hitOK {
				continue
			}
			if k, p := sideOf(hit); k >= 0 {
				meets = append(meets, meet{crossing{k, p}, dist[best]})
			}
		}
	}
	open, opened := crossing{}, false
	votes, length := map[crossing]int{}, map[crossing]int{}
	for _, m := range meets {
		if x, ok := snap(m.at); ok {
			votes[x]++
			length[x] += m.dist
		}
	}
	// The opening already planned stays while it still opens.
	for _, r := range plan.Reservations {
		if r.Kind != ReserveKillbox {
			continue
		}
		for k, sd := range sides {
			for p := sd.lo; p <= sd.hi && !opened; p++ {
				if kb, _ := openingAt(crossing{k, p}); kb == r.Area && opens(crossing{k, p}, false) {
					open, opened = crossing{k, p}, true
				}
			}
		}
	}
	pinned := opened
	for x, v := range votes {
		if pinned {
			break
		}
		o := votes[open]
		if !opened || v > o || v == o && (length[x] < length[open] || length[x] == length[open] && (x.side < open.side || x.side == open.side && x.pos < open.pos)) {
			open, opened = x, true
		}
	}

	var res []LayoutReservation
	add := func(kind ReservationKind, r Rectangle) {
		if r = clipRect(r, Rectangle{Width: w, Height: h}); r.Width > 0 && r.Height > 0 {
			res = append(res, LayoutReservation{Kind: kind, Area: r})
		}
	}
	var killbox Rectangle
	if opened {
		var at func(in, al int32) domain.Cell
		killbox, at = openingAt(open)
		add(ReserveKillbox, killbox)
		// The approach leaves the opening straight out, then turns along
		// the wall, so the lane breaks line of sight into the killbox.
		add(ReserveKillboxApproach, rectOf(at(-1, -1), at(-approachLeg, 1)))
		add(ReserveKillboxApproach, rectOf(at(-approachLeg+2, 2), at(-approachLeg, approachLeg+1)))
	}

	// axes are where the hallways, run straight on, meet sd: the east-west
	// ones on the east and west sides, the crossings on the others.
	axes := func(sd ringSide) []int32 {
		var out []int32
		for _, sg := range plan.Spine {
			if alongX(sg) == sd.vertical {
				out = append(out, sd.pos(sg.From))
			}
		}
		return out
	}
	// Wall runs and gates along the dry ring, side by side. A run shorter
	// than the pitch (a step of a squared-off diagonal) takes its gate
	// only a pitch clear of every other, so a staircase is gated about as
	// often as a straight side (#1287).
	var gates, stepGates []Rectangle
	for k, sd := range sides {
		start := int32(-1)
		flush := func(end int32) {
			if start < 0 {
				return
			}
			add(ReservePerimeter, rectOf(sd.base(start), sd.cell(end, perimeterThick-1)))
			n := end - start + 1
			if n >= 5 {
				// The pitch runs from a hallway's axis when one meets
				// this run, so a gate lines up with it (#952).
				first := start + min(perimeterGatePitch/2, n/2)
				for _, a := range axes(sd) {
					if a > start && a < end {
						first = start + (a-start)%perimeterGatePitch
						break
					}
				}
				for p := first; p <= end; p += perimeterGatePitch {
					g := rectOf(sd.base(p), sd.cell(p, perimeterThick-1))
					if n < perimeterGatePitch {
						stepGates = append(stepGates, g)
					} else {
						gates = append(gates, g)
					}
				}
			}
			start = -1
		}
		for p := sd.lo; p <= sd.hi; p++ {
			gap := opened && k == open.side && p >= open.pos-1 && p <= open.pos+1
			terrain := true
			for t := int32(0); t < perimeterThick; t++ {
				terrain = terrain && impassable(sd.cell(p, t))
			}
			if gap || terrain || wet[crossing{k, p}] {
				flush(p - 1)
			} else if start < 0 {
				start = p
			}
		}
		flush(sd.hi)
	}
	for _, g := range stepGates {
		clear := true
		for _, o := range gates {
			clear = clear && max(o.X-g.X, g.X-o.X, o.Z-g.Z, g.Z-o.Z) >= perimeterGatePitch
		}
		if clear {
			gates = append(gates, g)
		}
	}
	for _, g := range gates {
		add(ReserveGate, g)
	}
	// Soft stretches: a detour's wall, else the ring crossing them.
	walls, light, bridges, gaps := map[domain.Cell]bool{}, map[domain.Cell]bool{}, map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for c := range detour {
		if k, p := sideOf(c); k < 0 || wet[crossing{k, p}] {
			walls[c] = true
		}
	}
	closeCell := func(c domain.Cell) {
		sc, read := cells[c]
		switch {
		case impassable(c):
		case !read || sc.Footing == FootingFirm:
			walls[c] = true
		case sc.Footing == FootingLight:
			walls[c], light[c] = true, true
		case sc.Bridgeable:
			walls[c], bridges[c] = true, true
		default:
			gaps[c] = true
		}
	}
	for x := range wet {
		if detoured[x] && !failed[x] {
			continue
		}
		for t := int32(0); t < perimeterThick; t++ {
			closeCell(sides[x.side].cell(x.pos, t))
		}
	}
	// Orphan ring cells close one by one, but for soft ground a kept
	// detour leaves outside.
	for _, c := range orphans {
		if !shut[c] {
			closeCell(c)
		}
	}
	for kind, set := range map[ReservationKind]map[domain.Cell]bool{ReservePerimeter: walls, ReservePerimeterLight: light, ReserveBridge: bridges, ReservePerimeterGap: gaps} {
		for _, r := range cellRects(set) {
			add(kind, r)
		}
	}

	// Moisture pumps (#954): sites inside the wall, greedily covering the
	// soft ring cells a pump dries, each within pumpRadiusSq of its pump.
	dries := map[domain.Cell]bool{}
	for _, c := range enc.ringCells() {
		if soft(c) && cells[c].Dries {
			dries[c] = true
		}
	}
	// A site already planned stays while it still dries something, the
	// pump standing there (Built) or not: a re-survey never moves a pump.
	pumpSite := func(c domain.Cell, kept bool) bool {
		sc, ok := cells[c]
		return ok && enc.inside(c) && (sc.Walkable && !sc.Built || kept && sc.Built) && !sc.Rock && sc.Footing == FootingFirm && !built(c) && !contains(killbox, c) && !detour[c] && !shut[c]
	}
	pumps := map[domain.Cell]bool{}
	drying := func(pump domain.Cell) int {
		n := 0
		for d := range dries {
			if squaredDistance(pump, d) <= pumpRadiusSq {
				n++
			}
		}
		return n
	}
	cover := func(pump domain.Cell) {
		pumps[pump] = true
		for d := range dries {
			if squaredDistance(pump, d) <= pumpRadiusSq {
				delete(dries, d)
			}
		}
	}
	for _, r := range plan.Reservations {
		if c := (domain.Cell{X: r.Area.X, Z: r.Area.Z}); r.Kind == ReserveMoisturePump && pumpSite(c, true) && drying(c) > 0 {
			cover(c)
		}
	}
	candidates := map[domain.Cell]bool{}
	for c := range dries {
		for dx := int32(-6); dx <= 6; dx++ {
			for dz := int32(-6); dz <= 6; dz++ {
				if n := (domain.Cell{X: c.X + dx, Z: c.Z + dz}); dx*dx+dz*dz <= pumpRadiusSq && pumpSite(n, false) {
					candidates[n] = true
				}
			}
		}
	}
	for len(dries) > 0 {
		best, most := domain.Cell{}, 0
		for c := range candidates {
			if n := drying(c); n > most || n == most && n > 0 && defenseCellLess(c, best) {
				best, most = c, n
			}
		}
		if most == 0 {
			break
		}
		delete(candidates, best)
		cover(best)
	}
	for _, c := range sortedCells(pumps) {
		add(ReserveMoisturePump, rectOf(c, c))
	}

	// Cover-clear band: everything within perimeterCoverBand outside the
	// ring.
	band := map[domain.Cell]bool{}
	for i, v := range enc.dist {
		if v > perimeterThick {
			band[domain.Cell{X: int32(i) % w, Z: int32(i) / w}] = true
		}
	}
	for _, r := range cellRects(band) {
		add(ReserveCoverClear, r)
	}

	// Mortar: the firm cell deepest inside the wall, off rooms, hallway,
	// killbox and detours.
	outside := make([]bool, len(enc.in))
	for i, v := range enc.in {
		outside[i] = !v
	}
	deep := chebyshevField(w, h, outside, w+h)
	mortar, depth := domain.Cell{}, int32(-1)
	for z := enc.bbox.Z; z < enc.bbox.Z+enc.bbox.Height; z++ {
		for x := enc.bbox.X; x < enc.bbox.X+enc.bbox.Width; x++ {
			c := domain.Cell{X: x, Z: z}
			sc, ok := cells[c]
			if !ok || !enc.inside(c) || !sc.Walkable || sc.Rock || sc.Footing != FootingFirm || built(c) || contains(killbox, c) || detour[c] || pumps[c] {
				continue
			}
			if d := deep[z*w+x]; d > depth {
				mortar, depth = c, d
			}
		}
	}
	if depth >= 0 {
		add(ReserveMortar, rectOf(mortar, mortar))
	}

	kept := plan.Reservations[:0:0]
	for _, r := range plan.Reservations {
		if !perimeterKinds[r.Kind] {
			kept = append(kept, r)
		}
	}
	sort.SliceStable(res, func(i, j int) bool { return res[i].Kind < res[j].Kind })
	plan.Reservations = append(kept, res...)
	return plan
}

// neighbours8 are the eight cells around one.
var neighbours8 = [8]domain.Cell{{X: -1, Z: -1}, {X: 0, Z: -1}, {X: 1, Z: -1}, {X: -1}, {X: 1}, {X: -1, Z: 1}, {X: 0, Z: 1}, {X: 1, Z: 1}}

// ringSide is one side of the wall ring: positions lo..hi along al, the
// outer face at base(p), thickness toward in.
type ringSide struct {
	in, al   domain.Cell
	lo, hi   int32
	face     int32 // the outer face's row (south/north) or column (east/west)
	vertical bool
}

func (sd ringSide) base(p int32) domain.Cell {
	if sd.vertical {
		return domain.Cell{X: sd.face, Z: p}
	}
	return domain.Cell{X: p, Z: sd.face}
}

func (sd ringSide) cell(p, t int32) domain.Cell {
	b := sd.base(p)
	return domain.Cell{X: b.X + sd.in.X*t, Z: b.Z + sd.in.Z*t}
}

func (sd ringSide) pos(c domain.Cell) int32 {
	if sd.vertical {
		return c.Z
	}
	return c.X
}

func rectOf(a, b domain.Cell) Rectangle {
	x0, z0 := min(a.X, b.X), min(a.Z, b.Z)
	return Rectangle{X: x0, Z: z0, Width: max(a.X, b.X) - x0 + 1, Height: max(a.Z, b.Z) - z0 + 1}
}

func pad(r Rectangle, n int32) Rectangle {
	return Rectangle{X: r.X - n, Z: r.Z - n, Width: r.Width + 2*n, Height: r.Height + 2*n}
}

func clipRect(r, to Rectangle) Rectangle {
	x0, z0 := max(r.X, to.X), max(r.Z, to.Z)
	x1, z1 := min(r.X+r.Width, to.X+to.Width), min(r.Z+r.Height, to.Z+to.Height)
	if x1 <= x0 || z1 <= z0 {
		return Rectangle{}
	}
	return Rectangle{X: x0, Z: z0, Width: x1 - x0, Height: z1 - z0}
}

func contains(r Rectangle, c domain.Cell) bool {
	return c.X >= r.X && c.Z >= r.Z && c.X < r.X+r.Width && c.Z < r.Z+r.Height
}
