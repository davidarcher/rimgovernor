package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The burn-out (#1120): wood stools stand around the hive, one molotov
// lights them, and every colonist waits behind at least burnDoors closed
// doors while the insects cook. The layout planner builds the fuel from
// the fight's memory; the fight lights it once the census (CombatView.
// BurnFuel) shows burnFuelStools standing.
const (
	// BurnFuelDef and BurnFuelStuff are the fuel: cheap wood furniture,
	// the #1063 bait stool.
	BurnFuelDef   = "Stool"
	BurnFuelStuff = "WoodLog"
	// BurnFuelRadius bounds the fuel census around the hive (Chebyshev).
	BurnFuelRadius = 3
	burnFuelStools = 4
	burnDoors      = 2
	// burnThrowTicks is how long the carrier keeps its throw before it
	// retreats; burnFireTicks how long the fire is given to take before a
	// cool hive reads as out.
	burnThrowTicks domain.Tick = 300
	burnFireTicks  domain.Tick = 1200
	burnMolotov                = "Weapon_GrenadeMolotov"
)

// CombatBurn is an infestation fight's burn-out: the hive it cooks, the
// room the colonists retreat to, the molotov carrier and the stop it was
// lit (0 while the fuel is short). Done once the fire is out.
type CombatBurn struct {
	Hive    domain.Cell
	Room    Rectangle
	Carrier domain.PawnID
	Lit     domain.Tick `json:",omitempty"`
	Done    bool        `json:",omitempty"`
}

// Fueling reports a burn-out waiting on its fuel: the layout planner's cue.
func (b *CombatBurn) Fueling() bool { return b != nil && b.Lit == 0 && !b.Done }

// retreating reports a lit burn-out still holding its retreat.
func (b *CombatBurn) retreating() bool { return b != nil && b.Lit > 0 && !b.Done }

// BurnFuelRegion is the fuel census rectangle around the hive, as min and
// max corners.
func BurnFuelRegion(hive domain.Cell) (domain.Cell, domain.Cell) {
	return domain.Cell{X: hive.X - BurnFuelRadius, Z: hive.Z - BurnFuelRadius}, domain.Cell{X: hive.X + BurnFuelRadius, Z: hive.Z + BurnFuelRadius}
}

// BurnFuelStanding counts the fuel the census shows within BurnFuelRadius
// of the hive.
func BurnFuelStanding(hive domain.Cell, census map[domain.Cell]WaitDoorCell) int {
	n := 0
	for c, at := range census {
		if at.Edifice == BurnFuelDef && max(abs(c.X-hive.X), abs(c.Z-hive.Z)) <= BurnFuelRadius {
			n++
		}
	}
	return n
}

// BurnFuel is the layout planner's fuel tier: stools on the free walkable
// census cells nearest the hive (then lower x, z), enough to bring the
// standing fuel to burnFuelStools. The hive's own cell is never used.
func BurnFuel(hive domain.Cell, census map[domain.Cell]WaitDoorCell) ([]domain.Building, error) {
	need := burnFuelStools - BurnFuelStanding(hive, census)
	var free []domain.Cell
	for c, at := range census {
		if c != hive && at.Edifice == "" && at.Walkable && max(abs(c.X-hive.X), abs(c.Z-hive.Z)) <= BurnFuelRadius {
			free = append(free, c)
		}
	}
	slices.SortFunc(free, func(a, b domain.Cell) int {
		if d := distance2(a, hive) - distance2(b, hive); d != 0 {
			return int(d)
		}
		if a.X != b.X {
			return int(a.X - b.X)
		}
		return int(a.Z - b.Z)
	})
	var out []domain.Building
	for _, c := range free[:max(0, min(need, len(free)))] {
		b, err := domain.NewBuilding(BurnFuelDef, c, domain.North, BurnFuelStuff)
		if err != nil {
			return nil, err
		}
		out = append(out, b)
	}
	return out, nil
}

// roomDoorDepth is the room/door graph on view.Rooms: the fewest doors
// between from (a room index, -1 outside every room) and each room. A
// room door leads to the room holding the cell beyond it, else outside.
func roomDoorDepth(rooms []CombatRoom, from int) map[int]int {
	at := func(c domain.Cell) int {
		return slices.IndexFunc(rooms, func(r CombatRoom) bool { return r.contains(c) })
	}
	edges := map[int][]int{}
	for i, r := range rooms {
		for _, d := range r.Doors {
			out, ok := outward(r.Interior, d)
			if !ok {
				continue
			}
			j := at(domain.Cell{X: d.X + out.X, Z: d.Z + out.Z})
			edges[i], edges[j] = append(edges[i], j), append(edges[j], i)
		}
	}
	depth := map[int]int{from: 0}
	queue := []int{from}
	for len(queue) > 0 {
		n := queue[0]
		queue = queue[1:]
		for _, m := range edges[n] {
			if _, seen := depth[m]; !seen {
				depth[m] = depth[n] + 1
				queue = append(queue, m)
			}
		}
	}
	return depth
}

// burnRetreatRoom is the room the most doors from the hive, at least
// burnDoors; ties go to the larger room, then the lower index.
func burnRetreatRoom(view CombatView, hive domain.Cell) (Rectangle, bool) {
	from := slices.IndexFunc(view.Rooms, func(r CombatRoom) bool { return r.contains(hive) })
	depth := roomDoorDepth(view.Rooms, from)
	best, bestDepth := -1, burnDoors-1
	for i, r := range view.Rooms {
		d, ok := depth[i]
		if !ok || i == from {
			continue
		}
		if d > bestDepth || d == bestDepth && best >= 0 && r.Interior.Width*r.Interior.Height > view.Rooms[best].Interior.Width*view.Rooms[best].Interior.Height {
			best, bestDepth = i, d
		}
	}
	if best < 0 {
		return Rectangle{}, false
	}
	return view.Rooms[best].Interior, true
}

// burnOut runs the burn-out on an infestation fight with a live hive
// (#1120), after the heat hold. It plans the burn once a molotov carrier
// has the hive in reach and a room lies burnDoors doors from it; fewer
// doors, no burn. While the fuel is short no molotov is thrown. With the
// fuel standing the carrier throws once at the hive, everyone else takes
// the retreat room's cells behind closed, forbidden doors (the #1065
// wait), and the carrier follows after burnThrowTicks. The retreat holds
// while the hive reads hot; once it cools (or reads unknown) past
// burnFireTicks the burn is done and the wait releases so the fight
// re-forms.
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
		room, ok := burnRetreatRoom(view, hive)
		if !ok {
			return
		}
		carrier := slices.IndexFunc(m.Roles, func(r CombatRole) bool {
			s := state[r.Pawn]
			at, known := s.Cell.Value()
			return s.Weapon == burnMolotov && known && dist(at, hive) <= grenadeReach(s)
		})
		if carrier < 0 {
			return
		}
		m.Burn = &CombatBurn{Hive: hive, Room: room, Carrier: m.Roles[carrier].Pawn}
	}
	b := m.Burn
	if b.Done {
		return
	}
	if b.Lit == 0 {
		if fuel, known := view.BurnFuel.Value(); !known || fuel < burnFuelStools {
			// One molotov, into the fuel: none before it stands.
			for i := range m.Roles {
				if state[m.Roles[i].Pawn].Weapon == burnMolotov {
					m.Roles[i].Ground = nil
				}
			}
			return
		}
		if !slices.ContainsFunc(m.Roles, func(r CombatRole) bool { return r.Pawn == b.Carrier }) {
			return
		}
		b.Lit = view.Tick
	} else if temp, known := view.HiveTemperatureC.Value(); view.Tick-b.Lit >= burnFireTicks && (!known || temp <= heatEntryMaxC) {
		b.Done = true
		return
	}
	if !m.Wait {
		m.Wait, m.WaitSince = true, view.Tick
	}
	shelter(view, m)
	var cells []domain.Cell
	for x := b.Room.X; x < b.Room.X+b.Room.Width; x++ {
		for z := b.Room.Z; z < b.Room.Z+b.Room.Height; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	for i := range m.Roles {
		r := &m.Roles[i]
		r.Target, r.Duty, r.Cell, r.Retreat, r.Ground, r.Mortar, r.Aim = "", "", nil, false, nil, nil, nil
		if r.Pawn == b.Carrier && view.Tick-b.Lit < burnThrowTicks {
			if view.Tick == b.Lit {
				h := b.Hive
				r.Ground = &h
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

// burnWaiting reports a lit burn-out holding its retreat: waitTurn keeps
// the wait on for it.
func burnWaiting(m CombatMemory) bool { return m.Burn.retreating() }
