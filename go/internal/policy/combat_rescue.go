package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StopDowned is the #849 stop for a colonist downed or killed.
const StopDowned CombatStopKind = "downed"

// RoleRescuePath asks the game for a rescuer's route to a downed pawn
// (#867): the pathfinder's cells, each flagged for hostile line of fire.
const RoleRescuePath FormationRole = "rescue_path"

// Rescue orders (#867): the rescue itself, and a door's forbid or allow.
const (
	OrderRescue CombatOrderKind = "rescue"
	OrderDoor   CombatOrderKind = "door"
)

// DoorMode is a door order's mode.
type DoorMode string

const (
	DoorForbid DoorMode = "forbid"
	DoorAllow  DoorMode = "allow"
)

// rescueDoctorSkill is the Medicine level that makes a rescuer a doctor.
const rescueDoctorSkill = 6

// rescueJob is the vanilla rescue job def the rescue order takes.
const rescueJob = "Rescue"

// RouteCell is one rescue_path cell: hostile line of fire on it, and a
// player door standing on it.
type RouteCell struct {
	Cell       domain.Cell
	LineOfFire bool
	Door       bool
}

// CombatRescue is the rescue under way: the downed defender, the rescuer
// once ordered and the tick it went out, and the doors this rescue forbade.
type CombatRescue struct {
	Patient domain.PawnID
	Rescuer domain.PawnID `json:",omitempty"`
	Ordered domain.Tick   `json:",omitempty"`
	Doors   []domain.Cell `json:",omitempty"`
}

// carrying reports pawn is the rescue's ordered rescuer: its role waits.
func (r *CombatRescue) carrying(pawn domain.PawnID) bool {
	return r != nil && r.Rescuer != "" && r.Rescuer == pawn
}

// rescueStep is the reaction table's rescue row (#867). A downed stop for
// a defender opens a rescue. Each stop until it ends: pick a rescuer (a
// non-combatant doctor with a shield belt first, else the nearest
// non-blocker), ask the game for its route to the patient, and order the
// rescue only when no route cell is under hostile line of fire. A route
// through the fight forbids its doors next to a fire cell and waits, so
// the next stop's route goes around them; a route with no such door waits.
// The rescue ends when the patient is up or dead, or the rescuer's job is
// no longer the rescue at a later stop, and its doors are allowed again.
// It returns the stop's rescue and door orders, or the geometry ask.
func rescueStep(view CombatView, geometry GeometryReply, stop StopEvent, m *CombatMemory, orderable map[domain.PawnID]bool, state map[domain.PawnID]CombatPawnState) ([]CombatOrder, *GeometryRequest) {
	if m.Rescue == nil {
		if stop.Kind != StopDowned || !isDefender(view, stop.Pawn) {
			return nil, nil
		}
		m.Rescue = &CombatRescue{Patient: stop.Pawn}
	}
	r := m.Rescue
	patient, ok := state[r.Patient]
	if !ok || patient.Dead || !patient.Downed {
		return endRescue(m), nil
	}
	if r.Rescuer != "" {
		s, ok := state[r.Rescuer]
		switch {
		case !ok || s.Dead || s.Downed || !orderable[r.Rescuer]:
			r.Rescuer = ""
		case view.Tick > r.Ordered && s.Job != "" && s.Job != rescueJob:
			return endRescue(m), nil
		default:
			return nil, nil
		}
	}
	to, known := patient.Cell.Value()
	rescuer := pickRescuer(view, *m, orderable, state, to, known)
	if rescuer == "" || !known {
		return nil, nil
	}
	order := CombatOrder{Pawn: rescuer, Kind: OrderRescue, Target: r.Patient, Reason: ReasonRescue}
	hostiles := rescueHostiles(view)
	if len(hostiles) == 0 {
		return startRescue(m, order, view.Tick), nil
	}
	if geometry.Role != RoleRescuePath {
		if geometry.Answered {
			// The stop's one geometry read went to Formation.
			return nil, nil
		}
		return nil, &GeometryRequest{Propose: RoleRescuePath, Pawn: rescuer, To: to, Hostiles: hostiles}
	}
	route := geometry.Route
	if len(route) == 0 || !adjacent(route[len(route)-1].Cell, to) {
		// No path, or one cut at the cap: no route is known safe.
		return nil, nil
	}
	if !slices.ContainsFunc(route, func(c RouteCell) bool { return c.LineOfFire }) {
		return startRescue(m, order, view.Tick), nil
	}
	var doors []CombatOrder
	for i, c := range route {
		fire := c.LineOfFire || (i+1 < len(route) && route[i+1].LineOfFire) || (i > 0 && route[i-1].LineOfFire)
		if c.Door && fire && !slices.Contains(r.Doors, c.Cell) {
			r.Doors = append(r.Doors, c.Cell)
			doors = append(doors, CombatOrder{Kind: OrderDoor, Cell: c.Cell, Door: DoorForbid, Reason: ReasonRescue})
		}
	}
	return doors, nil
}

