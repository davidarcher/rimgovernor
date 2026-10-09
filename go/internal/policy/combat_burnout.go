package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The burn-out is the infestation's one heat tactic: it
// cooks every insect down together in its own sealed, roofed room. Every
// open cell of a square ring around the hive is walled but one, and from
// that gap a 1-wide corridor of burnCorridorDoors stone doors in series
// runs toward the colony, walled on both flanks. Wood stools stand inside
// the seal, spread out and away from the hive, as fuel. Once the census
// (CombatView.Burn) shows the room roofed and every seal wall, corridor
// door and stool standing, one molotov lights a stool clear of the
// insects (a player's flame on an insect may send the hive to assault)
// and every colonist waits past the last door. While the hive reads under
// burnTopUpC another molotov tops the fire up; none is thrown at or above
// burnMaxC, below the 210 C air burn whose memo also turns the hive.
// Every insect breathes the same air, so heat stroke downs them together
// (about heatStrokeTicks at 150 C); the retreat holds until the frame
// shows every insect downed, then the fight re-enters and finishes them
// before the stroke kills. The doors hold any straggler.
const (
	// BurnFuelDef and BurnFuelStuff are the fuel: cheap wood furniture,
	// the bait stool.
	BurnFuelDef   = "Stool"
	BurnFuelStuff = "WoodLog"
	// BurnWallDef and BurnDoorDef are the seal's walls and corridor doors,
	// built in stone: the block ItemFacts.StoneBlock picks.
	BurnWallDef = "Wall"
	BurnDoorDef = "Door"
	// BurnFuelRadius bounds the fuel around the hive (Chebyshev); the seal
	// ring lies one cell beyond it. No stool stands nearer the hive than
	// burnFuelClear.
	BurnFuelRadius    = 3
	burnFuelClear     = 2
	burnSealRadius    = BurnFuelRadius + 1
	burnCorridorDoors = 3
	// burnRetreatDepth and burnRetreatWidth size the retreat just past the
	// last door.
	burnRetreatDepth = 2
	burnRetreatWidth = 3
	burnFuelStools   = 4
	// burnInsectClear is the least distance from a molotov's target stool
	// to any insect.
	burnInsectClear = 2.5
	// burnThrowTicks is how long the carrier keeps a throw before it
	// retreats; burnFireTicks the least wait between molotovs, and how
	// long a fire with no fuel left is given before a cool hive reads as
	// out.
	burnThrowTicks domain.Tick = 300
	burnFireTicks  domain.Tick = 1200
	// burnTopUpC is the hive temperature below which a molotov goes in;
	// burnMaxC one no molotov is ever thrown at.
	burnTopUpC = 150.0
	burnMaxC   = 200.0
	// heatEntryMaxC is the hive temperature above which no melee fighter
	// walks in at anything but a downed insect.
	heatEntryMaxC = 50.0
	// heatStrokeTicks is how long 150 C air takes to down an insect
	// (HediffGiver_Heat): every insect's comfortable maximum is 60 C, so
	// heat stroke builds above 70 C, by curve(150-70)*6.45e-5 = 0.00335
	// per 60-tick interval, to 0.62 (consciousness 0.1, downed) in about
	// 185 intervals. Death at 1.0 follows some 6.7k ticks later: the
	// window to finish them.
	heatStrokeTicks domain.Tick = 11100
)

// BurnSite is the combat step's census of a burn-out: how many
// seal walls, corridor doors and stools it still lacks, whether its sealed
// room is roofed, and the stools standing.
type BurnSite struct {
	Missing int
	Roofed  bool
	Fuel    []domain.Cell
}

// CombatBurn is an infestation fight's burn-out: the hive it cooks, the
// corridor's direction out of the seal (a unit step), the retreat past the
// last corridor door, the molotov carrier, the stop it was lit (0 while
// the seal or fuel is short) and the stop of its last molotov. Done once
// every insect is down, the fire is out, or the room reads unroofed.
type CombatBurn struct {
	Hive    domain.Cell
	Exit    domain.Cell
	Room    Rectangle
	Carrier domain.PawnID
	Lit     domain.Tick `json:",omitempty"`
	Thrown  domain.Tick `json:",omitempty"`
	Done    bool        `json:",omitempty"`
}

