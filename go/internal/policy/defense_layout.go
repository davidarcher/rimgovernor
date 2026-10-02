package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DefenseCell is one census row of observations_read_defense_site. Unknown
// facts never become free space, cover or a route.
type DefenseCell struct {
	Cell                                                                     domain.Cell
	Walkable, Passable, BlocksSight, PlayerOwned, NaturalRock, EdgeReachable domain.Fact[bool]
	HomeArea, Door                                                           domain.Fact[bool]
	// Roofed is the cell under any roof; the mortar tier needs it known
	// false (#1206).
	Roofed    domain.Fact[bool]
	CoverFill domain.Fact[float64]
	Edifice   string
}

// DefenseLine is one observations_read_lines_of_fire row: whether a firing
// cell has native line of sight to an approach cell.
type DefenseLine struct {
	From, To    domain.Cell
	LineOfSight domain.Fact[bool]
}

// DefenseDefinitions names the native buildings each tier places. Stuff
// empty requests the native default material. Door is the safe lane's
// colonist-only passage; its stuff must be wood, the corridor's pricing
// (#619) assumes a wooden door's opening time. Floor is the constructed
// terrain the firing line lays on each shooter cell: nothing grows on a
// built floor, and a blueprint over a plant has the constructor cut it, so
// the firing position stays standable for the hold plan's move (#224).
// Embrasure, when the game has it (Ideology/1.4+), is the cover a firing
// position gets where its cover cell is the perimeter wall (#868); empty,
// such positions are skipped. It takes WallStuff.
type DefenseDefinitions struct {
	Sandbag, SandbagStuff, Wall, WallStuff, Fence, FenceStuff, Trap, TrapStuff, Door, DoorStuff, Floor string
	Embrasure                                                                                          string
	// Bait is the cheap furniture the bait tier places on the arrival
	// sector's approach (#1063); empty places none.
	Bait, BaitStuff string
}

type DefenseRequest struct {
	Bounds Bounds
	Region Rectangle
	// Home is the colony cell the approach path leads to; Entrances are
	// colony doors that must keep a trap-free route to the map edge.
	Home      domain.Cell
	Entrances []domain.Cell
	Cells     []DefenseCell
	// Protected holds accepted footprints, walkways and doorway approaches.
	Protected   []domain.Cell
	Lines       []DefenseLine
	Definitions DefenseDefinitions
	// MinRange is the shortest ranged-weapon range among defenders; unknown
	// forbids the firing line rather than assuming one.
	MinRange  domain.Fact[float64]
	Defenders int
	// UnitCosts maps a definition to its per-placement cost; a missing entry
	// leaves that tier's cost unknown.
	UnitCosts map[string][]Amount
	// Turret asks for the powered turret tier; see DefenseTurretRequest.
	Turret DefenseTurretRequest
	// Mortar asks for the mortar tier; see DefenseMortarRequest.
	Mortar DefenseMortarRequest
	// Arrivals are distinct, observed ground-raid arrivals in this census.
	// Tick bounds their recency; turret attack ticks alone are not arrivals.
	Arrivals []DefenseArrival
	Tick     domain.Tick
	// CoverThreshold is the fill percentage above which a thing counts as
	// raider cover; the game grants a block chance to any positive fill, so
	// the native census reports zero. Unknown disables cover selection; a
	// definition name is not evidence of its fill.
	CoverThreshold domain.Fact[float64]
	// Killbox is the layout plan's opening the corridor stands in.
	Killbox DefenseKillbox
	// IEDs are the IED traps the approach tier may place (#1209), in
	// preference order; empty places none.
	IEDs []DefenseIED
	// FlammableStorage is every storage cell the IED tier's blast must not
	// reach; unknown places no IED.
	FlammableStorage domain.Fact[[]domain.Cell]
}

// DefenseKillbox is the layout plan's killbox opening (#789): Entry is the
// centre cell of the opening's outer face, Toward points inward through it,
// Width is the opening's width. Walled cells are the perimeter tier's wall;
// the funnel leaves them to it. Depth is the killbox's rows inward of the
// ring, the space the corridor and kill zone stand in.
type DefenseKillbox struct {
	Depth  int32
	Entry  domain.Cell
	Toward domain.Rotation
	Width  int32
	Walled []domain.Cell
}

type DefenseTierName string

