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
	CoverFill                                                                domain.Fact[float64]
	Edifice                                                                  string
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
}

// DefenseKillbox is the layout plan's killbox opening (#789): Entry is the
// centre cell of the opening's outer face, Toward points inward through it,
// Width is the opening's width. Walled cells are the perimeter tier's wall;
// the funnel leaves them to it. Turrets are the plan's turret slots.
type DefenseKillbox struct {
	Entry   domain.Cell
	Toward  domain.Rotation
	Width   int32
	Walled  []domain.Cell
	Turrets []domain.Cell
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
	// Entry is the approach cell raiders reach first; TrapLane and SafeLane
	// are the two corridor lanes in entry-to-exit order. SafeLane is the
	// civilian corridor and stays trap free and passable end to end.
	Entry              domain.Cell
	TrapLane, SafeLane []domain.Cell
	Firing             []FiringPosition
	// Turrets lists every turret position considered; the turret tier holds
	// the verified ones the power and stock gates allow.
	Turrets []TurretPosition
	Tiers   []DefenseTier
	// LinesVerified is false until every firing cell carries a known native
	// line of sight to Entry; Probe lists the pairs to read.
	LinesVerified bool
	// Approaches describes local routes and cover demand, not admitted orders.
	Approaches DefenseApproaches
}