// Fueling reports a burn-out waiting on its seal and fuel: the layout
// planner's cue.
func (b *CombatBurn) Fueling() bool { return b != nil && b.Lit == 0 && !b.Done }

// Active reports a burn-out not yet done: the combat step censuses it.
func (b *CombatBurn) Active() bool { return b != nil && !b.Done }

// retreating reports a lit burn-out still holding its retreat.
func (b *CombatBurn) retreating() bool { return b != nil && b.Lit > 0 && !b.Done }

// step is the cell k steps from the hive along the exit, side steps
// across it.
func (b CombatBurn) step(k, side int32) domain.Cell {
	return domain.Cell{X: b.Hive.X + b.Exit.X*k + b.Exit.Z*side, Z: b.Hive.Z + b.Exit.Z*k + b.Exit.X*side}
}

// CorridorDoors are the corridor's door cells, from the seal's gap out.
func (b CombatBurn) CorridorDoors() []domain.Cell {
	out := make([]domain.Cell, burnCorridorDoors)
	for k := range out {
		out[k] = b.step(burnSealRadius+int32(k), 0)
	}
	return out
}

// BurnRegion is the census rectangle a burn-out reads, as min and max
// corners: its fuel, seal, corridor and retreat.
func BurnRegion(hive domain.Cell) (domain.Cell, domain.Cell) {
	r := int32(burnSealRadius + burnCorridorDoors + burnRetreatDepth)
	return domain.Cell{X: hive.X - r, Z: hive.Z - r}, domain.Cell{X: hive.X + r, Z: hive.Z + r}
}

// burnExit is the unit step from hive toward toward on its dominant axis;
// ties go to z.
func burnExit(hive, toward domain.Cell) domain.Cell {
	dx, dz := toward.X-hive.X, toward.Z-hive.Z
	switch {
	case abs(dx) > abs(dz) && dx > 0:
		return domain.Cell{X: 1}
	case abs(dx) > abs(dz):
		return domain.Cell{X: -1}
	case dz < 0:
		return domain.Cell{Z: -1}
	}
	return domain.Cell{Z: 1}
}

// burnRetreat is the rectangle just past the last corridor door.
func burnRetreat(b CombatBurn) Rectangle {
	near := int32(burnSealRadius + burnCorridorDoors)
	a := b.step(near, -burnRetreatWidth/2)
	z := b.step(near+burnRetreatDepth-1, burnRetreatWidth/2)
	return Rectangle{X: min(a.X, z.X), Z: min(a.Z, z.Z), Width: abs(a.X-z.X) + 1, Height: abs(a.Z-z.Z) + 1}
}

// burnFuelCell reports c a fuel cell of the hive: inside the seal, clear
// of the hive.
func burnFuelCell(hive, c domain.Cell) bool {
	d := chebyshev(c, hive)
	return d >= burnFuelClear && d <= BurnFuelRadius
}

// BurnFuelStanding is the fuel the census shows inside the seal, by cell.
func BurnFuelStanding(hive domain.Cell, census map[domain.Cell]WaitDoorCell) []domain.Cell {
	var out []domain.Cell
	for c, at := range census {
		if at.Edifice == BurnFuelDef && burnFuelCell(hive, c) {
			out = append(out, c)
		}
	}
	slices.SortFunc(out, cellOrder)
	return out
}

// cellOrder orders cells by x, then z.
func cellOrder(a, b domain.Cell) int {
	if a.X != b.X {
		return int(a.X - b.X)
	}
	return int(a.Z - b.Z)
}

