package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TacticPods is the drop-pod raid's own formation (#891): a pods arrival
// picks it instead of the generic squad fallback.
const TacticPods CombatTactic = "pods"

// DutyEvacuee is a non-combatant moved away from the landing cells (#891).
const DutyEvacuee CombatDuty = "evacuee"

// PodArrival is a drop-pod raid's arrival (#870): the cells the pods land
// on and the tick the last pod opens (spawn + 520).
type PodArrival struct {
	Landing []domain.Cell
	Open    domain.Tick
}

// CombatRoom is one standing room on the map (#897): its floor and the
// doors in its walls.
type CombatRoom struct {
	Interior Rectangle
	Doors    []domain.Cell `json:",omitempty"`
}

// contains reports c on the room's floor.
func (r CombatRoom) contains(c domain.Cell) bool {
	in := r.Interior
	return c.X >= in.X && c.X < in.X+in.Width && c.Z >= in.Z && c.Z < in.Z+in.Height
}

// Pod response constants (#891).
const (
	// podRespondersPerPod is how many armed colonists answer each landing cell.
	podRespondersPerPod = 2
	// podDangerRadius is how close to a landing cell a non-combatant is
	// moved away, in cells.
	podDangerRadius = 12
)

// podFormation is the pods tactic's roles (#891): the nearest armed
// colonists respond, each on the nearest live hostile once one is out;
// with a landing cell inside the perimeter they take the layout's inner
// line (#860) instead. Two responders flank each door of a landing room
// on its far side, the door held open (#892), close-range fighters first.
// Non-combatants in a landing room or near a landing cell move to the
// landing-free room cell farthest from the pods.
func podFormation(view CombatView, pods PodArrival, geometry GeometryReply) ([]CombatRole, []PodDoor) {
	at := map[domain.PawnID]domain.Cell{}
	reach := map[domain.PawnID]float64{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			at[p.ID] = c
		}
		reach[p.ID] = p.WeaponRange
	}
	var armed, civilians []SquadDefenderFacts
	for _, d := range view.Defenders {
		switch {
		case squadDefenderEligible(d) && positive(d.Armed):
			armed = append(armed, d)
		case evacuable(d):
			civilians = append(civilians, d)
		}
	}
	// Nearest to a landing cell first; an unknown cell ranks last.
	near := func(id domain.PawnID) int64 {
		if c, ok := at[id]; ok {
			return nearestDistance(c, pods.Landing)
		}
		return 1 << 62
	}
	var roles []CombatRole
	var doors []PodDoor
	taken := map[domain.PawnID]bool{}
	pool := closeRange(armed, reach, near)
	for _, door := range landingDoors(view, pods) {
		held := false
		for _, cell := range door.slots {
			if !geometry.stands(cell) {
				continue
			}
			if len(pool) == 0 {
				break
			}
			d := pool[0]
			pool = pool[1:]
			taken[d.ID] = true
			c := cell
			roles = append(roles, CombatRole{Pawn: d.ID, Cell: &c, Target: nearestHostile(view, c), Ranged: positive(d.RangedEquipped), Duty: DutyDoorway})
			held = true
		}
		if held {
			doors = append(doors, PodDoor{Cell: door.cell, Mode: DoorHoldOpen})
		}
	}
	armed = slices.DeleteFunc(armed, func(d SquadDefenderFacts) bool { return taken[d.ID] })
	sort.SliceStable(armed, func(i, j int) bool { return near(armed[i].ID) < near(armed[j].ID) })
	n := max(min(max(podRespondersPerPod*len(pods.Landing), podRespondersPerPod), maxSquadDefenders)-len(taken), 0)
	if len(armed) > n {
		armed = armed[:n]
	}
	var inner []domain.Cell
	if layout, ok := view.Layout.Value(); ok && len(layout.Retreat) > 0 && podsInside(layout, pods) {
		inner = slices.Clone(layout.Retreat)
	}
	for _, d := range armed {
		role := CombatRole{Pawn: d.ID, Target: nearestHostile(view, at[d.ID]), Ranged: positive(d.RangedEquipped)}
		if len(inner) > 0 {
			i := nearestIndex(at[d.ID], inner)
			cell := inner[i]
			inner = slices.Delete(inner, i, i+1)
			role.Cell, role.Retreat = &cell, true
		}
		roles = append(roles, role)
	}
	return sortRoles(append(roles, evacuees(view, pods, civilians, at)...)), doors
}