const (
	TierChokepoint   DefenseTierName = "chokepoint"
	TierFiringLine   DefenseTierName = "firing_line"
	TierFunnel       DefenseTierName = "funnel"
	TierTrapCorridor DefenseTierName = "trap_corridor"
)

// DefenseTier is one staged construction: its placements plus cells the tier
// reserves against later routine construction.
type DefenseTier struct {
	Name      DefenseTierName
	Buildings []domain.Building
	Reserved  []domain.Cell
	Costs     domain.Fact[[]Amount]
}
type FiringPosition struct {
	Cell, Cover, Retreat domain.Cell
	Verified             bool
}

// DefenseLayout is a deterministic proposal. Native placement previews and
// the access audit still decide legality; only admitted actions reserve cells.
type DefenseLayout struct {
	Chokepoint domain.Cell
	// Toward is the corridor direction from the map edge toward Home.
	Toward domain.Rotation
	// Width is the passable width across the chokepoint before construction.
	Width int
	// Entry is the approach cell raiders reach first. TrapLane is the
	// raiders' snake in entry-to-exit order, its last cell the exit into
	// the kill zone.
	Entry    domain.Cell
	TrapLane []domain.Cell
	Firing   []FiringPosition
	// Turrets lists every turret position considered; the turret tier holds
	// the verified ones the power and stock gates allow.
	Turrets []TurretPosition
	Tiers   []DefenseTier
	// LinesVerified is false until every firing cell carries a known native
	// line of sight to the kill zone; Probe lists the pairs to read.
	LinesVerified bool
	// Approaches describes local routes and cover demand, not admitted orders.
	Approaches DefenseApproaches
}

const (
	defenseMaxWidth     = 12
	defenseMaxDefenders = 8
)

// The killbox (#1544, after the RimWorld wiki's defense structures), in
// rows inward from the ring's inner face and columns across the opening
// (column 0 is the 1-tile entrance). The corridor is a snake of zigzag
// legs: each leg a hallway killboxLegWidth wide across the kill zone's
// width, wall teeth jutting in from alternating sides every
// killboxToothPitch columns; legs join by U-turns at alternating ends and
// are parted by killboxWallThick rows of wall, as is the last leg from the
// kill zone, its exit gap at the last leg's end. Below the snake: the kill
// row, the fence bar, sandbags, shooters, retreat cells and the 2-thick
// back wall with the defenders' doorway at column 0. No door stands on the
// raiders' route.
const (
	killboxLegWidth   int32 = 3
	killboxWallThick  int32 = 3
	killboxToothPitch int32 = 3
	// killboxLegPitch is the rows one leg and the wall after it take.
	killboxLegPitch = killboxLegWidth + killboxWallThick
	// killboxZoneRows is the kill zone below the snake: kill row, fence
	// bar, sandbags, shooters, retreat cells and the 2-thick back wall.
	killboxZoneRows int32 = 7
	// killboxMinHalf is the narrowest kill zone: a leg long enough for
	// two teeth.
	killboxMinHalf int32 = 4
	// killboxMaxLegs bounds the legs, and the layout plan reserves depth
	// for them: across the widest kill zone a third leg's U-turn and walk
	// back cost raiders more than bashing the wall before it.
	killboxMaxLegs int32 = 2
	// killboxRows is the killbox's depth the layout plan reserves.
	killboxRows = killboxMaxLegs*killboxLegPitch + killboxZoneRows
)

// hallway is a corridor shape in killbox coordinates (X the column, Z the
// row): lane is every corridor cell, the ring entrance first and the exit
// last; walls are what it builds; pockets mark the turns, the corners
// beside each tooth and at each U-turn, traps going on the raiders' line
// near them; rows is its depth.
type hallway struct {
	lane, walls, pockets []domain.Cell
	rows                 int32
}