// BurnRoofed reports every census cell inside the seal roofed: an
// open sky vents the heat. A cell the census does not carry is not read.
func BurnRoofed(hive domain.Cell, census map[domain.Cell]WaitDoorCell) bool {
	for c, at := range census {
		if chebyshev(c, hive) < burnSealRadius && !at.Roofed {
			return false
		}
	}
	return true
}

// BurnSurvey is the combat step's BurnSite from a census.
func BurnSurvey(b CombatBurn, census map[domain.Cell]WaitDoorCell, items ItemFacts) (BurnSite, error) {
	stone, err := items.StoneBlock(nil)
	if err != nil {
		return BurnSite{}, err
	}
	missing, err := BurnBuilds(b, census, string(stone), items)
	if err != nil {
		return BurnSite{}, err
	}
	return BurnSite{Missing: len(missing), Roofed: BurnRoofed(b.Hive, census), Fuel: BurnFuelStanding(b.Hive, census)}, nil
}

// BurnSeal is the seal and corridor still to build: a stone wall
// on every open (walkable, edifice-free) cell of the ring burnSealRadius
// around the hive except the corridor's gap, a wall on each open flank
// cell beside the doors past the gap, and a stone door on each corridor
// cell not already holding one (a door of other stuff, per the census's
// edifice stuff read, is rebuilt in stone). A ring or flank cell the
// census does not carry is left alone; a door is always asked for.
func BurnSeal(b CombatBurn, census map[domain.Cell]WaitDoorCell, stone string, items ItemFacts) ([]domain.Building, error) {
	doors := b.CorridorDoors()
	var cells []domain.Cell
	for x := b.Hive.X - burnSealRadius; x <= b.Hive.X+burnSealRadius; x++ {
		for z := b.Hive.Z - burnSealRadius; z <= b.Hive.Z+burnSealRadius; z++ {
			if c := (domain.Cell{X: x, Z: z}); chebyshev(c, b.Hive) == burnSealRadius && c != doors[0] {
				cells = append(cells, c)
			}
		}
	}
	for k := int32(1); k < burnCorridorDoors; k++ {
		cells = append(cells, b.step(burnSealRadius+k, -1), b.step(burnSealRadius+k, 1))
	}
	var out []domain.Building
	for _, c := range cells {
		if at, ok := census[c]; ok && at.Edifice == "" && at.Walkable {
			w, err := domain.NewBuilding(BurnWallDef, c, domain.North, stone)
			if err != nil {
				return nil, err
			}
			out = append(out, w)
		}
	}
	for _, c := range doors {
		if at := census[c]; at.Edifice != BurnDoorDef || !items.IsStoneBlocks(Resource(at.Stuff)) {
			d, err := domain.NewBuilding(BurnDoorDef, c, domain.North, stone)
			if err != nil {
				return nil, err
			}
			out = append(out, d)
		}
	}
	return out, nil
}

// BurnBuilds is everything the burn-out still needs standing: its seal
// and corridor, then its fuel. The fight lights on none missing.
func BurnBuilds(b CombatBurn, census map[domain.Cell]WaitDoorCell, stone string, items ItemFacts) ([]domain.Building, error) {
	seal, err := BurnSeal(b, census, stone, items)
	if err != nil {
		return nil, err
	}
	fuel, err := BurnFuel(b.Hive, census)
	return append(seal, fuel...), err
}

