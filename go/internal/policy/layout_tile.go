package policy

import (
	"errors"
	"fmt"
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Tiling turns the embedding (layout_embed.go) into a LayoutPlan: one room per
// placed group on the group's block, a door graph over the walls the blocks
// share, an exterior door and entrance per room, and the group's stations set
// into the room by PlaceInRegion. It is a pure function of the embedding and
// the survey; the old planner stays authoritative and nothing calls it unless
// StationFacts.Tile is set. No constant is new:
//
//   - Role. A group's PlannedRole comes from what the game makes of its
//     stations (the group's predicted native role, PlannedRoleOf) and its cold
//     need: a storeroom group that stores cold is the freezer. A group whose
//     role no planned room carries (barracks, the generic room) stops the tiler
//     with ErrTileRole; no table fills the gap.
//   - Blocks. The embedding's rectangles are the rooms; walls are the ring
//     around them, shared where two blocks stand a wall apart.
//   - Links. A door joins two blocks that share a wall when the groups have a
//     required link (first, their area counts it), and for the heaviest flows
//     otherwise as a maximum spanning forest over the adjacent linked pairs
//     (each flow weight is the clustering's, movements a day), so no loop of
//     doors opens shortcuts. Flow doors come last, after the exterior and
//     sealed-room doors, and only where the placer still furnishes both
//     rooms. The door sits in the middle of the shared run when the placer
//     furnishes both rooms behind it, else at the first cell of the run that
//     does (pickLink).
//   - Exterior door. Every room gets one door onto open ground when its wall
//     has a cell whose outside neighbour is free, buildable and joined to the
//     map's largest free area, the one nearest the middle of the blocks. The
//     outside cell is the entrance. A room with no such cell is sealed in by
//     its neighbours and is entered through a link door instead; a group of
//     sealed rooms is joined to a component that has an exterior door.
//   - Pass-through. Each room carries what crossing it costs (RoomCrossing,
//     read from its group's stations: cold, player-faction beds, cleanliness
//     stations). A room that costs nothing is walked freely; a cold room is
//     never crossed; any other may not lie on a path between two other rooms.
//     CheckRoutes finds a violation; the tiler then drops the
//     crossed room's lightest link door that leaves every room still joined to
//     an exterior door, required links last, and checks again. When no door
//     can go, a room that MayBeThrough is marked Through (a decision,
//     ReasonTilePassThrough) and walked through; a sealed room is joined to the
//     neighbour crossing costs least (throughRank).
//   - Vents (layout_tile_vent.go). After the routes are kept, a vent is a 1x1
//     NodeVent site in a wall cell facing open ground, placed where the climate
//     model prices it under holding the doors alone (build cost included).
//   - Trips. The plan carries the group flows as LayoutPlan.Trips between its
//     rooms; plan scoring walks them (tripWalk), not the role-pair table.
//
// Hallways are not planned here: they need ground between the blocks, which
// only the embedding can reserve. A plan with hallways is scored and routed as
// before, and the tiler needs none to open a packed base (#2879).

// Verdicts and reasons of tiling decisions.
const (
	TilePlace       = "place"
	TileUnsatisfied = "unsatisfied"

	ReasonTileRoom         = "tile_room"
	ReasonTileRoomBuilt    = "tile_room_built"
	ReasonTileRoomMoved    = "tile_room_successor"
	ReasonTileLinkDoor     = "tile_link_door"
	ReasonTileExteriorDoor = "tile_exterior_door"
	ReasonTileSealedRoom   = "tile_sealed_room"
	ReasonTileLinkDropped  = "tile_link_dropped_thoroughfare"
	ReasonTileWallStation  = "tile_wall_station_not_set"
	ReasonTileNoFit        = "tile_no_fit"
	ReasonTileRoutes       = "tile_routes"
	ReasonTileNoLink       = "tile_required_link_not_adjacent"
	ReasonTilePassThrough  = "tile_pass_through"

	ReasonTileAirlock          = "tile_airlock"
	ReasonTileAirlockNotPaid   = "tile_airlock_not_paid"
	ReasonTileAirlockNoGround  = "tile_airlock_no_ground"
	ReasonTileAirlockUnpriced  = "tile_airlock_unpriced"
	ReasonTileAirlockTemperate = "tile_airlock_temperate"
)

// ErrTile marks an embedding that cannot be tiled into a valid plan; ErrTileRole
// a group whose room role no planned room carries.
var (
	ErrTile     = errors.New("tile embedding")
	ErrTileRole = fmt.Errorf("%w: group role has no planned room", ErrTile)
	// ErrTileClosed: rooms packed wall to wall leave a room with no door onto
	// the ground and no neighbour it may be walked through, even marked Through.
	ErrTileClosed = fmt.Errorf("%w: packed rooms leave no way in", ErrTile)
)

// TileInput is everything tiling reads. Embedding must come from Embed over
// Groups.
type TileInput struct {
	Groups       []RoomGroup
	Embedding    Embedding
	Catalog      StationCatalog
	Zones        []LayoutZone
	Reservations []LayoutReservation
	Survey       MapSurvey
	// OutdoorCurve and RoofDistance are the roofed set's reads (layout_roof.go);
	// the climate model is Catalog.Climate. An unknown read leaves the hallways
	// unroofed and the decision says so.
	OutdoorCurve domain.Fact[[]float64]
	RoofDistance float64
	// FullRotRateC (the band a cold room is held under) and SteelValue (the
	// market value a build cost is counted in) are the airlocks' pricing reads
	// (layout_tile_airlock.go); either unknown leaves the airlocks unpriced.
	FullRotRateC, SteelValue float64
	// VentAvailable is whether the vent is researched; the tiler places vents
	// (layout_tile_vent.go) over the same curve, rot band and steel value.
	VentAvailable domain.Fact[bool]
	// Light is the price of darkness against lamps (layout_light.go); invalid
	// leaves the lamps unplaced and the decision says so.
	Light LightPrice
}

// TileDecision is one tiling decision for the caller to journal. Group is the
// room's group (the first of a link's two) and Other the second group of a
// link decision, -1 otherwise.
type TileDecision struct {
	Group, Other int
	Verdict      string
	Reason       string
	// Field names the field of a field decision (Group is -1 then).
	Field string
	// DoorW and AirlockW are an airlock decision's priced options: the plain
	// door's and the airlock's total sustained watts (operating plus build).
	DoorW, AirlockW float64 `json:",omitempty"`
	// DarkW and LampW are a lamp decision's priced options in sustained watts:
	// the dark cost left unlit and the lamps' cost (a lit room's lamps, or one
	// lamp when none paid).
	DarkW, LampW float64 `json:",omitempty"`
}

// TileResult is the tiled plan with the furniture of every room and the
// decisions behind it.
type TileResult struct {
	Plan LayoutPlan
	// Rooms maps a group to its room's id.
	Rooms map[int]int32
	// Pieces are each room's stations, set into its region.
	Pieces    map[int32][]InteriorPiece
	Decisions []TileDecision
	// ConduitDecisions are the conduit net's decisions, one per routed or
	// blocked link (layout_conduit.go).
	ConduitDecisions []ConduitDecision
	// RoofDecisions are the roofed set's decisions (layout_roof.go).
	RoofDecisions []RoofDecision
	// Lamps are the lamp cells of each lit room by room id (layout_light.go).
	Lamps map[int32][]PlannedLamp
}

// groupCold reports that the group's room is kept cold: it needs cold or holds
// a cooler or cold storage.
func groupCold(g RoomGroup) bool {
	return slices.Contains(g.Requires, EnvCold) || slices.ContainsFunc(g.Stations, func(s GroupStation) bool {
		return s.Purpose == PurposeCool || s.Purpose == PurposeStoreCold
	})
}

// PlannedRoleOfGroup is the planned room a group becomes: the room role the
// game gives its stations, a storeroom that stores cold being the freezer.
// False when no planned room carries the group's role.
func PlannedRoleOfGroup(g RoomGroup) (PlannedRole, bool) {
	if g.Planned != "" {
		return g.Planned, true
	}
	if g.Role == RoomRoleStoreroom {
		if groupCold(g) {
			return PlannedFreezer, true
		}
		return PlannedStorage, true
	}
	return PlannedRoleOf(g.Role)
}

type tileRoom struct {
	group int
	rect  Rectangle
	role  PlannedRole
}

type tiler struct {
	in       TileInput
	rooms    []tileRoom
	weight   map[[2]int]float64
	required map[[2]int]bool
	parent   []int // union-find over the rooms the link doors join
	adj      map[[2]int][]domain.Cell
	links    map[[2]int]domain.Cell
	through  map[int]bool
	extDoor  []*domain.Cell
	extOut   []domain.Cell
	free     map[domain.Cell]bool // open ground, as chooseExteriors found it
	vents    []tileVent
	plan     LayoutPlan
	out      TileResult
}

func pairOf(i, j int) [2]int {
	if j < i {
		i, j = j, i
	}
	return [2]int{i, j}
}

// TileEmbedding tiles the embedding into a plan. Groups the embedding could not
// place are left out (the embedding reports them). It returns the partial
// result with the error when a room cannot be reached. A room the placer cannot
// furnish stands bare with a TileUnsatisfied decision (TileResult.Unfit).
func TileEmbedding(in TileInput) (TileResult, error) {
	scorer := newPlanScorer(in.Zones, in.Reservations, in.Survey)
	t := &tiler{in: in, weight: map[[2]int]float64{}, required: map[[2]int]bool{}, adj: map[[2]int][]domain.Cell{}, links: map[[2]int]domain.Cell{}, through: map[int]bool{}}
	t.out.Rooms = map[int]int32{}
	t.out.Pieces = map[int32][]InteriorPiece{}
	index := map[int]int{}
	blocks := slices.Clone(in.Embedding.Blocks)
	placed := placedBlocks(in.Groups, blocks)
	sort.Slice(blocks, func(i, j int) bool { return blocks[i].Group < blocks[j].Group })
	t.plan = LayoutPlan{Zones: in.Zones, Reservations: in.Reservations}
	t.fieldZones() // the placed fields, no walls or doors (layout_field_place.go)
	// Every group owes a room, placed or not: a group the embedding left out
	// stays in the hard tier's Missing term instead of hiding behind another
	// group of its role. A role no planned room carries is stopped below when
	// its group is placed, else left to the embedding's report.
	for _, g := range in.Groups {
		if role, ok := PlannedRoleOfGroup(g); ok {
			t.plan.Owed = append(t.plan.Owed, role)
		}
	}
	for _, b := range blocks {
		if b.Group < 0 || b.Group >= len(in.Groups) {
			return t.out, fmt.Errorf("%w: block of unknown group %d", ErrTile, b.Group)
		}
		g := in.Groups[b.Group]
		role, ok := PlannedRoleOfGroup(g)
		if !ok {
			return t.out, fmt.Errorf("%w: group %d predicts %s", ErrTileRole, g.ID, g.Role)
		}
		for _, s := range g.Stations {
			if _, ok := in.Catalog.Station(s.Def); !ok {
				return t.out, fmt.Errorf("%w: station %s of group %d is not in the catalog", ErrTile, s.Def, g.ID)
			}
		}
		index[b.Group] = len(t.rooms)
		t.rooms = append(t.rooms, tileRoom{group: b.Group, rect: b.Interior, role: role})
		crossing := crossingOf(g)
		room := PlannedRoom{ID: b.Built, Replaces: b.Replaces, Role: role, Region: RectRegion(b.Interior), Crossing: &crossing, Outdoor: role.IsOutdoor()}
		room.Markers = g.Markers
		if role == PlannedTrainingRange {
			if ref, ok := awayReference(in.Groups, placed); ok {
				room.Facing = dummyFacing(b.Interior, ref)
			}
		}
		room.Dug = scorer.g.dug(room)
		t.plan.Rooms = append(t.plan.Rooms, room)
	}
	t.plan = t.plan.WithRoomIDs()
	for i, r := range t.rooms {
		t.out.Rooms[r.group] = t.plan.Rooms[i].ID
		reason := ReasonTileRoom
		switch b := blocks[slices.IndexFunc(blocks, func(b EmbeddedBlock) bool { return b.Group == r.group })]; {
		case b.Built != 0:
			reason = ReasonTileRoomBuilt // the standing room, kept as it stands
		case b.Replaces != 0:
			reason = ReasonTileRoomMoved // a successor for the standing room it replaces
		}
		t.note(r.group, -1, TilePlace, reason)
	}
	for _, b := range blocks {
		for _, l := range in.Groups[b.Group].Links {
			j, ok := index[l.To]
			if !ok || l.To == b.Group {
				continue
			}
			k := pairOf(index[b.Group], j)
			if b.Group < l.To {
				t.weight[k] += l.Weight
			}
			t.required[k] = t.required[k] || l.Required
		}
	}
	for i := range t.rooms {
		for j := i + 1; j < len(t.rooms); j++ {
			if t.open(i) || t.open(j) {
				continue // an unfenced room has no wall to share
			}
			if run := sharedWallRun(pad(t.rooms[i].rect, 1), pad(t.rooms[j].rect, 1)); len(run) > 0 {
				t.adj[[2]int{i, j}] = run
			}
		}
	}
	for k := range t.required {
		if t.required[k] && len(t.adj[k]) == 0 && !t.open(k[0]) && !t.open(k[1]) {
			t.note(t.rooms[k[0]].group, t.rooms[k[1]].group, TileUnsatisfied, ReasonTileNoLink)
		}
	}
	t.parent = make([]int, len(t.rooms))
	for i := range t.parent {
		t.parent[i] = i
	}
	t.chooseLinks(true)
	t.chooseExteriors(scorer)
	if err := t.joinSealed(); err != nil {
		return t.out, err
	}
	t.chooseLinks(false)
	if err := t.keepRoutes(); err != nil {
		t.out.Plan = t.build()
		return t.out, err
	}
	t.chooseVents()
	t.out.Plan = t.build()
	if !t.out.Plan.Valid() {
		return t.out, fmt.Errorf("%w: the tiled plan is not valid", ErrTile)
	}
	t.furnish()
	roof := PlanRoofed(t.out.Plan, t.roofInput())
	t.out.Plan.Roofed, t.out.RoofDecisions = roof.Roofed, roof.Decisions
	if t.placeAirlocks() { // the vestibules are rooms too: roof them
		roof = PlanRoofed(t.out.Plan, t.roofInput())
		t.out.Plan.Roofed, t.out.RoofDecisions = roof.Roofed, roof.Decisions
	}
	t.placeLamps()
	t.out.Plan.Lamps = t.out.Lamps
	t.routeTileConduits()
	if !t.out.Plan.Valid() {
		return t.out, fmt.Errorf("%w: the conduit net is not valid", ErrTile)
	}
	return t.out, nil
}

// open reports room i as unfenced: it has no wall, door or entrance, and every
// pawn reaches it over open ground (PlannedRoom.Unfenced).
func (t *tiler) open(i int) bool { return t.plan.Rooms[i].Unfenced() }

func (t *tiler) note(group, other int, verdict, reason string) {
	t.out.Decisions = append(t.out.Decisions, TileDecision{Group: group, Other: other, Verdict: verdict, Reason: reason})
}

func (t *tiler) find(i int) int {
	if t.parent[i] != i {
		t.parent[i] = t.find(t.parent[i])
	}
	return t.parent[i]
}

// doorsOf are the doors room i has so far: its exterior door and its links.
func (t *tiler) doorsOf(i int) []domain.Cell {
	doors := t.linkCells(i)
	if i < len(t.extDoor) && t.extDoor[i] != nil {
		doors = append(doors, *t.extDoor[i])
	}
	return doors
}

// pickLink is the door cell of the shared wall of pair k, and whether the
// placer furnishes both rooms behind it: the middle of the run when it does,
// else the first cell of the run that does, else the middle. The placer is the
// judge of a door cell as it is of an exterior door.
func (t *tiler) pickLink(k [2]int) (domain.Cell, bool) {
	run := t.adj[k]
	middle := run[(len(run)-1)/2]
	fits := func(c domain.Cell) bool {
		return t.furnishes(k[0], append(t.doorsOf(k[0]), c)) && t.furnishes(k[1], append(t.doorsOf(k[1]), c))
	}
	if fits(middle) {
		return middle, true
	}
	for _, c := range run {
		if fits(c) {
			return c, true
		}
	}
	return middle, false
}

// chooseLinks opens a door on every adjacent required pair (required) or on
// the heaviest adjacent flows that join two rooms not yet joined. A required
// door is part of the group's area (RoomGroup.AddRequiredDoors); a flow door
// is a wish, opened only while the placer still furnishes both rooms behind
// it, so no door crowds a room's stations out.
func (t *tiler) chooseLinks(required bool) {
	find := t.find
	var pairs [][2]int
	for k := range t.adj {
		pairs = append(pairs, k)
	}
	sort.Slice(pairs, func(a, b int) bool {
		p, q := pairs[a], pairs[b]
		if t.required[p] != t.required[q] {
			return t.required[p]
		}
		if t.weight[p] != t.weight[q] {
			return t.weight[p] > t.weight[q]
		}
		if p[0] != q[0] {
			return p[0] < q[0]
		}
		return p[1] < q[1]
	})
	for _, k := range pairs {
		if t.required[k] != required {
			continue
		}
		cell, fits := t.pickLink(k)
		if !required && (t.weight[k] <= 0 || find(k[0]) == find(k[1]) || !fits || t.sleepersUseCirculation(k)) {
			continue
		}
		t.links[k] = cell
		t.parent[find(k[0])] = find(k[1])
		t.note(t.rooms[k[0]].group, t.rooms[k[1]].group, TilePlace, ReasonTileLinkDoor)
	}
}

// sleepersUseCirculation reports a flow door between a room with beds and a
// room that is not circulation, while the plan has circulation: the sleepers'
// way to the hubs is the corridor, so no hub carries a bedroom door.
func (t *tiler) sleepersUseCirculation(k [2]int) bool {
	if !slices.ContainsFunc(t.plan.Rooms, func(r PlannedRoom) bool { return r.Role == PlannedCirculation }) {
		return false
	}
	sleeps := func(i int) bool { c := t.plan.Rooms[i].Crossing; return c != nil && c.Beds > 0 }
	circulation := func(i int) bool { return t.plan.Rooms[i].Role == PlannedCirculation }
	return (sleeps(k[0]) || sleeps(k[1])) && !circulation(k[0]) && !circulation(k[1])
}

// chooseExteriors gives every room the door nearest the middle of the blocks
// whose outside cell is open ground.
func (t *tiler) chooseExteriors(scorer planScorer) {
	occupied := map[domain.Cell]bool{}
	var cx, cz int64
	for _, r := range t.rooms {
		for _, c := range rectCells(pad(r.rect, 1)) {
			occupied[c] = true
		}
		cx += int64(r.rect.X + r.rect.Width/2)
		cz += int64(r.rect.Z + r.rect.Height/2)
	}
	open := tileOpenGround(scorer, occupied)
	t.free = open
	var centre domain.Cell
	if n := int64(len(t.rooms)); n > 0 {
		centre = domain.Cell{X: int32(cx / n), Z: int32(cz / n)}
	}
	t.extDoor = make([]*domain.Cell, len(t.rooms))
	t.extOut = make([]domain.Cell, len(t.rooms))
	for i, r := range t.rooms {
		type candidate struct{ door, out domain.Cell }
		var cands []candidate
		in := r.rect
		for x := in.X; x < in.X+in.Width; x++ {
			cands = append(cands, candidate{domain.Cell{X: x, Z: in.Z - 1}, domain.Cell{X: x, Z: in.Z - 2}},
				candidate{domain.Cell{X: x, Z: in.Z + in.Height}, domain.Cell{X: x, Z: in.Z + in.Height + 1}})
		}
		for z := in.Z; z < in.Z+in.Height; z++ {
			cands = append(cands, candidate{domain.Cell{X: in.X - 1, Z: z}, domain.Cell{X: in.X - 2, Z: z}},
				candidate{domain.Cell{X: in.X + in.Width, Z: z}, domain.Cell{X: in.X + in.Width + 1, Z: z}})
		}
		cands = slices.DeleteFunc(cands, func(c candidate) bool { return !open[c.out] })
		sort.Slice(cands, func(a, b int) bool {
			da, db := squaredDistance(cands[a].door, centre), squaredDistance(cands[b].door, centre)
			return da < db || da == db && cellLess(cands[a].door, cands[b].door)
		})
		if len(cands) == 0 || t.open(i) {
			continue
		}
		// The placer is the judge of a door: each door keeps its threshold
		// clear, so the first door, nearest the middle, that leaves the group's
		// stations a place is taken, else the nearest.
		pick := cands[0]
		links := t.linkCells(i)
		for _, c := range cands {
			if t.furnishes(i, append([]domain.Cell{c.door}, links...)) {
				pick = c
				break
			}
		}
		door := pick.door
		t.extDoor[i], t.extOut[i] = &door, pick.out
		t.note(r.group, -1, TilePlace, ReasonTileExteriorDoor)
	}
}

// linkCells are the link doors of room i.
func (t *tiler) linkCells(i int) []domain.Cell {
	var out []domain.Cell
	for k, cell := range t.links {
		if k[0] == i || k[1] == i {
			out = append(out, cell)
		}
	}
	sortCells(out)
	return out
}

// wants are the floor pieces of room i's group; a wall building stands in the
// wall ring and is left out.
func (t *tiler) wants(i int) (wants []RegionWant, walls bool) {
	for _, s := range t.in.Groups[t.rooms[i].group].Stations {
		st, _ := t.in.Catalog.Station(s.Def) // TileEmbedding checked every def
		if st.Access == AccessWall {
			walls = true
			continue
		}
		wants = append(wants, RegionWant{Slot: s.Node, Def: s.Def, Size: st.Shape.Size, Interaction: st.Shape.Interaction, Count: s.Count, Min: s.Count})
	}
	return wants, walls
}

// furnishes reports that the placer sets room i's stations behind doors.
func (t *tiler) furnishes(i int, doors []domain.Cell) bool {
	wants, _ := t.wants(i)
	_, err := PlaceInRegion(RegionPlacement{Region: RectRegion(t.rooms[i].rect), Doors: doors, Wants: wants})
	return err == nil
}

// tileOpenGround is the buildable cells outside every room and wall that join the
// largest such area: an entrance in a pocket the rooms close in is no entrance.
func tileOpenGround(scorer planScorer, occupied map[domain.Cell]bool) map[domain.Cell]bool {
	var cells []domain.Cell
	for c, ok := range scorer.g.core {
		if ok && !occupied[c] {
			cells = append(cells, c)
		}
	}
	sortCells(cells)
	free := make(map[domain.Cell]bool, len(cells))
	for _, c := range cells {
		free[c] = true
	}
	var best []domain.Cell
	seen := map[domain.Cell]bool{}
	for _, c := range cells {
		if seen[c] {
			continue
		}
		comp := []domain.Cell{c}
		seen[c] = true
		for h := 0; h < len(comp); h++ {
			p := comp[h]
			for _, n := range [4]domain.Cell{{X: p.X + 1, Z: p.Z}, {X: p.X - 1, Z: p.Z}, {X: p.X, Z: p.Z + 1}, {X: p.X, Z: p.Z - 1}} {
				if free[n] && !seen[n] {
					seen[n] = true
					comp = append(comp, n)
				}
			}
		}
		if len(comp) > len(best) {
			best = comp
		}
	}
	out := make(map[domain.Cell]bool, len(best))
	for _, c := range best {
		out[c] = true
	}
	return out
}

// reach reports which rooms are joined by link doors to a room with an
// exterior door.
func (t *tiler) reach() []bool {
	ok := make([]bool, len(t.rooms))
	var queue []int
	for i := range t.rooms {
		if t.extDoor[i] != nil || t.open(i) {
			ok[i] = true
			queue = append(queue, i)
		}
	}
	for h := 0; h < len(queue); h++ {
		for k := range t.links {
			for _, e := range [2][2]int{{k[0], k[1]}, {k[1], k[0]}} {
				if e[0] == queue[h] && !ok[e[1]] {
					ok[e[1]] = true
					queue = append(queue, e[1])
				}
			}
		}
	}
	return ok
}

// joinSealed opens a link door from every room no exterior door reaches into
// an adjacent room that is reached, preferring a neighbour a room may be
// walked through (throughRank), then the heavier flow.
func (t *tiler) joinSealed() error {
	for {
		ok := t.reach()
		var best [2]int
		bestScore, bestThrough, bestFits, found := 0.0, -1, false, false
		stranded := false
		for k := range t.adj {
			for _, e := range [2][2]int{{k[0], k[1]}, {k[1], k[0]}} {
				if ok[e[0]] {
					continue
				}
				stranded = true
				if !ok[e[1]] {
					continue
				}
				score, through := t.weight[k], throughRank(t.plan.Rooms[e[1]])
				if _, linked := t.links[k]; linked {
					continue
				}
				_, fits := t.pickLink(k)
				same := through == bestThrough && fits == bestFits
				better := through > bestThrough || through == bestThrough && fits != bestFits && fits || same && score > bestScore
				if !found || better || same && score == bestScore && k[0]*len(t.rooms)+k[1] < best[0]*len(t.rooms)+best[1] {
					best, bestScore, bestThrough, bestFits, found = k, score, through, fits, true
				}
			}
		}
		if !found {
			if !stranded {
				return nil
			}
			for i, r := range t.rooms {
				if !ok[i] {
					t.note(r.group, -1, TileUnsatisfied, ReasonTileSealedRoom)
					return fmt.Errorf("%w: group %d", ErrTileClosed, r.group)
				}
			}
			return nil
		}
		t.links[best], _ = t.pickLink(best)
		t.parent[t.find(best[0])] = t.find(best[1])
		t.note(t.rooms[best[0]].group, t.rooms[best[1]].group, TilePlace, ReasonTileSealedRoom)
	}
}

// keepRoutes tiles until CheckRoutes accepts the plan: a pass-through room
// loses its lightest link door that strands no room.
func (t *tiler) keepRoutes() error {
	for {
		t.out.Plan = t.build()
		_, err := CheckRoutes(t.out.Plan)
		var through *ThroughfareError
		if !errors.As(err, &through) {
			if err != nil {
				return fmt.Errorf("%w: %v", ErrTile, err)
			}
			return nil
		}
		room := -1
		for i, r := range t.out.Plan.Rooms {
			if r.ID == through.Room {
				room = i
			}
		}
		var drop []([2]int)
		for k := range t.links {
			if k[0] == room || k[1] == room {
				drop = append(drop, k)
			}
		}
		sort.Slice(drop, func(a, b int) bool {
			p, q := drop[a], drop[b]
			if t.required[p] != t.required[q] {
				return !t.required[p]
			}
			if t.weight[p] != t.weight[q] {
				return t.weight[p] < t.weight[q]
			}
			if p[0] != q[0] {
				return p[0] < q[0]
			}
			return p[1] < q[1]
		})
		dropped := false
		for _, k := range drop {
			cell := t.links[k]
			delete(t.links, k)
			if slices.Contains(t.reach(), false) {
				t.links[k] = cell
				continue
			}
			t.note(t.rooms[k[0]].group, t.rooms[k[1]].group, TileUnsatisfied, ReasonTileLinkDropped)
			dropped = true
			break
		}
		if !dropped && room >= 0 && t.plan.Rooms[room].MayBeThrough() && !t.through[room] {
			// No door can go without stranding a room: the packed base has no
			// other way in, so the private room is walked through.
			t.through[room] = true
			t.note(t.rooms[room].group, -1, TilePlace, ReasonTilePassThrough)
			continue
		}
		if !dropped {
			t.note(t.rooms[max(room, 0)].group, -1, TileUnsatisfied, ReasonTileRoutes)
			return fmt.Errorf("%w: %v", ErrTileClosed, through)
		}
	}
}

// build is the plan over the doors chosen so far.
func (t *tiler) build() LayoutPlan {
	p := t.plan
	p.Rooms = slices.Clone(t.plan.Rooms)
	p.Links, p.Entrances = nil, nil
	for k, cell := range t.links {
		p.Links = append(p.Links, RoomLink{A: min(p.Rooms[k[0]].ID, p.Rooms[k[1]].ID), B: max(p.Rooms[k[0]].ID, p.Rooms[k[1]].ID), Cell: cell})
	}
	sort.Slice(p.Links, func(i, j int) bool {
		a, b := p.Links[i], p.Links[j]
		if a.A != b.A {
			return a.A < b.A
		}
		if a.B != b.B {
			return a.B < b.B
		}
		return cellLess(a.Cell, b.Cell)
	})
	for i := range p.Rooms {
		r := &p.Rooms[i]
		r.Through = t.through[i]
		var door domain.Cell
		if t.extDoor[i] != nil {
			door = *t.extDoor[i]
			p.Entrances = append(p.Entrances, t.extOut[i])
		} else {
			for _, l := range p.Links {
				if l.A == r.ID || l.B == r.ID {
					door = l.Cell
					break
				}
			}
		}
		if r.Unfenced() {
			continue // no ring, no door
		}
		r.Door = door
		r.DoorRot, _ = doorSide(r.Region.Bounds(), door)
	}
	sortCells(p.Entrances)
	p.Entrances = slices.Compact(p.Entrances)
	p.Sites = append(slices.Clone(t.plan.Sites), t.ventSites()...)
	p.Trips = nil
	for k, w := range t.weight {
		if w > 0 {
			p.Trips = append(p.Trips, RoomTrip{A: min(p.Rooms[k[0]].ID, p.Rooms[k[1]].ID), B: max(p.Rooms[k[0]].ID, p.Rooms[k[1]].ID), Weight: w})
		}
	}
	sort.Slice(p.Trips, func(i, j int) bool {
		a, b := p.Trips[i], p.Trips[j]
		return a.A < b.A || a.A == b.A && a.B < b.B
	})
	p.OutsideTrips = nil
	for i, r := range t.rooms {
		if w := t.in.Groups[r.group].Outside; w > 0 {
			p.OutsideTrips = append(p.OutsideTrips, OutsideTrip{Room: p.Rooms[i].ID, Weight: w})
		}
	}
	return p
}

// furnish sets every room's stations into its region, behind its doors.
func (t *tiler) furnish() {
	for i, room := range t.out.Plan.Rooms {
		g := t.in.Groups[t.rooms[i].group]
		doors := append([]domain.Cell{room.Door}, t.out.Plan.LinkCells(room.ID)...)
		sortCells(doors)
		doors = slices.Compact(doors)
		wants, walls := t.wants(i)
		if walls {
			// A wall building stands in the wall ring, not on the floor.
			t.note(g.ID, -1, TilePlace, ReasonTileWallStation)
		}
		pieces, err := PlaceInRegion(RegionPlacement{Region: room.Region, Doors: doors, Wants: wants})
		if err != nil {
			// The group's area undercounts what the placer needs: the room
			// stands without furniture and the decision says so.
			t.note(g.ID, -1, TileUnsatisfied, ReasonTileNoFit)
			continue
		}
		t.out.Pieces[room.ID] = pieces
	}
}

// Unfit are the groups whose room the placer could not furnish.
func (r TileResult) Unfit() []int {
	var out []int
	for _, d := range r.Decisions {
		if d.Reason == ReasonTileNoFit {
			out = append(out, d.Group)
		}
	}
	return out
}

// throughRank orders the neighbours a sealed room is joined through, higher
// first: by what crossing the neighbour triggers (RoomCrossing.Mechanisms), so
// a room that costs nothing to cross comes before a sleeping or a clean one
// and a cold room, which is never crossed, comes last, joined only when
// nothing else is left.
func throughRank(r PlannedRoom) int {
	if r.Role == PlannedCirculation {
		return math.MaxInt // a room that exists to be walked through comes first
	}
	if r.Crossing == nil {
		return 0
	}
	if !r.Crossing.Crossable() {
		return math.MinInt
	}
	return -r.Crossing.Mechanisms()
}

// roofInput is what the roofed set reads from the tiler's input.
func (t *tiler) roofInput() RoofInput {
	return RoofInput{Curve: t.in.OutdoorCurve, Cold: t.in.Survey.Cold, Hot: t.in.Survey.Hot, Climate: t.in.Catalog.Climate, RoofDistance: t.in.RoofDistance, Light: t.in.Light, Rock: surveyRock(t.in.Survey)}
}