// snakeCorridor is the snake of zigzag legs between columns -half and half.
// Leg 0 holds the entrance and runs toward +half; each U-turn opens
// killboxLegWidth columns of the wall below a leg's end. Within a leg a
// tooth stands every killboxToothPitch columns, two cells long from the top
// or bottom row in turn, so the hallway pinches to one cell at alternating
// sides. The pockets are the tooth-side cells beside each tooth and the
// outer corners of each U-turn.
func snakeCorridor(half, legs int32) hallway {
	var h hallway
	for k := -perimeterThick; k < 0; k++ {
		h.lane = append(h.lane, domain.Cell{X: 0, Z: k})
	}
	low, high := -half, half
	h.rows = legs * killboxLegPitch
	for k := int32(0); k < h.rows; k++ {
		h.walls = append(h.walls, domain.Cell{X: low - 1, Z: k}, domain.Cell{X: high + 1, Z: k})
	}
	pocket := func(c domain.Cell) { h.pockets = append(h.pockets, c) }
	for leg := int32(0); leg < legs; leg++ {
		top := leg * killboxLegPitch
		bottom := top + killboxLegWidth - 1
		// The leg runs from start toward end in steps of dir.
		start, end, dir := low, high, int32(1)
		if leg%2 == 1 {
			start, end, dir = high, low, -1
		}
		tooth := map[domain.Cell]bool{}
		fromTop := leg%2 == 0
		// Leg 0's teeth stand past the entrance: the cells behind it are
		// a dead end.
		first := start + dir*2
		if leg == 0 {
			first = 2
		}
		for a := first; dir*(end-a) >= 2; a += dir * killboxToothPitch {
			rows := []int32{top, top + 1}
			side := top
			if !fromTop {
				rows, side = []int32{bottom - 1, bottom}, bottom
			}
			for _, k := range rows {
				tooth[domain.Cell{X: a, Z: k}] = true
				h.walls = append(h.walls, domain.Cell{X: a, Z: k})
			}
			pocket(domain.Cell{X: a - dir, Z: side})
			pocket(domain.Cell{X: a + dir, Z: side})
			fromTop = !fromTop
		}
		for i := int32(0); i <= high-low; i++ {
			for k := top; k <= bottom; k++ {
				if c := (domain.Cell{X: start + dir*i, Z: k}); !tooth[c] {
					h.lane = append(h.lane, c)
				}
			}
		}
		// The wall below the leg, open under its last killboxLegWidth
		// columns: the U-turn into the next leg, or the exit.
		var exit domain.Cell
		for k := bottom + 1; k <= bottom+killboxWallThick; k++ {
			for i := int32(0); i <= high-low; i++ {
				c := domain.Cell{X: start + dir*i, Z: k}
				switch {
				case i < high-low+1-killboxLegWidth:
					h.walls = append(h.walls, c)
				case leg == legs-1 && k == bottom+killboxWallThick && i == high-low-1:
					exit = c
				default:
					h.lane = append(h.lane, c)
				}
			}
		}
		// The U-turn's outer corners: where the leg meets the far wall,
		// and where the next leg leaves it.
		pocket(domain.Cell{X: end, Z: top})
		if leg == legs-1 {
			h.lane = append(h.lane, exit)
		} else {
			pocket(domain.Cell{X: end, Z: bottom + killboxLegPitch})
		}
	}
	return h
}

var directions = [4]domain.Cell{{X: 0, Z: 1}, {X: 1, Z: 0}, {X: 0, Z: -1}, {X: -1, Z: 0}}

func rotationOf(d domain.Cell) domain.Rotation {
	switch d {
	case directions[0]:
		return domain.North
	case directions[1]:
		return domain.East
	case directions[2]:
		return domain.South
	}
	return domain.West
}
func addCell(a, b domain.Cell) domain.Cell { return domain.Cell{X: a.X + b.X, Z: a.Z + b.Z} }
func scale(d domain.Cell, n int32) domain.Cell {
	return domain.Cell{X: d.X * n, Z: d.Z * n}
}
func perpendicular(d domain.Cell) domain.Cell { return domain.Cell{X: -d.Z, Z: d.X} }

type defenseSite struct {
	r        DefenseRequest
	cells    map[domain.Cell]DefenseCell
	protect  map[domain.Cell]bool
	inRegion func(domain.Cell) bool
}

func (s defenseSite) passable(c domain.Cell) bool {
	row, ok := s.cells[c]
	return ok && positive(row.Passable)
}

// free is buildable: walkable, unprotected, no edifice, not a door.
func (s defenseSite) free(c domain.Cell) bool {
	row, ok := s.cells[c]
	return ok && !s.protect[c] && positive(row.Walkable) && row.Edifice == "" && !positive(row.Door) && !positive(row.PlayerOwned)
}