const (
	defenseCorridorLength = 6
	defenseMaxWidth       = 12
	defenseMaxDefenders   = 8
)

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
	if len(r.Protected) > 65536 || len(r.Entrances) > 64 || len(r.Lines) > 4096 {
		return defenseSite{}, errors.New("defense census too large")
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
	if kb.Width < 2 || kb.Width > defenseMaxWidth || !s.inRegion(kb.Entry) {
		return DefenseLayout{}, errors.New("no killbox opening to anchor the corridor on")
	}
	walled := map[domain.Cell]bool{}
	for _, c := range kb.Walled {
		walled[c] = true
	}
	d := directionOf(kb.Toward)
	p := perpendicular(d)
	width, low := int(kb.Width), addCell(kb.Entry, scale(p, -kb.Width/2))
	entry := kb.Entry
	// Lanes: the opening's centre is the trap lane; the safe lane is the
	// passable neighbour across p, preferring the side nearer the home area.
	side := p
	if !s.passable(addCell(entry, p)) || s.passable(addCell(entry, scale(p, -1))) && positive(s.cells[addCell(entry, scale(p, -1))].HomeArea) {
		side = scale(p, -1)
	}
	layout := DefenseLayout{Chokepoint: entry, Toward: rotationOf(d), Width: width, Entry: entry}
	for k := 0; k < defenseCorridorLength; k++ {
		trap := addCell(entry, scale(d, int32(k)))
		safe := addCell(trap, side)
		if !s.passable(trap) || !s.passable(safe) {
			return DefenseLayout{}, errors.New("corridor lane is not passable end to end")
		}
		layout.TrapLane = append(layout.TrapLane, trap)
		layout.SafeLane = append(layout.SafeLane, safe)
	}
	lane := map[domain.Cell]bool{}
	for i := range layout.TrapLane {
		lane[layout.TrapLane[i]], lane[layout.SafeLane[i]] = true, true
	}
	// Funnel: wall every free cell across the chokepoint width outside the
	// two lanes, at the entry row and along both corridor flanks.
	var funnel []domain.Building
	var funnelReserved []domain.Cell
	wall := func(c domain.Cell) error {
		if lane[c] || walled[c] || s.blocking(c) {
			return nil
		}
		if !s.free(c) {
			return errors.New("funnel cell is not buildable")
		}
		b, err := domain.NewBuilding(r.Definitions.Wall, c, domain.North, r.Definitions.WallStuff)
		if err != nil {
			return err
		}
		funnel = append(funnel, b)
		funnelReserved = append(funnelReserved, c)
		return nil
	}
	for at, n := low, 0; n < width; at, n = addCell(at, p), n+1 {
		if err := wall(at); err != nil {
			return DefenseLayout{}, err
		}
	}
	for k := 1; k < defenseCorridorLength; k++ {
		for _, flank := range []domain.Cell{addCell(layout.TrapLane[k], scale(side, -1)), addCell(layout.SafeLane[k], side)} {
			if err := wall(flank); err != nil {
				return DefenseLayout{}, err
			}
		}
	}
	// Trap corridor (#619). Vanilla colonists path over their own traps at
	// no cost (TrapSpike has no pathCost and the player's pawns get no
	// avoid grid), so the corridor is priced for the game's own pathfinder
	// rather than for a pawn that avoids its traps: traps on trap-lane
	// rows 1 and 3 (PlaceWorker_NeverAdjacentTrap refuses a trap beside
	// another, including diagonally) with fences on rows 2 and 4, so the
	// trap lane costs everyone 160; wooden doors on safe-lane rows 1 and
	// 3, which a colonist opens for 38 each while a raider cannot open a
	// player door (150 in PassDoors mode, 300 to bash, otherwise
	// impassable). A door is a full-fill edifice, so the pathfinder also
	// refuses the diagonal steps around it that would let a pawn hop
	// between the trap cells and the free safe-lane rows. The entry pair
	// and the last row stay open. colonistRouteAvoidsTraps proves the
	// result against the same prices below.
	var corridor []domain.Building
	costs := newCorridorCosts()
	for _, c := range funnelReserved {
		costs.closed[c] = true
	}
	place := func(definition, stuff string, c domain.Cell, what string) error {
		if !s.free(c) {
			return errors.New(what + " cell is not buildable")
		}
		b, err := domain.NewBuilding(definition, c, domain.North, stuff)
		if err != nil {
			return err
		}
		corridor = append(corridor, b)
		return nil
	}
	for k := 1; k < defenseCorridorLength-1; k++ {
		t, f := layout.TrapLane[k], layout.SafeLane[k]
		if k%2 == 1 {
			if err := place(r.Definitions.Trap, r.Definitions.TrapStuff, t, "trap"); err != nil {
				return DefenseLayout{}, err
			}
			costs.traps[t] = true
			if err := place(r.Definitions.Door, r.Definitions.DoorStuff, f, "door"); err != nil {
				return DefenseLayout{}, err
			}
			costs.cost[f], costs.full[f] = pathWoodDoorCost, true
			continue
		}
		if err := place(r.Definitions.Fence, r.Definitions.FenceStuff, t, "fence"); err != nil {
			return DefenseLayout{}, err
		}
		costs.cost[t] = pathFenceCost
	}
	// Firing line: sandbags two rows past the corridor exit, floored shooter
	// cells behind them, a free retreat cell behind each shooter, all within
	// MinRange of the entry. Cells are centred on the corridor and spread
	// outward.
	exit := addCell(layout.TrapLane[defenseCorridorLength-1], d)
	coverRow := addCell(exit, scale(d, 2))
	lines := map[[2]domain.Cell]domain.Fact[bool]{}
	for _, l := range r.Lines {
		lines[[2]domain.Cell{l.From, l.To}] = l.LineOfSight
	}
	var line []domain.Building
	var firingReserved []domain.Cell
	minRange, rangeKnown := r.MinRange.Value()
	if rangeKnown && r.Defenders > 0 {
		offsets := []int32{0, 1, -1, 2, -2, 3, -3, 4, -4}
		for _, o := range offsets {
			if len(layout.Firing) >= r.Defenders {
				break
			}
			cover := addCell(coverRow, scale(p, o))
			shooter := addCell(cover, d)
			retreat := addCell(shooter, d)
			if !s.free(cover) || !s.free(shooter) || !s.free(retreat) || lane[cover] || lane[shooter] {
				continue
			}
			if math.Sqrt(float64(squaredDistance(shooter, entry))) > minRange {
				continue
			}
			los, known := lines[[2]domain.Cell{shooter, entry}].Value()
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
	// Every colony entrance, and Home, keeps a route to the map edge once
	// the layout stands, and no cheapest colonist route crosses a trap.
	for _, start := range append([]domain.Cell{s.r.Home}, s.r.Entrances...) {
		if s.passable(start) && !s.colonistRouteAvoidsTraps(costs, start) {
			return DefenseLayout{}, errors.New("layout would cut an entrance off from the map edge or route colonists over a trap")
		}
	}
	// Chokepoint tier reuses existing geometry: it places nothing, reserving
	// the lanes so routine construction never fills the corridor.
	reservedLanes := append(append([]domain.Cell{}, layout.TrapLane...), layout.SafeLane...)
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
	return firing, []domain.Cell{l.Entry}
}

// Tier returns the named tier; ok is false for an unknown name.
func (l DefenseLayout) Tier(name DefenseTierName) (DefenseTier, bool) {
	for _, t := range l.Tiers {
		if t.Name == name {
			return t, true
		}
	}
	return DefenseTier{}, false
}