// DutyDoorway holds a cell beside a landing room's door (#892).
const DutyDoorway CombatDuty = "doorway"

// PodDoor is a door the pods tactic wants in a mode, and whether that
// order went out.
type PodDoor struct {
	Cell domain.Cell
	Mode DoorMode
	Sent bool `json:",omitempty"`
	// Repairer is the door gunner sent to repair the manhunter potshot
	// door (#900), while it is damaged and no animal is near.
	Repairer domain.PawnID `json:",omitempty"`
}

// closeRange ranks the armed for the doorway slots (#892): melee-only
// brawlers first, best armored first, then the ranged by shortest weapon
// range (a shotgun before a rifle), an unknown range last; nearest the
// pods on a tie.
func closeRange(armed []SquadDefenderFacts, reach map[domain.PawnID]float64, near func(domain.PawnID) int64) []SquadDefenderFacts {
	melee := func(d SquadDefenderFacts) bool { return positive(d.MeleeEquipped) && !positive(d.RangedEquipped) }
	rng := func(d SquadDefenderFacts) float64 {
		if r := reach[d.ID]; r > 0 {
			return r
		}
		return 1 << 20
	}
	out := byArmor(slices.Clone(armed))
	sort.SliceStable(out, func(i, j int) bool {
		mi, mj := melee(out[i]), melee(out[j])
		if mi != mj {
			return mi
		}
		if mi {
			return false // byArmor's order
		}
		if ri, rj := rng(out[i]), rng(out[j]); ri != rj {
			return ri < rj
		}
		if ni, nj := near(out[i].ID), near(out[j].ID); ni != nj {
			return ni < nj
		}
		return out[i].ID < out[j].ID
	})
	return out
}

// podDoorway is a landing room's door and the cells flanking it on its
// far side.
type podDoorway struct {
	cell  domain.Cell
	slots []domain.Cell
}

// landingDoors are the doors of every room holding a landing cell, each
// with the two far-side cells beside the cell straight out that are in no
// landing room and on no room's wall.
func landingDoors(view CombatView, pods PodArrival) []podDoorway {
	landing := func(c domain.Cell) bool {
		for _, r := range view.Rooms {
			if r.contains(c) && slices.ContainsFunc(pods.Landing, r.contains) {
				return true
			}
		}
		return false
	}
	wall := func(c domain.Cell) bool {
		for _, r := range view.Rooms {
			in := r.Interior
			ring := c.X >= in.X-1 && c.X <= in.X+in.Width && c.Z >= in.Z-1 && c.Z <= in.Z+in.Height && !r.contains(c)
			if ring && !slices.Contains(r.Doors, c) {
				return true
			}
		}
		return false
	}
	var out []podDoorway
	for _, r := range view.Rooms {
		if !slices.ContainsFunc(pods.Landing, r.contains) {
			continue
		}
		for _, door := range r.Doors {
			n, ok := outward(r.Interior, door)
			if !ok || slices.ContainsFunc(out, func(p podDoorway) bool { return p.cell == door }) {
				continue
			}
			o := domain.Cell{X: door.X + n.X, Z: door.Z + n.Z}
			t := domain.Cell{X: n.Z, Z: n.X}
			w := podDoorway{cell: door}
			for _, c := range []domain.Cell{{X: o.X + t.X, Z: o.Z + t.Z}, {X: o.X - t.X, Z: o.Z - t.Z}} {
				if c.X >= 0 && c.Z >= 0 && !landing(c) && !wall(c) {
					w.slots = append(w.slots, c)
				}
			}
			out = append(out, w)
		}
	}
	return out
}

