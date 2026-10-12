package policy

import (
	"errors"
	"fmt"
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
//     required link, and for the heaviest flows otherwise as a maximum
//     spanning forest over the adjacent linked pairs (each flow weight is the
//     clustering's, movements a day), so no loop of doors opens shortcuts.
//     The door sits in the middle of the shared run (sharedWallLink).
//   - Exterior door. Every room gets one door onto open ground when its wall
//     has a cell whose outside neighbour is free, buildable and joined to the
//     map's largest free area, the one nearest the middle of the blocks. The
//     outside cell is the entrance. A room with no such cell is sealed in by
//     its neighbours and is entered through a link door instead; a group of
//     sealed rooms is joined to a component that has an exterior door.
//   - Pass-through. A room in noThroughfare may not lie on a path between two
//     other rooms. CheckRoutes finds a violation; the tiler then drops the
//     crossed room's lightest link door that leaves every room still joined to
//     an exterior door, required links last, and checks again.
//
// Hallways are not planned here (#2879 adds them on top).

// Verdicts and reasons of tiling decisions.
const (
	TilePlace       = "place"
	TileUnsatisfied = "unsatisfied"

	ReasonTileRoom         = "tile_room"
	ReasonTileLinkDoor     = "tile_link_door"
	ReasonTileExteriorDoor = "tile_exterior_door"
	ReasonTileSealedRoom   = "tile_sealed_room"
	ReasonTileLinkDropped  = "tile_link_dropped_thoroughfare"
	ReasonTileWallStation  = "tile_wall_station_not_set"
	ReasonTileNoFit        = "tile_no_fit"
	ReasonTileRoutes       = "tile_routes"
	ReasonTileNoLink       = "tile_required_link_not_adjacent"
)

// ErrTile marks an embedding that cannot be tiled into a valid plan; ErrTileRole
// a group whose room role no planned room carries.
var (
	ErrTile     = errors.New("tile embedding")
	ErrTileRole = fmt.Errorf("%w: group role has no planned room", ErrTile)
	// ErrTileClosed: rooms packed wall to wall leave a room with no door onto
	// the ground and no neighbour it may be walked through (hallways, #2879).
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
}

// TileDecision is one tiling decision for the caller to journal. Group is the
// room's group (the first of a link's two) and Other the second group of a
// link decision, -1 otherwise.
type TileDecision struct {
	Group, Other int
	Verdict      string
	Reason       string
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
}

// PlannedRoleOfGroup is the planned room a group becomes: the room role the
// game gives its stations, a storeroom that stores cold being the freezer.
// False when no planned room carries the group's role.
func PlannedRoleOfGroup(g RoomGroup) (PlannedRole, bool) {
	cold := slices.Contains(g.Requires, EnvCold) || slices.ContainsFunc(g.Stations, func(s GroupStation) bool {
		return s.Purpose == PurposeCool || s.Purpose == PurposeStoreCold
	})
	if g.Role == RoomRoleStoreroom {
		if cold {
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
	adj      map[[2]int][]domain.Cell
	links    map[[2]int]domain.Cell
	extDoor  []*domain.Cell
	extOut   []domain.Cell
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
	t := &tiler{in: in, weight: map[[2]int]float64{}, required: map[[2]int]bool{}, adj: map[[2]int][]domain.Cell{}, links: map[[2]int]domain.Cell{}}
	t.out.Rooms = map[int]int32{}
	t.out.Pieces = map[int32][]InteriorPiece{}
	index := map[int]int{}
	blocks := slices.Clone(in.Embedding.Blocks)
	sort.Slice(blocks, func(i, j int) bool { return blocks[i].Group < blocks[j].Group })
	t.plan = LayoutPlan{Zones: in.Zones, Reservations: in.Reservations}
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
		room := PlannedRoom{Role: role, Region: RectRegion(b.Interior)}
		room.Dug = scorer.g.dug(room)
		t.plan.Rooms = append(t.plan.Rooms, room)
	}
	t.plan = t.plan.WithRoomIDs()
	for i, r := range t.rooms {
		t.out.Rooms[r.group] = t.plan.Rooms[i].ID
		t.note(r.group, -1, TilePlace, ReasonTileRoom)
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
			if run := sharedWallRun(pad(t.rooms[i].rect, 1), pad(t.rooms[j].rect, 1)); len(run) > 0 {
				t.adj[[2]int{i, j}] = run
			}
		}
	}
	for k := range t.required {
		if t.required[k] && len(t.adj[k]) == 0 {
			t.note(t.rooms[k[0]].group, t.rooms[k[1]].group, TileUnsatisfied, ReasonTileNoLink)
		}
	}
	t.chooseLinks()
	t.chooseExteriors(scorer)
	if err := t.joinSealed(); err != nil {
		return t.out, err
	}
	if err := t.keepRoutes(); err != nil {
		t.out.Plan = t.build()
		return t.out, err
	}
	t.out.Plan = t.build()
	if !t.out.Plan.Valid() {
		return t.out, fmt.Errorf("%w: the tiled plan is not valid", ErrTile)
	}
	t.furnish()
	return t.out, nil
}

func (t *tiler) note(group, other int, verdict, reason string) {
	t.out.Decisions = append(t.out.Decisions, TileDecision{Group: group, Other: other, Verdict: verdict, Reason: reason})
}

// chooseLinks opens a door on every adjacent required pair, then on the
// heaviest adjacent flows that join two rooms not yet joined.
func (t *tiler) chooseLinks() {
	parent := make([]int, len(t.rooms))
	for i := range parent {
		parent[i] = i
	}
	var find func(int) int
	find = func(i int) int {
		if parent[i] != i {
			parent[i] = find(parent[i])
		}
		return parent[i]
	}
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
		if !t.required[k] && (t.weight[k] <= 0 || find(k[0]) == find(k[1])) {
			continue
		}
		run := t.adj[k]
		t.links[k] = run[(len(run)-1)/2]
		parent[find(k[0])] = find(k[1])
		t.note(t.rooms[k[0]].group, t.rooms[k[1]].group, TilePlace, ReasonTileLinkDoor)
	}
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
		if len(cands) == 0 {
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
		if t.extDoor[i] != nil {
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
// walked through, then the heavier flow.
func (t *tiler) joinSealed() error {
	for {
		ok := t.reach()
		var best [2]int
		bestScore, bestThrough, found := 0.0, false, false
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
				score, through := t.weight[k], !noThroughfare[t.rooms[e[1]].role]
				if _, linked := t.links[k]; linked {
					continue
				}
				better := through != bestThrough && through || through == bestThrough && score > bestScore
				if !found || better || through == bestThrough && score == bestScore && k[0]*len(t.rooms)+k[1] < best[0]*len(t.rooms)+best[1] {
					best, bestScore, bestThrough, found = k, score, through, true
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
		run := t.adj[best]
		t.links[best] = run[(len(run)-1)/2]
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
		p.Links = append(p.Links, RoomLink{A: p.Rooms[k[0]].ID, B: p.Rooms[k[1]].ID, Cell: cell})
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
		r.Door = door
		r.DoorRot, _ = doorSide(r.Region.Bounds(), door)
	}
	sortCells(p.Entrances)
	p.Entrances = slices.Compact(p.Entrances)
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
