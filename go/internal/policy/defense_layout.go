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
// empty requests the native default material.
type DefenseDefinitions struct {
	Sandbag, SandbagStuff, Wall, WallStuff, Fence, FenceStuff, Trap, TrapStuff string
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
	Tiers              []DefenseTier
	// LinesVerified is false until every firing cell carries a known native
	// line of sight to Entry; Probe lists the pairs to read.
	LinesVerified bool
}

const (
	defenseCorridorLength = 6
	defenseMaxWidth       = 12
	defenseMaxDefenders   = 8
	defenseMaxCells       = 2048
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
	if reg.Width <= 0 || reg.Height <= 0 || reg.X < 0 || reg.Z < 0 || reg.X+reg.Width > r.Bounds.Width || reg.Z+reg.Height > r.Bounds.Height || int64(reg.Width)*int64(reg.Height) > defenseMaxCells {
		return defenseSite{}, errors.New("invalid defense region")
	}
	if len(r.Cells) > defenseMaxCells || len(r.Protected) > 65536 || len(r.Entrances) > 64 || len(r.Lines) > 4096 {
		return defenseSite{}, errors.New("defense census too large")
	}
	if r.Defenders < 0 || r.Defenders > defenseMaxDefenders {
		return defenseSite{}, errors.New("invalid defender count")
	}
	if v, k := r.MinRange.Value(); k && (math.IsNaN(v) || math.IsInf(v, 0) || v <= 0) {
		return defenseSite{}, errors.New("invalid minimum range")
	}
	d := r.Definitions
	for _, name := range []string{d.Sandbag, d.Wall, d.Fence, d.Trap} {
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

// approach is the shortest passable path from Home to the nearest census
// cell that reaches the map edge, ordered home first. Ties resolve by cell
// order so the result is stable across runs.
func (s defenseSite) approach() []domain.Cell {
	if !s.passable(s.r.Home) {
		return nil
	}
	prev := map[domain.Cell]domain.Cell{s.r.Home: s.r.Home}
	queue := []domain.Cell{s.r.Home}
	for len(queue) > 0 {
		c := queue[0]
		queue = queue[1:]
		if row := s.cells[c]; positive(row.EdgeReachable) && c != s.r.Home && s.onBorder(c) {
			var path []domain.Cell
			for at := c; ; at = prev[at] {
				path = append(path, at)
				if at == s.r.Home {
					break
				}
			}
			for i, j := 0, len(path)-1; i < j; i, j = i+1, j-1 {
				path[i], path[j] = path[j], path[i]
			}
			return path
		}
		for _, d := range directions {
			n := addCell(c, d)
			if _, seen := prev[n]; seen || !s.passable(n) {
				continue
			}
			prev[n] = c
			queue = append(queue, n)
		}
	}
	return nil
}
func (s defenseSite) onBorder(c domain.Cell) bool {
	reg := s.r.Region
	return c.X == reg.X || c.Z == reg.Z || c.X == reg.X+reg.Width-1 || c.Z == reg.Z+reg.Height-1
}

// width counts contiguous passable cells across axis d through c, bounded.
func (s defenseSite) width(c, d domain.Cell) (int, domain.Cell) {
	p := perpendicular(d)
	low := c
	for n := 0; n < defenseMaxWidth && s.passable(addCell(low, scale(p, -1))); n++ {
		low = addCell(low, scale(p, -1))
	}
	w := 0
	for at := low; w <= defenseMaxWidth && s.passable(at); at = addCell(at, p) {
		w++
	}
	return w, low
}

// DefenseLayouts proposes a staged corridor defense on the shortest edge
// approach to Home. It returns an error when no chokepoint narrow enough for
// a two-lane corridor exists, and never places on protected, occupied or
// unknown cells.
func DefenseLayouts(r DefenseRequest) (DefenseLayout, error) {
	s, err := newDefenseSite(r)
	if err != nil {
		return DefenseLayout{}, err
	}
	path := s.approach()
	if len(path) < defenseCorridorLength+4 {
		return DefenseLayout{}, errors.New("no edge approach long enough for a corridor")
	}
	// Candidate chokepoints: path cells whose approach direction is straight
	// for the corridor length, ranked by passable width then distance to home.
	type candidate struct {
		index int
		width int
		low   domain.Cell
		d     domain.Cell
	}
	var best *candidate
	for i := defenseCorridorLength + 2; i < len(path)-1; i++ {
		d := domain.Cell{X: path[i-1].X - path[i].X, Z: path[i-1].Z - path[i].Z}
		straight := true
		for k := 1; k < defenseCorridorLength; k++ {
			if addCell(path[i-k], scale(d, -1)) != path[i-k+1] {
				straight = false
				break
			}
		}
		if !straight {
			continue
		}
		w, low := s.width(path[i], d)
		if w < 2 || w > defenseMaxWidth {
			continue
		}
		c := candidate{i, w, low, d}
		if best == nil || c.width < best.width {
			best = &c
		}
	}
	if best == nil {
		return DefenseLayout{}, errors.New("no chokepoint narrow enough for a corridor")
	}
	d, p := best.d, perpendicular(best.d)
	entry := path[best.index]
	// Lanes: the path's own cells are the trap lane; the safe lane is the
	// passable neighbour across p, preferring the side nearer the home area.
	side := p
	if !s.passable(addCell(entry, p)) || s.passable(addCell(entry, scale(p, -1))) && positive(s.cells[addCell(entry, scale(p, -1))].HomeArea) {
		side = scale(p, -1)
	}
	layout := DefenseLayout{Chokepoint: entry, Toward: rotationOf(d), Width: best.width, Entry: entry}
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
		if lane[c] || s.blocking(c) {
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
	for at, n := best.low, 0; n < best.width; at, n = addCell(at, p), n+1 {
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
	// Trap corridor: traps on every other trap-lane row (RimWorld's
	// PlaceWorker_NeverAdjacentTrap refuses a trap beside another, including
	// diagonally, so a contiguous lane cannot be built), fences on the
	// safe-lane cell of the same rows so raiders, who do not see the traps,
	// path through them rather than pay the fence crossing, while colonists,
	// who avoid their own traps, cross the fences. The entry pair stays open.
	var corridor []domain.Building
	trapCells := map[domain.Cell]bool{}
	for k := 1; k < defenseCorridorLength; k += 2 {
		t := layout.TrapLane[k]
		if !s.free(t) {
			return DefenseLayout{}, errors.New("trap cell is not buildable")
		}
		b, err := domain.NewBuilding(r.Definitions.Trap, t, domain.North, r.Definitions.TrapStuff)
		if err != nil {
			return DefenseLayout{}, err
		}
		corridor = append(corridor, b)
		trapCells[t] = true
		{
			f := layout.SafeLane[k]
			if !s.free(f) {
				return DefenseLayout{}, errors.New("fence cell is not buildable")
			}
			b, err := domain.NewBuilding(r.Definitions.Fence, f, domain.North, r.Definitions.FenceStuff)
			if err != nil {
				return DefenseLayout{}, err
			}
			corridor = append(corridor, b)
		}
	}
	// Firing line: sandbags two rows past the corridor exit, shooters behind
	// them, a free retreat cell behind each shooter, all within MinRange of
	// the entry. Cells are centred on the corridor and spread outward.
	exit := addCell(layout.TrapLane[defenseCorridorLength-1], d)
	coverRow := addCell(exit, scale(d, 2))
	lines := map[[2]domain.Cell]domain.Fact[bool]{}
	for _, l := range r.Lines {
		lines[[2]domain.Cell{l.From, l.To}] = l.LineOfSight
	}
	var sandbags []domain.Building
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
			b, err := domain.NewBuilding(r.Definitions.Sandbag, cover, domain.North, r.Definitions.SandbagStuff)
			if err != nil {
				return DefenseLayout{}, err
			}
			sandbags = append(sandbags, b)
			firingReserved = append(firingReserved, cover, shooter, retreat)
			layout.Firing = append(layout.Firing, FiringPosition{Cell: shooter, Cover: cover, Retreat: retreat, Verified: known})
		}
	}
	layout.LinesVerified = len(layout.Firing) > 0
	for _, f := range layout.Firing {
		layout.LinesVerified = layout.LinesVerified && f.Verified
	}
	// Every colony entrance keeps a trap-free passable route to the map edge
	// once walls and traps stand; the safe lane counts as passable.
	if !s.trapFreeRoutes(funnelReserved, trapCells) {
		return DefenseLayout{}, errors.New("layout would cut an entrance off from the map edge")
	}
	// Chokepoint tier reuses existing geometry: it places nothing, reserving
	// the lanes so routine construction never fills the corridor.
	reservedLanes := append(append([]domain.Cell{}, layout.TrapLane...), layout.SafeLane...)
	layout.Tiers = []DefenseTier{
		{Name: TierChokepoint, Reserved: reservedLanes, Costs: domain.Known([]Amount{})},
		{Name: TierFiringLine, Buildings: sandbags, Reserved: firingReserved, Costs: tierCosts(r.UnitCosts, sandbags)},
		{Name: TierFunnel, Buildings: funnel, Reserved: funnelReserved, Costs: tierCosts(r.UnitCosts, funnel)},
		{Name: TierTrapCorridor, Buildings: corridor, Reserved: reservedLanes, Costs: tierCosts(r.UnitCosts, corridor)},
	}
	return layout, nil
}

func (s defenseSite) trapFreeRoutes(walls []domain.Cell, traps map[domain.Cell]bool) bool {
	closed := map[domain.Cell]bool{}
	for _, c := range walls {
		closed[c] = true
	}
	for c := range traps {
		closed[c] = true
	}
	for _, start := range s.r.Entrances {
		seen := map[domain.Cell]bool{start: true}
		queue := []domain.Cell{start}
		found := false
		for len(queue) > 0 && !found {
			c := queue[0]
			queue = queue[1:]
			if positive(s.cells[c].EdgeReachable) && s.onBorder(c) {
				found = true
				break
			}
			for _, d := range directions {
				n := addCell(c, d)
				if seen[n] || closed[n] || !s.passable(n) {
					continue
				}
				seen[n] = true
				queue = append(queue, n)
			}
		}
		if !found {
			return false
		}
	}
	return true
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
// observations_read_lines_of_fire before the firing line is verified.
func (l DefenseLayout) Probe() (firing, approach []domain.Cell) {
	for _, f := range l.Firing {
		firing = append(firing, f.Cell)
	}
	if len(firing) == 0 {
		return nil, nil
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