// blocking is a cell known to stop raiders: observed impassable rock or
// wall. A missing or unknown cell is neither a route nor a wall.
func (s defenseSite) blocking(c domain.Cell) bool {
	row, ok := s.cells[c]
	v, known := row.Passable.Value()
	return ok && known && !v
}

func newDefenseSite(r DefenseRequest) (defenseSite, error) {
	if r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096 {
		return defenseSite{}, errors.New("invalid defense bounds")
	}
	reg := r.Region
	if reg.Width <= 0 || reg.Height <= 0 || reg.X < 0 || reg.Z < 0 || reg.X+reg.Width > r.Bounds.Width || reg.Z+reg.Height > r.Bounds.Height {
		return defenseSite{}, errors.New("invalid defense region")
	}
	if r.Defenders < 0 || r.Defenders > defenseMaxDefenders {
		return defenseSite{}, errors.New("invalid defender count")
	}
	if err := validateDefenseArrivals(r); err != nil {
		return defenseSite{}, err
	}
	if v, k := r.MinRange.Value(); k && (math.IsNaN(v) || math.IsInf(v, 0) || v <= 0) {
		return defenseSite{}, errors.New("invalid minimum range")
	}
	d := r.Definitions
	for _, name := range []string{d.Sandbag, d.Wall, d.Fence, d.Trap, d.Door, d.Floor} {
		if name == "" {
			return defenseSite{}, errors.New("missing defense definition")
		}
	}
	s := defenseSite{r: r, cells: map[domain.Cell]DefenseCell{}, protect: map[domain.Cell]bool{}}
	s.inRegion = func(c domain.Cell) bool {
		return c.X >= reg.X && c.Z >= reg.Z && c.X < reg.X+reg.Width && c.Z < reg.Z+reg.Height
	}
	if !s.inRegion(r.Home) {
		return defenseSite{}, errors.New("home outside defense region")
	}
	for _, c := range r.Cells {
		if !s.inRegion(c.Cell) {
			return defenseSite{}, errors.New("defense cell outside region")
		}
		if _, dup := s.cells[c.Cell]; dup {
			return defenseSite{}, errors.New("duplicate defense cell")
		}
		if v, k := c.CoverFill.Value(); k && (math.IsNaN(v) || v < 0 || v > 1) {
			return defenseSite{}, errors.New("invalid cover fill")
		}
		s.cells[c.Cell] = c
	}
	for _, c := range r.Protected {
		s.protect[c] = true
	}
	for _, c := range r.Entrances {
		if !s.inRegion(c) {
			return defenseSite{}, errors.New("entrance outside defense region")
		}
	}
	for _, l := range r.Lines {
		if !s.inRegion(l.From) || !s.inRegion(l.To) {
			return defenseSite{}, errors.New("line of fire outside region")
		}
	}
	return s, nil
}

func (s defenseSite) onBorder(c domain.Cell) bool {
	reg := s.r.Region
	return c.X == reg.X || c.Z == reg.Z || c.X == reg.X+reg.Width-1 || c.Z == reg.Z+reg.Height-1
}