func startRescue(m *CombatMemory, order CombatOrder, tick domain.Tick) []CombatOrder {
	m.Rescue.Rescuer, m.Rescue.Ordered = order.Pawn, tick
	return []CombatOrder{order}
}

// endRescue closes the rescue and allows the doors it forbade.
func endRescue(m *CombatMemory) []CombatOrder {
	var orders []CombatOrder
	for _, c := range m.Rescue.Doors {
		orders = append(orders, CombatOrder{Kind: OrderDoor, Cell: c, Door: DoorAllow, Reason: ReasonRescue})
	}
	m.Rescue = nil
	return orders
}

// pickRescuer is the rescue's pawn: an orderable, live defender other
// than the patient, not holding a choke as a blocker. A non-combatant
// (no weapon, or no target) doctor with a shield belt comes first, the
// best Medicine first; otherwise the nearest to the patient. Ties go by id.
func pickRescuer(view CombatView, m CombatMemory, orderable map[domain.PawnID]bool, state map[domain.PawnID]CombatPawnState, to domain.Cell, known bool) domain.PawnID {
	roles := map[domain.PawnID]CombatRole{}
	for _, r := range m.Roles {
		roles[r.Pawn] = r
	}
	live := view.live()
	type candidate struct {
		id       domain.PawnID
		shielded bool
		medicine int
		distance int64
	}
	var best *candidate
	better := func(a, b candidate) bool {
		if a.shielded != b.shielded {
			return a.shielded
		}
		if a.shielded && a.medicine != b.medicine {
			return a.medicine > b.medicine
		}
		if a.distance != b.distance {
			return a.distance < b.distance
		}
		return a.id < b.id
	}
	for _, id := range view.Orderable {
		s, ok := state[id]
		role := roles[id]
		if !orderable[id] || !live[id] || id == m.Rescue.Patient || !ok || s.Dead || s.Downed || role.Duty == DutyBlocker {
			continue
		}
		c := candidate{id: id, medicine: s.MedicalSkill, distance: 1 << 62}
		c.shielded = s.ShieldBelt && s.MedicalSkill >= rescueDoctorSkill && (s.Weapon == "" || role.Target == "")
		if at, ok := s.Cell.Value(); ok && known {
			dx, dz := int64(at.X-to.X), int64(at.Z-to.Z)
			c.distance = dx*dx + dz*dz
		}
		if best == nil || better(c, *best) {
			best = &c
		}
	}
	if best == nil {
		return ""
	}
	return best.id
}

// rescueHostiles are the live hostiles the route is scored against, the
// top-scored first (#863), at most the geometry cap.
func rescueHostiles(view CombatView) []domain.PawnID {
	var out []domain.PawnID
	for _, h := range rankThreats(view) {
		if len(out) < maxGeometryHostiles {
			out = append(out, h.ID)
		}
	}
	return out
}

func isDefender(view CombatView, id domain.PawnID) bool {
	return id != "" && slices.ContainsFunc(view.Defenders, func(d SquadDefenderFacts) bool { return d.ID == id })
}

func adjacent(a, b domain.Cell) bool {
	return a == b || (abs32(a.X-b.X) <= 1 && abs32(a.Z-b.Z) <= 1)
}