// podsAsk is a pods formation's geometry ask (#897): every doorway flank
// cell named for its standability, then the shooters' cells for their
// lines of fire, against the live hostiles (none before the pods open).
// Without a landing-room door there is nothing to ask.
func podsAsk(view CombatView, pods PodArrival) *GeometryRequest {
	ask := &GeometryRequest{}
	for _, door := range landingDoors(view, pods) {
		for _, c := range door.slots {
			if !slices.Contains(ask.Cells, c) {
				ask.Cells = append(ask.Cells, c)
			}
		}
	}
	if len(ask.Cells) == 0 {
		return nil
	}
	for _, h := range rankThreats(view) {
		if len(ask.Hostiles) < maxGeometryHostiles {
			ask.Hostiles = append(ask.Hostiles, h.ID)
		}
	}
	if len(ask.Hostiles) > 0 {
		for _, c := range shooterCells(view) {
			if !slices.Contains(ask.Cells, c) {
				ask.Cells = append(ask.Cells, c)
			}
		}
	}
	if len(ask.Cells) > maxGeometryCells {
		ask.Cells = ask.Cells[:maxGeometryCells]
	}
	return ask
}

// outward is the unit step from a room's interior through its wall at
// door; false for a cell not on a side of the wall ring.
func outward(in Rectangle, door domain.Cell) (domain.Cell, bool) {
	insideX := door.X >= in.X && door.X < in.X+in.Width
	insideZ := door.Z >= in.Z && door.Z < in.Z+in.Height
	switch {
	case door.X == in.X-1 && insideZ:
		return domain.Cell{X: -1}, true
	case door.X == in.X+in.Width && insideZ:
		return domain.Cell{X: 1}, true
	case door.Z == in.Z-1 && insideX:
		return domain.Cell{Z: -1}, true
	case door.Z == in.Z+in.Height && insideX:
		return domain.Cell{Z: 1}, true
	}
	return domain.Cell{}, false
}

// podDoorOrders are the door orders the pods tactic (and the manhunter
// potshot door, #900) has not sent yet.
func podDoorOrders(m *CombatMemory) []CombatOrder {
	var out []CombatOrder
	send := func(d *PodDoor) {
		if !d.Sent {
			out = append(out, CombatOrder{Kind: OrderDoor, Cell: d.Cell, Door: d.Mode, Reason: ReasonFormation})
			d.Sent = true
		}
	}
	for i := range m.PodDoors {
		send(&m.PodDoors[i])
	}
	for i := range m.WaitDoors {
		send(&m.WaitDoors[i])
	}
	if m.ManhunterDoor != nil {
		send(m.ManhunterDoor)
	}
	return out
}

// keepSent is want with each door already sent in the same mode marked
// sent, so a re-formation does not repeat it.
func keepSent(had, want []PodDoor) []PodDoor {
	for i := range want {
		for _, h := range had {
			if h.Cell == want[i].Cell && h.Mode == want[i].Mode && h.Sent {
				want[i].Sent = true
			}
		}
	}
	return want
}

// evacuable is a colonist the pods tactic moves but never fights with:
// alive, up, sane and free, but unarmed or incapable of violence.
func evacuable(d SquadDefenderFacts) bool {
	busy, bk := squadDraftBusy(d)
	return !positive(d.Dead) && !positive(d.Downed) && !positive(d.MentalState) && bk && !busy &&
		(!positive(d.ViolenceCapable) || !positive(d.Armed))
}