// DefenseLayouts proposes a staged corridor defense in the request's killbox
// opening. It returns an error without an opening or when the corridor
// cannot stand there, and never places on protected, occupied or unknown
// cells.
func DefenseLayouts(r DefenseRequest) (DefenseLayout, error) {
	s, err := newDefenseSite(r)
	if err != nil {
		return DefenseLayout{}, err
	}
	// The layout anchors on the layout plan's killbox opening (#789): the
	// corridor runs inward from the opening's outer face, across the
	// opening's width, and the wall ring beside it is the perimeter tier's.
	kb := r.Killbox
	if kb.Width < 1 || kb.Width > defenseMaxWidth || !s.inRegion(kb.Entry) {
		return DefenseLayout{}, errors.New("no killbox opening to anchor the corridor on")
	}
	walled := map[domain.Cell]bool{}
	for _, c := range kb.Walled {
		walled[c] = true
	}
	d := directionOf(kb.Toward)
	p := perpendicular(d)
	entry := kb.Entry
	// at is a killbox cell: row k inward from the ring's inner face (the
	// ring rows are -perimeterThick..-1), column a across the opening.
	at := func(k, a int32) domain.Cell {
		return addCell(entry, addCell(scale(d, perimeterThick+k), scale(p, a)))
	}
	layout := DefenseLayout{Chokepoint: entry, Toward: rotationOf(d), Width: int(kb.Width), Entry: entry}
	// The kill zone is sized from the defenders and turrets: the firing
	// span centred on column 0 with a turret slot every turretSpacing
	// beyond it, within the plan's killbox. The snake's legs span it, and
	// as many legs as the killbox's depth holds.
	half := int32(r.Defenders / 2)
	if q := r.Turret; q.Definition != "" && q.Max > 0 {
		size := max(q.Size.Width, q.Size.Height, 1)
		half += (turretSpacing + size - 1) * int32((q.Max+1)/2)
	}
	half = min(max(half, killboxMinHalf), killboxHalf-1)
	low, high := -half, half
	legs := min((kb.Depth-killboxZoneRows)/killboxLegPitch, killboxMaxLegs)
	if legs < 1 {
		return DefenseLayout{}, errors.New("killbox too shallow for the corridor")
	}
	hall := snakeCorridor(half, legs)
	for _, c := range hall.lane {
		layout.TrapLane = append(layout.TrapLane, at(c.Z, c.X))
	}
	for _, c := range layout.TrapLane {
		if !s.passable(c) {
			return DefenseLayout{}, errors.New("corridor is not passable end to end")
		}
	}
	exit := layout.KillZone()
	lane := map[domain.Cell]bool{}
	for _, c := range layout.TrapLane {
		lane[c] = true
	}

	var funnel, corridor, line []domain.Building
	var funnelReserved, firingReserved []domain.Cell
	costs := newCorridorCosts()
	for c := range walled {
		costs.closed[c] = true
	}
	build := func(into *[]domain.Building, definition, stuff string, c domain.Cell, what string) error {
		if !s.free(c) {
			return errors.New(what + " cell is not buildable")
		}
		b, err := domain.NewBuilding(definition, c, domain.North, stuff)
		if err != nil {
			return err
		}
		*into = append(*into, b)
		return nil
	}
	var cellsToWall []domain.Cell
	// The ring opening narrows to the 1-tile entrance.
	openLow := -kb.Width / 2
	for a := openLow; a < openLow+kb.Width; a++ {
		for k := int32(-perimeterThick); k < 0 && a != 0; k++ {
			cellsToWall = append(cellsToWall, at(k, a))
		}
	}
	for _, c := range hall.walls {
		cellsToWall = append(cellsToWall, at(c.Z, c.X))
	}
	// The kill zone's sides and its 2-thick back wall.
	killRow, fenceRow := hall.rows, hall.rows+1
	backWall := fenceRow + 4
	for k := killRow; k < backWall; k++ {
		cellsToWall = append(cellsToWall, at(k, low-1), at(k, high+1))
	}
	for k := backWall; k < backWall+2; k++ {
		for a := low - 1; a <= high+1; a++ {
			if a != 0 {
				cellsToWall = append(cellsToWall, at(k, a))
			}
		}
	}
	for _, c := range cellsToWall {
		if lane[c] || costs.closed[c] || s.blocking(c) {
			continue
		}
		if err := build(&funnel, r.Definitions.Wall, r.Definitions.WallStuff, c, "wall"); err != nil {
			return DefenseLayout{}, err
		}
		funnelReserved = append(funnelReserved, c)
		costs.closed[c] = true
	}
	// The defenders' doorway through the back wall.
	for k := backWall; k < backWall+2; k++ {
		c := at(k, 0)
		if err := build(&funnel, r.Definitions.Door, r.Definitions.DoorStuff, c, "door"); err != nil {
			return DefenseLayout{}, err
		}
		funnelReserved = append(funnelReserved, c)
		costs.cost[c], costs.full[c] = pathWoodDoorCost, true
	}
	// Spike traps on the raiders' cheapest line at each turn: hostile
	// pawns do not know the player's traps and walk over them, while
	// colonists price their faction's known traps (Building_Trap
	// PathFindCostFor) and take the hallway's trap-free side, from which
	// they rearm them. Never beside another (PlaceWorker_NeverAdjacentTrap),
	// never where the colonists' cheapest hallway route would cross one.
	raiderLine, _ := s.cheapestRoutes(raiderCosts(costs, nil), entry, []domain.Cell{exit})
	type site struct {
		cell domain.Cell
		near int32
	}
	var sites []site
	for c := range raiderLine {
		if !lane[c] || c == entry || c == exit {
			continue
		}
		near := int32(math.MaxInt32)
		for _, q := range hall.pockets {
			t := at(q.Z, q.X)
			near = min(near, max(abs32(t.X-c.X), abs32(t.Z-c.Z)))
		}
		if near <= 2 {
			sites = append(sites, site{c, near})
		}
	}
	order := map[domain.Cell]int{}
	for i, c := range layout.TrapLane {
		order[c] = i
	}
	sort.Slice(sites, func(i, j int) bool {
		if sites[i].near != sites[j].near {
			return sites[i].near < sites[j].near
		}
		return order[sites[i].cell] < order[sites[j].cell]
	})
	for _, st := range sites {
		t := st.cell
		beside := costs.traps[t]
		for _, d := range pathSteps {
			beside = beside || costs.traps[addCell(t, d)]
		}
		if beside || !s.free(t) {
			continue
		}
		costs.traps[t] = true
		if !s.hallwayAvoidsTraps(costs, lane, exit, entry) {
			delete(costs.traps, t)
			continue
		}
		if err := build(&corridor, r.Definitions.Trap, r.Definitions.TrapStuff, t, "trap"); err != nil {
			return DefenseLayout{}, err
		}
	}
	// The fence T: a continuous bar across the kill zone and a stem toward
	// the exit, so raiders climb fences under fire. Raider-side shaping is
	// walls and fences only: in 1.6 a pawn stands on a sandbag or barricade
	// cell and still takes its cover, so one there would hand raiders a
	// firing position. Sandbags stand only on the defenders' line.
	fences := []domain.Cell{at(killRow, hall.lane[len(hall.lane)-1].X)}
	for a := low; a <= high; a++ {
		fences = append(fences, at(fenceRow, a))
	}
	for _, c := range fences {
		if err := build(&corridor, r.Definitions.Fence, r.Definitions.FenceStuff, c, "fence"); err != nil {
			return DefenseLayout{}, err
		}
		costs.cost[c] = pathFenceCost
	}
	// Firing line: sandbags behind the fence bar, floored shooter cells
	// behind them and a free retreat cell before the back wall, centred on
	// column 0, each within MinRange of the exit.
	lines := map[[2]domain.Cell]domain.Fact[bool]{}
	for _, l := range r.Lines {
		lines[[2]domain.Cell{l.From, l.To}] = l.LineOfSight
	}
	minRange, rangeKnown := r.MinRange.Value()
	if rangeKnown && r.Defenders > 0 {
		for _, o := range []int32{0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6} {
			if len(layout.Firing) >= r.Defenders {
				break
			}
			if o < low || o > high {
				continue
			}
			cover, shooter, retreat := at(fenceRow+1, o), at(fenceRow+2, o), at(fenceRow+3, o)
			if !s.free(cover) || !s.free(shooter) || !s.free(retreat) {
				continue
			}
			if math.Sqrt(float64(squaredDistance(shooter, exit))) > minRange {
				continue
			}
			los, known := lines[[2]domain.Cell{shooter, exit}].Value()
			if known && !los {
				continue
			}
			// A cover cell on the perimeter wall is an embrasure in the
			// wall rather than wall plus sandbag (#868).
			coverDef, coverStuff := r.Definitions.Sandbag, r.Definitions.SandbagStuff
			if walled[cover] {
				if r.Definitions.Embrasure == "" {
					continue
				}
				coverDef, coverStuff = r.Definitions.Embrasure, r.Definitions.WallStuff
			}
			b, err := domain.NewBuilding(coverDef, cover, domain.North, coverStuff)
			if err != nil {
				return DefenseLayout{}, err
			}
			floor, err := domain.NewBuilding(r.Definitions.Floor, shooter, domain.North, "")
			if err != nil {
				return DefenseLayout{}, err
			}
			line = append(line, b, floor)
			firingReserved = append(firingReserved, cover, shooter, retreat)
			layout.Firing = append(layout.Firing, FiringPosition{Cell: shooter, Cover: cover, Retreat: retreat, Verified: known})
		}
	}
	layout.LinesVerified = len(layout.Firing) > 0
	for _, f := range layout.Firing {
		layout.LinesVerified = layout.LinesVerified && f.Verified
	}
	// Every colony entrance and Home keep a route to the map edge once the
	// layout stands, and no cheapest colonist route crosses a trap, nor
	// does one through the hallway from the kill zone to the entrance.
	for _, start := range append([]domain.Cell{s.r.Home}, s.r.Entrances...) {
		if s.passable(start) && !s.colonistRouteAvoidsTraps(costs, start) {
			return DefenseLayout{}, errors.New("layout would cut an entrance off from the map edge or route colonists over a trap")
		}
	}
	if !s.hallwayAvoidsTraps(costs, lane, exit, entry) {
		return DefenseLayout{}, errors.New("colonists would cross a trap walking the hallway")
	}
	// Raiders walk every leg into the kill zone rather than bash through
	// a wall.
	var killCells []domain.Cell
	for a := low; a <= high; a++ {
		killCells = append(killCells, at(killRow, a))
	}
	if !s.raiderWalksCorridor(costs, layout, funnelReserved, killCells) {
		return DefenseLayout{}, errors.New("raiders would bash through the corridor")
	}
	// The chokepoint tier places nothing: it reserves the corridor and the
	// kill row so routine construction never fills them.
	reservedLanes := append([]domain.Cell{}, layout.TrapLane...)
	for _, c := range killCells {
		if costs.cost[c] == 0 {
			reservedLanes = append(reservedLanes, c)
		}
	}
	layout.Tiers = []DefenseTier{
		{Name: TierChokepoint, Reserved: reservedLanes, Costs: domain.Known([]Amount{})},
		{Name: TierFiringLine, Buildings: line, Reserved: firingReserved, Costs: tierCosts(r.UnitCosts, line)},
		{Name: TierFunnel, Buildings: funnel, Reserved: funnelReserved, Costs: tierCosts(r.UnitCosts, funnel)},
		{Name: TierTrapCorridor, Buildings: corridor, Reserved: reservedLanes, Costs: tierCosts(r.UnitCosts, corridor)},
	}
	if r.Turret.Definition != "" {
		turrets, candidates, err := s.turrets(layout.Geometry())
		if err != nil {
			return DefenseLayout{}, err
		}
		layout.Turrets = candidates
		layout.Tiers = append(layout.Tiers, turrets)
	}
	layout.Approaches = s.defenseApproaches(layout)
	if bait := s.baitTier(layout); len(bait.Buildings) > 0 {
		layout.Tiers = append(layout.Tiers, bait)
	}
	if ieds := s.iedTier(layout, costs); len(ieds.Buildings) > 0 {
		layout.Tiers = append(layout.Tiers, ieds)
	}
	return layout, nil
}

