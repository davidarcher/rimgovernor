package policy

import (
	"errors"

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
	// Lanes are the corridor lanes; the safe lane is the civilian corridor
	// and no turret or conduit ever stands on either.
	Lanes    []domain.Cell
	Reserved []domain.Cell
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
	// maxTurretCandidates bounds the positions probed for line of sight.
	maxTurretCandidates = 4
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
	g := DefenseGeometry{Entry: l.Entry, Approach: append([]domain.Cell{}, l.TrapLane...), Toward: l.Toward, Lanes: append(append([]domain.Cell{}, l.TrapLane...), l.SafeLane...)}
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
		}
	}
	return g
}

// DefenseTurrets proposes the powered turret tier for a layout: turrets
// behind the shooters' row in line with the lane, then on the row outside
// the firing positions, spaced against chain explosions, off the lanes and
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
	if q.Conduit == "" || len(q.Transmitters) > 4096 || q.Max > 64 || len(g.Firing) > 64 || len(g.Lanes) > 128 || len(g.Approach) > 128 || len(g.Reserved) > 4096 {
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
	var chosen []domain.Cell
	clear := func(c domain.Cell) bool {
		if !s.free(c) || taken[c] {
			return false
		}
		for _, f := range g.Firing {
			if chebyshev(c, f) < turretSpacing {
				return false
			}
		}
		for _, t := range chosen {
			if chebyshev(c, t) < turretSpacing {
				return false
			}
		}
		return true
	}
	// Positions behind the shooters' row in line with the kill zone come
	// first: the funnel walls hide most of the corridor from the flanks,
	// while a turret looking straight up the lane past the firing line
	// covers its whole length. The flanks of the firing span follow.
	var positions []domain.Cell
	behind := addCell(origin, scale(d, turretSpacing))
	seenOffset := map[int32]bool{}
	for _, a := range g.Approach {
		o := (a.X-origin.X)*p.X + (a.Z-origin.Z)*p.Z
		for _, o := range []int32{o, o + turretSpacing, o - turretSpacing} {
			if !seenOffset[o] {
				seenOffset[o] = true
				positions = append(positions, addCell(behind, scale(p, o)))
			}
		}
	}
	for step := int32(0); step < defenseMaxWidth; step++ {
		for _, o := range []int32{high + turretSpacing + step*turretSpacing, low - turretSpacing - step*turretSpacing} {
			positions = append(positions, addCell(origin, scale(p, o)))
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
		chosen = append(chosen, c)
	}
	var verified []domain.Cell
	for _, c := range candidates {
		if c.Verified {
			verified = append(verified, c.Cell)
		}
	}
	n := turretBudget(s.r, len(verified))
	if n == 0 {
		return tier, candidates, nil
	}
	transmitters := map[domain.Cell]bool{}
	for _, c := range q.Transmitters {
		transmitters[c] = true
	}
	// Conduits may run under player walls and cover but never on a lane, a
	// trap cell, a protected walkway or unknown ground.
	allowed := map[domain.Cell]bool{}
	for c, row := range s.cells {
		if taken[c] || s.protect[c] || positive(row.Door) || positive(row.NaturalRock) {
			continue
		}
		if positive(row.Walkable) || positive(row.PlayerOwned) && row.Edifice != "" {
			allowed[c] = true
		}
	}
	for c := range transmitters {
		allowed[c] = true
	}
	var buildings []domain.Building
	for _, t := range verified[:n] {
		b, err := domain.NewBuilding(q.Definition, t, domain.North, q.Stuff)
		if err != nil {
			return DefenseTier{}, nil, err
		}
		buildings = append(buildings, b)
		tier.Reserved = append(tier.Reserved, t)
		chain, ok := s.conduitChain(t, transmitters, allowed)
		if !ok {
			// No route to the network: the turret would stand dark, so
			// the tier stops at the turrets it can power.
			buildings = buildings[:len(buildings)-1]
			tier.Reserved = tier.Reserved[:len(tier.Reserved)-1]
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
		tier.Reserved = tier.Reserved[:len(tier.Reserved)-1]
	}
	tier.Buildings = buildings
	tier.Costs = tierCosts(s.r.UnitCosts, buildings)
	return tier, candidates, nil
}

// TurretGatesOpen reports whether the observed gates (available definition,
// spare watts, stock) allow at least one turret, before any site or line of
// sight is read; a caller re-proposes an empty turret tier only then.
func (r DefenseRequest) TurretGatesOpen() bool { return turretBudget(r, 1) > 0 }

// turretBudget is how many of the verified positions the power and stock
// gates allow: available definition, spare watts for every turret's draw,
// and stock for the turrets alone (conduits are checked once routed).
func turretBudget(r DefenseRequest, verified int) int {
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
