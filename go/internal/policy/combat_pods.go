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

// CombatRoom is one planned room of the colony's layout plan: its floor
// and the doors in its walls.
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
// line (#860) instead. Non-combatants in a landing room or near a landing
// cell move to the landing-free room cell farthest from the pods.
func podFormation(view CombatView, pods PodArrival) []CombatRole {
	at := map[domain.PawnID]domain.Cell{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			at[p.ID] = c
		}
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
	sort.SliceStable(armed, func(i, j int) bool { return near(armed[i].ID) < near(armed[j].ID) })
	if n := min(max(podRespondersPerPod*len(pods.Landing), podRespondersPerPod), maxSquadDefenders); len(armed) > n {
		armed = armed[:n]
	}
	var inner []domain.Cell
	if layout, ok := view.Layout.Value(); ok && len(layout.Retreat) > 0 && podsInside(layout, pods) {
		inner = slices.Clone(layout.Retreat)
	}
	var roles []CombatRole
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
	return append(roles, evacuees(view, pods, civilians, at)...)
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
// is down, or a responder has none while a hostile is out.
func reformPods(view CombatView, m CombatMemory) bool {
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