// BurnFuel is the fuel still to build, enough to bring the standing fuel
// to burnFuelStools: stools on free walkable census cells inside the seal
// and clear of the hive, spread for an even heat. Each next stool takes
// the free cell farthest from every stool standing or chosen (the first,
// with none, the farthest from the hive), then the farther from the hive,
// then the lower x, z.
func BurnFuel(hive domain.Cell, census map[domain.Cell]WaitDoorCell) ([]domain.Building, error) {
	placed := BurnFuelStanding(hive, census)
	var free []domain.Cell
	for c, at := range census {
		if at.Edifice == "" && at.Walkable && burnFuelCell(hive, c) {
			free = append(free, c)
		}
	}
	slices.SortFunc(free, cellOrder)
	var out []domain.Building
	for len(placed) < burnFuelStools && len(free) > 0 {
		score := func(c domain.Cell) (int64, int64) {
			near := int64(-1)
			for _, p := range placed {
				if d := distance2(c, p); near < 0 || d < near {
					near = d
				}
			}
			return near, distance2(c, hive)
		}
		best := 0
		for i := range free {
			n, h := score(free[i])
			bn, bh := score(free[best])
			if n > bn || n == bn && h > bh {
				best = i
			}
		}
		c := free[best]
		free = slices.Delete(free, best, best+1)
		placed = append(placed, c)
		s, err := domain.NewBuilding(BurnFuelDef, c, domain.North, BurnFuelStuff)
		if err != nil {
			return nil, err
		}
		out = append(out, s)
	}
	return out, nil
}

// burnTarget is the standing stool a molotov lights: the one farthest
// from the nearest insect, at least burnInsectClear from every insect
// (then the lower x, z); none when no stool is clear.
func burnTarget(fuel, insects []domain.Cell) (domain.Cell, bool) {
	var best domain.Cell
	bestNear, found := 0.0, false
	for _, c := range fuel {
		near := -1.0
		for _, i := range insects {
			if d := dist(c, i); near < 0 || d < near {
				near = d
			}
		}
		if near >= 0 && near < burnInsectClear {
			continue
		}
		if near < 0 {
			near = 1e9
		}
		if !found || near > bestNear {
			best, bestNear, found = c, near, true
		}
	}
	return best, found
}

// burnInsects are the infestation's live hostile pawns: their cells, and
// whether every one reads downed.
func burnInsects(view CombatView) ([]domain.Cell, bool) {
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	var cells []domain.Cell
	down := true
	for _, t := range view.Threats {
		s := state[domain.PawnID(t.ID)]
		if t.Building || positive(t.Dead) || s.Dead {
			continue
		}
		if !positive(t.Downed) && !s.Downed {
			down = false
		}
		if c, ok := s.Cell.Value(); ok {
			cells = append(cells, c)
		}
	}
	return cells, down
}

