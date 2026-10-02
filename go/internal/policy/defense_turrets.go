package policy

import (
	"errors"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DefenseTurretRequest is the powered turret tier's input. Every gate is an
// observation: Available is the native planning definition's availability
// (research and content, never an assumed GunTurrets), DrawW its base draw,
// SpareW the chosen power network's spare watts, Transmitters the conduit
// cells that carry that network, and Stock the colony's stock census. An
// unknown gate places no turret. Definition empty asks for no turret tier.
type DefenseTurretRequest struct {
	Definition, Stuff, Conduit string
	Available                  domain.Fact[bool]
	DrawW, SpareW              domain.Fact[float64]
	Transmitters               []domain.Cell
	Stock                      domain.Fact[map[Resource]int64]
	// Max bounds the turrets placed; zero or negative places none.
	Max int
	// Size is the definition's North footprint (#1210); zero is 1x1.
	Size Bounds
}

// The turret ladder (#1210): the rung is the armory tier (AssessArmory),
// Machining allowing the autocannon and Fabrication the uranium slug
// turret. Each rung's own research and content still gate it through its
// planning definition's availability. The foam turret is not a rung (#1228).
const (
	TurretMini       = "Turret_MiniTurret"
	TurretAutocannon = "Turret_Autocannon"
	TurretSniper     = "Turret_Sniper"
)

// TurretLadder lists the rungs bottom first.
var TurretLadder = []string{TurretMini, TurretAutocannon, TurretSniper}

var turretRungTier = map[string]ArmoryTier{TurretMini: ArmoryTierUnknown, TurretAutocannon: ArmoryTierMachining, TurretSniper: ArmoryTierFabrication}

// TurretRungs are the ladder definitions the armory tier allows, highest
// first; the mini turret is always allowed.
func TurretRungs(tier ArmoryTier) []string {
	var out []string
	for i := len(TurretLadder) - 1; i >= 0; i-- {
		if turretRungTier[TurretLadder[i]] <= tier {
			out = append(out, TurretLadder[i])
		}
	}
	return out
}

// TurretRank is a definition's rung on the ladder, -1 off it.
func TurretRank(definition string) int {
	for i, d := range TurretLadder {
		if d == definition {
			return i
		}
	}
	return -1
}

// TurretFootprint is the cells a turret of the given North size occupies
// anchored at a cell, rotation North (a zero size is 1x1).
func TurretFootprint(anchor domain.Cell, size Bounds) []domain.Cell {
	if size.Width < 1 || size.Height < 1 {
		size = Bounds{Width: 1, Height: 1}
	}
	rect := OccupiedRect(anchor, domain.Cell{X: size.Width, Z: size.Height}, domain.North)
	var out []domain.Cell
	for x := rect.X; x < rect.X+rect.Width; x++ {
		for z := rect.Z; z < rect.Z+rect.Height; z++ {
			out = append(out, domain.Cell{X: x, Z: z})
		}
	}
	return out
}

// footprintGap is the Chebyshev distance between the nearest cells of two
// footprints: edge to edge, so a 2x2 turret keeps the same clearance a
// 1x1 one does.
func footprintGap(a, b []domain.Cell) int32 {
	gap := int32(math.MaxInt32)
	for _, x := range a {
		for _, y := range b {
			gap = min(gap, chebyshev(x, y))
		}
	}
	return gap
}

// DefenseGeometry is the part of a layout the turret tier places against:
// the corridor entry, the kill zone (the trap lane raiders walk) a turret
// must see some cell of, the corridor direction, the shooters' cells and
// every cell the other tiers occupy or reserve. It is derived from a fresh
// layout or from a stored record, so a colony whose layout stood before
// turrets were researched still gets the tier.
type DefenseGeometry struct {
	Entry    domain.Cell
	Approach []domain.Cell
	Toward   domain.Rotation
	Firing   []domain.Cell
	// Lanes are the corridor's cells; no turret or conduit stands on one.
	Lanes    []domain.Cell
	Reserved []domain.Cell
	// Turrets are the turret tier's cells; the mortar tier keeps off and
	// clear of them (#1206).
	Turrets []domain.Cell `json:",omitempty"`
	// Walls are the funnel tier's walls and doorway: no turret stands on
	// one, but a conduit may run beneath it out of the walled kill zone.
	Walls []domain.Cell `json:",omitempty"`
}

type TurretPosition struct {
	Cell     domain.Cell
	Verified bool
}

const (
	TierTurrets DefenseTierName = "turrets"
	// turretSpacing is the minimum Chebyshev distance between two turrets
	// and between a turret and a shooter: a destroyed turret explodes, and
	// a chain of them takes the firing line with it.
	turretSpacing = 3
	// conduitReach is the native PowerConnectionMaker search rect: a
	// consumer connects to the nearest transmitter within six cells, so a
	// chain of conduits need only end that close to the turret.
	conduitReach = 6
	// maxTurretCandidates bounds the positions probed for line of sight; it
	// covers TurretBudget's hard cap so a stocked colony can fill it.
	maxTurretCandidates = turretHardCap
)

func directionOf(r domain.Rotation) domain.Cell {
	switch r {
	case domain.North:
		return directions[0]
	case domain.East:
		return directions[1]
	case domain.South:
		return directions[2]
	}
	return directions[3]
}

// Geometry is the turret tier's view of the layout.
func (l DefenseLayout) Geometry() DefenseGeometry {
	g := DefenseGeometry{Entry: l.Entry, Approach: append([]domain.Cell{}, l.TrapLane...), Toward: l.Toward, Lanes: append([]domain.Cell{}, l.TrapLane...)}
	for _, f := range l.Firing {
		g.Firing = append(g.Firing, f.Cell)
	}
	for _, t := range l.Tiers {
		if t.Name == TierTurrets {
			continue
		}
		g.Reserved = append(g.Reserved, t.Reserved...)
		for _, b := range t.Buildings {
			g.Reserved = append(g.Reserved, b.Cell())
			if t.Name == TierFunnel {
				g.Walls = append(g.Walls, b.Cell())
			}
		}
	}
	return g
}

// DefenseTurrets proposes the powered turret tier for a layout: turrets
// beside the firing positions on the shooters' row or the row behind it,
// spaced against chain explosions, off the lanes and
// every reserved cell, each with a known native line of sight to some cell
// of the kill zone (the funnel walls hide most of the lane from the
// flanks), and a conduit chain from each turret
// to the network unless a transmitter already lies within native connector
// reach. Candidates lists every position considered so the caller can probe
// their lines of fire; the tier holds the verified ones the gates allow.
func DefenseTurrets(r DefenseRequest, g DefenseGeometry) (DefenseTier, []TurretPosition, error) {
	s, err := newDefenseSite(r)
	if err != nil {
		return DefenseTier{}, nil, err
	}
	return s.turrets(g)
}

func (s defenseSite) turrets(g DefenseGeometry) (DefenseTier, []TurretPosition, error) {
	tier := DefenseTier{Name: TierTurrets, Costs: domain.Known([]Amount{})}
	q := s.r.Turret
	if q.Definition == "" {
		return tier, nil, nil
	}
	if q.Conduit == "" || q.Max > 64 {
		return DefenseTier{}, nil, errors.New("invalid turret request")
	}
	if len(g.Firing) == 0 || len(g.Approach) == 0 || !s.inRegion(g.Entry) {
		return tier, nil, nil
	}
	d := directionOf(g.Toward)
	p := perpendicular(d)
	taken := map[domain.Cell]bool{}
	for _, c := range g.Lanes {
		taken[c] = true
	}
	for _, c := range g.Reserved {
		taken[c] = true
	}
	// Firing cells share the shooters' row; their offsets along p from the
	// first one bound the span the turrets flank.
	origin := g.Firing[0]
	low, high := int32(0), int32(0)
	for _, f := range g.Firing {
		o := (f.X-origin.X)*p.X + (f.Z-origin.Z)*p.Z
		low, high = min(low, o), max(high, o)
	}
	lines := map[[2]domain.Cell]domain.Fact[bool]{}
	for _, l := range s.r.Lines {
		lines[[2]domain.Cell{l.From, l.To}] = l.LineOfSight
	}
	var candidates []TurretPosition
	var chosen [][]domain.Cell
	// Every occupied cell must be free and off the lanes and reservations,
	// and the footprint keeps turret spacing edge to edge from the shooters
	// and every other turret (#1210).
	clear := func(c domain.Cell) bool {
		fp := TurretFootprint(c, q.Size)
		for _, x := range fp {
			if !s.free(x) || taken[x] {
				return false
			}
		}
		if footprintGap(fp, g.Firing) < turretSpacing {
			return false
		}
		for _, t := range chosen {
			if footprintGap(fp, t) < turretSpacing {
				return false
			}
		}
		return true
	}
	// Slots flank the firing span, kept turretSpacing apart by clear, on
	// the shooters' row and the row behind it: a mini turret takes the
	// front row first, a heavier rung (the autocannon's minimum range) the
	// back row (#1544).
	// A row's slots end at the kill zone's side wall.
	rows := []domain.Cell{origin, addCell(origin, d)}
	if TurretRank(q.Definition) > 0 {
		rows[0], rows[1] = rows[1], rows[0]
	}
	var positions []domain.Cell
	for _, row := range rows {
		for _, side := range []struct{ edge, dir int32 }{{high, 1}, {low, -1}} {
			for step := int32(1); step <= defenseMaxWidth; step++ {
				c := addCell(row, scale(p, side.edge+side.dir*step))
				if taken[c] || s.blocking(c) {
					break
				}
				positions = append(positions, c)
			}
		}
	}
	for _, c := range positions {
		if len(candidates) >= maxTurretCandidates {
			break
		}
		if !clear(c) {
			continue
		}
		// Verified by any known line to the kill zone; skipped only
		// when every line is known blocked.
		sees, blocked := false, true
		for _, a := range g.Approach {
			los, known := lines[[2]domain.Cell{c, a}].Value()
			sees = sees || known && los
			blocked = blocked && known && !los
		}
		if blocked {
			continue
		}
		candidates = append(candidates, TurretPosition{Cell: c, Verified: sees})
		chosen = append(chosen, TurretFootprint(c, q.Size))
	}
	var verified []domain.Cell
	for _, c := range candidates {
		if c.Verified {
			verified = append(verified, c.Cell)
		}
	}
	n := turretGate(s.r, len(verified))
	if n == 0 {
		return tier, candidates, nil
	}
	transmitters := map[domain.Cell]bool{}
	for _, c := range q.Transmitters {
		transmitters[c] = true
	}
	// Conduits may run under player walls, standing or planned, and cover but never on a lane, a
	// trap cell, a protected walkway or unknown ground.
	underWall := map[domain.Cell]bool{}
	for _, c := range g.Walls {
		underWall[c] = true
	}
	allowed := map[domain.Cell]bool{}
	for c, row := range s.cells {
		if taken[c] && !underWall[c] || s.protect[c] || positive(row.Door) || positive(row.NaturalRock) {
			continue
		}
		if positive(row.Walkable) || positive(row.PlayerOwned) && row.Edifice != "" {
			allowed[c] = true
		}
	}
	for c := range transmitters {
		allowed[c] = true
	}
	// No conduit runs under a turret's footprint.
	for _, t := range verified[:n] {
		for _, c := range TurretFootprint(t, q.Size) {
			delete(allowed, c)
		}
	}
	var buildings []domain.Building
	for _, t := range verified[:n] {
		b, err := domain.NewBuilding(q.Definition, t, domain.North, q.Stuff)
		if err != nil {
			return DefenseTier{}, nil, err
		}
		buildings = append(buildings, b)
		chain, ok := s.conduitChain(t, transmitters, allowed)
		if !ok {
			// No route to the network: the turret would stand dark, so
			// the tier stops at the turrets it can power.
			buildings = buildings[:len(buildings)-1]
			break
		}
		for _, c := range chain {
			b, err := domain.NewBuilding(q.Conduit, c, domain.North, "")
			if err != nil {
				return DefenseTier{}, nil, err
			}
			buildings = append(buildings, b)
			transmitters[c] = true
		}
	}
	// Conduit cost is in the budget only once the chains are known, so the
	// tier is re-checked against stock with them included.
	for len(buildings) > 0 && !affordable(s.r, buildings) {
		buildings = trimLastTurret(buildings, q.Definition)
	}
	// Each turret reserves its whole footprint.
	for _, b := range buildings {
		if b.Definition() == q.Definition {
			tier.Reserved = append(tier.Reserved, TurretFootprint(b.Cell(), q.Size)...)
		}
	}
	tier.Buildings = buildings
	tier.Costs = tierCosts(s.r.UnitCosts, buildings)
	return tier, candidates, nil
}

// TurretGatesOpen reports whether the observed gates (available definition,
// spare watts, stock) allow at least one turret, before any site or line of
// sight is read; a caller re-proposes an empty turret tier only then.
func (r DefenseRequest) TurretGatesOpen() bool { return turretGate(r, 1) > 0 }

// turretGate is how many of the verified positions the power and stock
// gates allow: available definition, spare watts for every turret's draw,
// and stock for the turrets alone (conduits are checked once routed).
func turretGate(r DefenseRequest, verified int) int {
	q := r.Turret
	available, ak := q.Available.Value()
	draw, dk := q.DrawW.Value()
	spare, sk := q.SpareW.Value()
	if !ak || !available || !dk || !sk || draw < 0 || q.Max <= 0 {
		return 0
	}
	n := q.Max
	if n > verified {
		n = verified
	}
	if draw > 0 {
		for n > 0 && float64(n)*draw > spare {
			n--
		}
	}
	for n > 0 {
		var turrets []domain.Building
		for i := 0; i < n; i++ {
			b, err := domain.NewBuilding(q.Definition, domain.Cell{}, domain.North, q.Stuff)
			if err != nil {
				return 0
			}
			turrets = append(turrets, b)
		}
		if affordable(r, turrets) {
			break
		}
		n--
	}
	return n
}

// affordable reports whether the stock census covers the buildings' summed
// unit costs; unknown costs or stock never afford anything.
func affordable(r DefenseRequest, buildings []domain.Building) bool {
	costs, ck := tierCosts(r.UnitCosts, buildings).Value()
	stock, sk := r.Turret.Stock.Value()
	if !ck || !sk {
		return false
	}
	for _, a := range costs {
		if stock[a.Resource] < a.Count {
			return false
		}
	}
	return true
}

// trimLastTurret drops the last turret and the conduits routed for it.
func trimLastTurret(buildings []domain.Building, turret string) []domain.Building {
	last := -1
	for i, b := range buildings {
		if b.Definition() == turret {
			last = i
		}
	}
	if last < 0 {
		return nil
	}
	return buildings[:last]
}

// conduitChain is the shortest conduit run that brings a transmitter within
// native connector reach of the turret, cardinally chained to an existing
// transmitter; empty with ok when one is already in reach.
func (s defenseSite) conduitChain(turret domain.Cell, transmitters, allowed map[domain.Cell]bool) ([]domain.Cell, bool) {
	for c := range transmitters {
		if chebyshev(c, turret) <= conduitReach {
			return nil, true
		}
	}
	if len(transmitters) == 0 {
		return nil, false
	}
	allowed[turret] = true
	path := powerRoute(turret, transmitters, allowed)
	delete(allowed, turret)
	if len(path) < 2 {
		return nil, false
	}
	// powerRoute returns destination first; reverse to turret first.
	for i, j := 0, len(path)-1; i < j; i, j = i+1, j-1 {
		path[i], path[j] = path[j], path[i]
	}
	start := 1
	for i := len(path) - 2; i >= 1; i-- {
		if chebyshev(path[i], turret) <= conduitReach {
			start = i
			break
		}
	}
	return append([]domain.Cell{}, path[start:len(path)-1]...), true
}

// StandingTurret is one tier turret the census found standing, with the
// cells it occupies.
type StandingTurret struct {
	Building domain.Building
	Cells    []domain.Cell
}

// TurretReplacement picks the one standing turret to replace in place with
// the request's higher rung (#1210): the first below the rung whose new
// footprint, anchored on the same cell, covers only its own cells or free
// ground off the lanes and reservations, keeps turret spacing from the
// shooters and every other turret, and whose gates (availability, spare
// watts for the new draw, stock for one) are open. The anchor keeps its
// verified line of fire. ok false replaces nothing.
func TurretReplacement(r DefenseRequest, g DefenseGeometry, standing []StandingTurret) (old, replacement domain.Building, ok bool, err error) {
	q := r.Turret
	rank := TurretRank(q.Definition)
	if rank <= 0 || turretGate(r, 1) == 0 {
		return old, replacement, false, nil
	}
	s, err := newDefenseSite(r)
	if err != nil {
		return old, replacement, false, err
	}
	taken := map[domain.Cell]bool{}
	for _, cells := range [][]domain.Cell{g.Lanes, g.Reserved} {
		for _, c := range cells {
			taken[c] = true
		}
	}
	for i, t := range standing {
		if have := TurretRank(t.Building.Definition()); have < 0 || have >= rank {
			continue
		}
		own := map[domain.Cell]bool{t.Building.Cell(): true}
		for _, c := range t.Cells {
			own[c] = true
		}
		fp := TurretFootprint(t.Building.Cell(), q.Size)
		fits := footprintGap(fp, g.Firing) >= turretSpacing
		for _, c := range fp {
			fits = fits && (own[c] || s.free(c) && !taken[c])
		}
		for j, other := range standing {
			if j != i && fits {
				fits = footprintGap(fp, append([]domain.Cell{other.Building.Cell()}, other.Cells...)) >= turretSpacing
			}
		}
		if !fits {
			continue
		}
		b, err := domain.NewBuilding(q.Definition, t.Building.Cell(), domain.North, q.Stuff)
		if err != nil {
			return old, replacement, false, err
		}
		return t.Building, b, true, nil
	}
	return old, replacement, false, nil
}