// evacuees are the non-combatants' moves: each one in a landing room or
// within podDangerRadius of a landing cell takes the free cell of a
// landing-free room farthest from the pods. Without such a room nobody
// moves.
func evacuees(view CombatView, pods PodArrival, civilians []SquadDefenderFacts, at map[domain.PawnID]domain.Cell) []CombatRole {
	var safe []domain.Cell
	landingRoom := func(c domain.Cell) bool {
		for _, r := range view.Rooms {
			if r.contains(c) && slices.ContainsFunc(pods.Landing, r.contains) {
				return true
			}
		}
		return false
	}
	for _, r := range view.Rooms {
		if slices.ContainsFunc(pods.Landing, r.contains) {
			continue
		}
		in := r.Interior
		for z := in.Z; z < in.Z+in.Height; z++ {
			for x := in.X; x < in.X+in.Width; x++ {
				safe = append(safe, domain.Cell{X: x, Z: z})
			}
		}
	}
	// Farthest from the pods first, then by cell.
	sort.SliceStable(safe, func(i, j int) bool {
		di, dj := nearestDistance(safe[i], pods.Landing), nearestDistance(safe[j], pods.Landing)
		if di != dj {
			return di > dj
		}
		return cellLess(safe[i], safe[j])
	})
	var roles []CombatRole
	for _, d := range civilians {
		c, ok := at[d.ID]
		if !ok || len(safe) == 0 || !landingRoom(c) && nearestDistance(c, pods.Landing) > podDangerRadius*podDangerRadius {
			continue
		}
		cell := safe[0]
		safe = safe[1:]
		roles = append(roles, CombatRole{Pawn: d.ID, Cell: &cell, Duty: DutyEvacuee})
	}
	return roles
}

// podsInside reports a landing cell at or behind the layout's firing line:
// the pods came down inside the perimeter.
func podsInside(layout CombatLayout, pods PodArrival) bool {
	for _, c := range pods.Landing {
		if BehindFiringLine(layout.Firing, layout.Toward, c) {
			return true
		}
	}
	return false
}

// nearestHostile is the live hostile pawn nearest from; "" with none out
// yet or from unknown.
func nearestHostile(view CombatView, from domain.Cell) domain.PawnID {
	down := map[domain.PawnID]bool{}
	for _, p := range view.Pawns {
		if p.Dead || p.Downed {
			down[p.ID] = true
		}
	}
	var best domain.PawnID
	bestD := int64(-1)
	for _, t := range view.Positional {
		c, ok := t.Position.Value()
		id := domain.PawnID(t.ID)
		if !ok || positive(t.Dead) || positive(t.Downed) || down[id] {
			continue
		}
		if d := distance2(from, c); bestD < 0 || d < bestD {
			best, bestD = id, d
		}
	}
	return best
}

// reformPods is the pods tactic's re-formation row: a responder's target
// is down, or a responder has none while a hostile is out. A fight
// waiting behind closed doors (#893) re-forms only on a raid phase change.
func reformPods(view CombatView, m CombatMemory) bool {
	if m.PodWait {
		return false
	}
	for _, r := range m.Roles {
		if r.Duty == DutyEvacuee {
			continue
		}
		if r.Target == "" {
			if nearestHostile(view, domain.Cell{}) != "" {
				return true
			}
			continue
		}
		for _, p := range view.Pawns {
			if p.ID == r.Target && (p.Dead || p.Downed) {
				return true
			}
		}
		for _, t := range view.Positional {
			if domain.PawnID(t.ID) == r.Target && (positive(t.Dead) || positive(t.Downed)) {
				return true
			}
		}
	}
	return false
}

func distance2(a, b domain.Cell) int64 {
	dx, dz := int64(a.X-b.X), int64(a.Z-b.Z)
	return dx*dx + dz*dz
}

// nearestDistance is the squared distance from c to the nearest of cells.
func nearestDistance(c domain.Cell, cells []domain.Cell) int64 {
	if len(cells) == 0 {
		return 1 << 62
	}
	return distance2(c, cells[nearestIndex(c, cells)])
}

// nearestIndex is the index of the cell nearest c, the first on a tie.
func nearestIndex(c domain.Cell, cells []domain.Cell) int {
	best := 0
	for i := range cells {
		if distance2(c, cells[i]) < distance2(c, cells[best]) {
			best = i
		}
	}
	return best
}