func tierCosts(unit map[string][]Amount, buildings []domain.Building) domain.Fact[[]Amount] {
	total := map[Resource]int64{}
	for _, b := range buildings {
		costs, ok := unit[b.Definition()]
		if !ok {
			return domain.Unknown[[]Amount]()
		}
		for _, a := range costs {
			total[a.Resource] += a.Count
		}
	}
	out := make([]Amount, 0, len(total))
	for res, n := range total {
		out = append(out, Amount{Resource: res, Count: n})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Resource < out[j].Resource })
	return domain.Known(out)
}

// Probe lists the firing/approach pairs a caller must read through
// observations_read_lines_of_fire before the firing line and the turret
// positions are verified.
func (l DefenseLayout) Probe() (firing, approach []domain.Cell) {
	for _, f := range l.Firing {
		firing = append(firing, f.Cell)
	}
	if len(firing) == 0 {
		return nil, nil
	}
	for _, t := range l.Turrets {
		firing = append(firing, t.Cell)
	}
	return firing, []domain.Cell{l.KillZone()}
}

// KillZone is the cell raiders reach leaving the snake, the one the firing
// line and turrets must see; Entry for a layout without a trap lane.
func (l DefenseLayout) KillZone() domain.Cell {
	if n := len(l.TrapLane); n > 0 {
		return l.TrapLane[n-1]
	}
	return l.Entry
}

// hallwayAvoidsTraps reports whether colonists walking the hallway alone
// from exit to entry have a cheapest route that crosses no trap.
func (s defenseSite) hallwayAvoidsTraps(k corridorCosts, lane map[domain.Cell]bool, exit, entry domain.Cell) bool {
	k.within = lane
	route, ok := s.cheapestRoutes(k, exit, []domain.Cell{entry})
	return ok && !crossesAny(route, k.traps)
}