// burnOut runs the burn-out on an infestation fight with a live hive.
// It plans the burn once a molotov carrier has the hive
// in reach, its corridor leading out of the seal toward the carrier.
// While the census is unread, or any seal wall, corridor door or stool is
// missing, no molotov is thrown; a room that reads unroofed ends the
// burn. With all of it standing the carrier lights a stool clear of the
// insects, everyone else takes the retreat past the last door, behind the
// corridor's closed, forbidden doors (while waiting), and the carrier
// follows after burnThrowTicks. While the hive reads under burnTopUpC, at
// most one molotov per burnFireTicks tops the fire up. The retreat holds
// until every insect reads downed (or a fire with no fuel left has
// cooled), then the burn is done, the wait releases and every fighter
// finishes the downed insects.
func burnOut(view CombatView, m *CombatMemory) {
	i := slices.IndexFunc(view.Structures, HostileStructure.hive)
	if m.Tactic != TacticInfestation || i < 0 {
		return
	}
	hive := view.Structures[i].Cell
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	if m.Burn == nil {
		var at domain.Cell
		carrier := slices.IndexFunc(m.Roles, func(r CombatRole) bool {
			s := state[r.Pawn]
			c, known := s.Cell.Value()
			at = c
			return s.WeaponFacts.Burner() && known && dist(c, hive) <= grenadeReach(s)
		})
		if carrier < 0 {
			return
		}
		b := CombatBurn{Hive: hive, Exit: burnExit(hive, at), Carrier: m.Roles[carrier].Pawn}
		b.Room = burnRetreat(b)
		m.Burn = &b
	}
	b := m.Burn
	if b.Done {
		burnFinish(view, m)
		return
	}
	for i := range m.Roles {
		if state[m.Roles[i].Pawn].WeaponFacts.Burner() {
			m.Roles[i].Ground = nil
		}
	}
	site, sited := view.Burn.Value()
	if sited && !site.Roofed {
		b.Done = true
		return
	}
	insects, down := burnInsects(view)
	temp, hot := view.HiveTemperatureC.Value()
	throw := false
	if b.Lit == 0 {
		if !sited || site.Missing > 0 || !slices.ContainsFunc(m.Roles, func(r CombatRole) bool { return r.Pawn == b.Carrier }) {
			return
		}
		throw = !hot || temp < burnTopUpC
		if !throw {
			return
		}
	} else {
		if down {
			b.Done = true
			burnFinish(view, m)
			return
		}
		if sited && len(site.Fuel) == 0 && hot && temp <= heatEntryMaxC && view.Tick-b.Lit >= burnFireTicks {
			b.Done = true
			return
		}
		throw = sited && hot && temp < burnTopUpC && view.Tick-b.Thrown >= burnFireTicks
	}
	var aim domain.Cell
	if throw {
		aim, throw = burnTarget(site.Fuel, insects)
		if !throw && b.Lit == 0 {
			return
		}
	}
	if throw {
		b.Thrown = view.Tick
		if b.Lit == 0 {
			b.Lit = view.Tick
		}
	}
	if !m.Wait {
		m.Wait, m.WaitSince = true, view.Tick
	}
	had := slices.Clone(m.WaitDoors)
	shelter(view, m)
	var want []PodDoor
	for _, d := range b.CorridorDoors() {
		if !slices.ContainsFunc(m.WaitDoors, func(p PodDoor) bool { return p.Cell == d }) {
			want = append(want, PodDoor{Cell: d, Mode: DoorClose}, PodDoor{Cell: d, Mode: DoorForbid})
		}
	}
	m.WaitDoors = append(m.WaitDoors, keepSent(had, want)...)
	cells := rectCells(b.Room)
	for i := range m.Roles {
		r := &m.Roles[i]
		r.Target, r.Duty, r.Cell, r.Retreat, r.Ground, r.Mortar, r.Aim = "", "", nil, false, nil, nil, nil
		if r.Pawn == b.Carrier && view.Tick-b.Thrown < burnThrowTicks {
			if throw {
				c := aim
				r.Ground = &c
			}
			continue
		}
		if len(cells) == 0 {
			continue
		}
		at, _ := state[r.Pawn].Cell.Value()
		k := nearestIndex(at, cells)
		c := cells[k]
		cells = slices.Delete(cells, k, k+1)
		r.Cell, r.Retreat = &c, true
	}
}

// burnFinish sends every fighter at the nearest insect a finished burn
// left downed but alive, before heat stroke kills it.
func burnFinish(view CombatView, m *CombatMemory) {
	if m.Burn.Lit == 0 {
		return
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	var downed []CombatPawnState
	for _, t := range view.Threats {
		s, ok := state[domain.PawnID(t.ID)]
		if _, known := s.Cell.Value(); ok && known && !t.Building && !positive(t.Dead) && !s.Dead && (positive(t.Downed) || s.Downed) {
			downed = append(downed, s)
		}
	}
	if len(downed) == 0 {
		return
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		if m.Rescue.carrying(r.Pawn) {
			continue
		}
		at, known := state[r.Pawn].Cell.Value()
		if !known {
			continue
		}
		best := downed[0]
		for _, d := range downed[1:] {
			c, _ := d.Cell.Value()
			bc, _ := best.Cell.Value()
			if dist(at, c) < dist(at, bc) {
				best = d
			}
		}
		r.Target, r.Duty, r.Cell, r.Retreat, r.Ground, r.Mortar, r.Aim = best.ID, "", nil, false, nil, nil, nil
	}
}

// burnWaiting reports a lit burn-out holding its retreat: waitTurn keeps
// the wait on for it.
func burnWaiting(m CombatMemory) bool { return m.Burn.retreating() }
