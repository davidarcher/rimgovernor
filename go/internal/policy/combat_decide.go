package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DecideCombat (#852) is the fight's per-stop decision: the live combat view
// at a stop, at most one geometry answer, the stop that woke it and the
// fight's memory in; the changed orders, a geometry ask and the next memory
// out. It is pure and deterministic: no clock, no IO, and every input is
// sorted before use, so a shuffled view decides the same orders.
//
// It runs in two phases. Formation (before contact, and again on a raid
// phase change, a breach, a compromised hold or a squad target lost) assigns
// roles: the hold-the-line positions on a complete layout, otherwise the
// tribal or squad assignment, the choice the one-shot planner made. Reaction
// (every stop) turns the roles into orders, changes only: a pawn already
// doing what its role wants gets nothing, and an order that would interrupt
// aim warmup or cooldown is dropped unless it is a retreat or a rescue.
// Drafted pawns on fire-at-will pick their own targets once in place.
func DecideCombat(view CombatView, geometry GeometryReply, stop StopEvent, memory CombatMemory) ([]CombatOrder, *GeometryRequest, CombatMemory) {
	view = view.sorted()
	next := memory.clone()
	next.Tick = view.Tick
	live := view.live()
	// A downed or dead defender keeps no role; its rescue is the rescue
	// planner's (#867), not an order here.
	next.Roles = slices.DeleteFunc(next.Roles, func(r CombatRole) bool { return !live[r.Pawn] })
	if !relieveBlocker(view, stop, &next) && !fallBack(view, stop, &next) && reform(view, stop, next) {
		if !geometry.Answered {
			// Formation asks the game for its candidate cells by role in the
			// stop's one geometry round trip.
			return nil, formationAsk(view), memory
		}
		next.Tactic, next.Roles, next.Refusal = formation(view, geometry)
		next.Formed = view.Tick
	}
	peel(view, stop, &next)
	next.Roles = focusFire(view, next.Roles, memory.Roles)
	orderable := map[domain.PawnID]bool{}
	for _, id := range view.Orderable {
		orderable[id] = true
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	var orders []CombatOrder
	for _, role := range next.Roles {
		if !orderable[role.Pawn] {
			continue
		}
		want, ok := role.want(state[role.Pawn])
		if !ok || next.doing(want, state[role.Pawn]) {
			continue
		}
		if interruptsAim(state[role.Pawn]) && want.Reason != ReasonRetreat && want.Reason != ReasonRescue {
			continue
		}
		orders = append(orders, want)
		next.issue(want, view.Tick)
	}
	return orders, nil, next
}

// CombatStance is a combatant's stance (mirror CombatStance).
type CombatStance string

const (
	StanceUnknown  CombatStance = ""
	StanceIdle     CombatStance = "idle"
	StanceWarmup   CombatStance = "warmup"
	StanceCooldown CombatStance = "cooldown"
	StanceMoving   CombatStance = "moving"
	StanceMelee    CombatStance = "melee"
)

// CombatPawnState is one combatant's live state at the stop (the #851
// SECTION_COMBAT_PAWNS row). Unknown fields stay zero; a zero stance never
// guards an order and a zero cell never proves arrival.
type CombatPawnState struct {
	ID           domain.PawnID
	Cell         domain.Fact[domain.Cell]
	Downed, Dead bool
	Target       domain.PawnID
	Stance       CombatStance
	// Threat facts (#863): the equipped weapon def and its range (0
	// unknown), the pawn kind def, and a sapper or breacher at work.
	Weapon      string
	WeaponRange float64
	Kind        string
	Sapper      bool
}

// CombatLayout is the stored, complete defense layout's line.
type CombatLayout struct {
	Firing []domain.Cell
	// Retreat is the inner line (#860): Retreat[i] is Firing[i]'s fall-back
	// cell, one step further toward Home. Empty on a record that predates it.
	Retreat []domain.Cell
	Toward  domain.Rotation
	// Choke is the corridor's exit cell on our side, where blockers hold
	// (#864); unknown without a corridor.
	Choke domain.Fact[domain.Cell]
}

// CombatView is the fight at one stop. Defenders, Threats and Positional
// are Formation's inputs, the facts the one-shot planner chose from;
// Pawns is the live state Reaction compares against; Orderable are the
// defenders whose drafts the fight's plan owns (the caller's per-plan
// owned-draft check), the only pawns an order may name.
type CombatView struct {
	Tick       domain.Tick
	Pawns      []CombatPawnState
	Defenders  []SquadDefenderFacts
	Threats    []SquadThreatFacts
	Positional []DefensiveThreatFacts
	Layout     domain.Fact[CombatLayout]
	Orderable  []domain.PawnID
}

// CombatStopKind is the #849 event that stopped the clock, lower-cased
// (downed, raid_phase, ...); empty for the first decision or the backstop.
type CombatStopKind string

const (
	StopRaidPhase CombatStopKind = "raid_phase"
	StopBreach    CombatStopKind = "breach"
)

// StopEvent is the stop DecideCombat answers.
type StopEvent struct {
	Kind   CombatStopKind
	Pawn   domain.PawnID
	Target domain.PawnID
}

// FormationRole names a candidate-cell role Formation asks the game to
// propose (#871): cells with cover behind the line, cells adjacent to the
// choke, and firing cells.
type FormationRole string

const (
	RoleCoverBehindLine FormationRole = "cover_behind_line"
	RoleAdjacentToChoke FormationRole = "adjacent_to_choke"
	RoleFiringCells     FormationRole = "firing_cells"
)

// GeometryRequest is DecideCombat's one geometry ask per stop (#871): the
// game proposes candidate cells for one role, scored against the named
// hostiles (bridge.CombatGeometryMaxHostiles at most). Line is the
// cover_behind_line role's anchor.
type GeometryRequest struct {
	Propose  FormationRole
	Line     []domain.Cell
	Hostiles []domain.PawnID
	// Choke and OurSide anchor the adjacent_to_choke role (#864).
	Choke, OurSide domain.Cell
}

// GeometryReply answers a GeometryRequest with the game's proposals, best
// first. Answered with no proposals is a valid answer (the game proposed
// nothing, or the read failed): Formation then keeps the layout's firing
// line alone.
type GeometryReply struct {
	Answered  bool
	Proposals []domain.Cell
	// Scored is the game's cover for the line and proposed cells (#862);
	// Formation ranks its candidate cells by it.
	Scored []ScoredCell `json:",omitempty"`
}

// CombatTactic is the formation a fight runs.
type CombatTactic string

const (
	TacticHold  CombatTactic = "hold"
	TacticSquad CombatTactic = "squad"
)

// CombatRole is one defender's place in the formation: a firing cell to
// hold (hold-the-line) and the hostile to engage.
type CombatRole struct {
	Pawn   domain.PawnID
	Cell   *domain.Cell `json:",omitempty"`
	Target domain.PawnID
	Ranged bool
	// Duty is a brawler's formation duty (#864).
	Duty CombatDuty `json:",omitempty"`
	// Retreat marks a role pulled back to its inner-line cell (#860), or
	// a hurt blocker relieved by the reserve (#864).
	Retreat bool `json:",omitempty"`
}

// CombatOrderKind is the combat.orders order an order becomes.
type CombatOrderKind string

const (
	OrderMove   CombatOrderKind = "move"
	OrderAttack CombatOrderKind = "attack"
)

// CombatOrderReason says why an order was given; retreat and rescue pass
// the aim guard.
type CombatOrderReason string

const (
	ReasonFormation CombatOrderReason = "formation"
	ReasonRetreat   CombatOrderReason = "retreat"
	ReasonRescue    CombatOrderReason = "rescue"
)

// CombatOrder is one changed order.
type CombatOrder struct {
	Pawn   domain.PawnID
	Kind   CombatOrderKind
	Cell   domain.Cell   `json:",omitempty"`
	Target domain.PawnID `json:",omitempty"`
	Reason CombatOrderReason
}

// IssuedOrder is the last order a pawn was given and the tick it went out.
type IssuedOrder struct {
	CombatOrder
	Tick domain.Tick
}

// CombatMemory is the fight's small explicit state between stops.
type CombatMemory struct {
	Tactic CombatTactic `json:",omitempty"`
	Roles  []CombatRole `json:",omitempty"`
	// Refusal is why the last Formation did not hold the line, when it did not.
	Refusal string        `json:",omitempty"`
	Formed  domain.Tick   `json:",omitempty"`
	Issued  []IssuedOrder `json:",omitempty"`
	Tick    domain.Tick   `json:",omitempty"`
}

// Forget drops pawn's last order, so the next stop gives it again (native
// refused it).
func (m CombatMemory) Forget(pawn domain.PawnID) CombatMemory {
	m = m.clone()
	m.Issued = slices.DeleteFunc(m.Issued, func(o IssuedOrder) bool { return o.Pawn == pawn })
	return m
}

func (m CombatMemory) clone() CombatMemory {
	m.Roles = slices.Clone(m.Roles)
	m.Issued = slices.Clone(m.Issued)
	return m
}

func (m *CombatMemory) issue(order CombatOrder, tick domain.Tick) {
	m.Issued = slices.DeleteFunc(m.Issued, func(o IssuedOrder) bool { return o.Pawn == order.Pawn })
	m.Issued = append(m.Issued, IssuedOrder{CombatOrder: order, Tick: tick})
	sort.Slice(m.Issued, func(i, j int) bool { return m.Issued[i].Pawn < m.Issued[j].Pawn })
}

// doing reports whether the pawn is already doing want: at the cell for a
// move, on the target for an attack, or given this same order and not
// seen doing anything else since.
func (m CombatMemory) doing(want CombatOrder, s CombatPawnState) bool {
	switch want.Kind {
	case OrderAttack:
		if s.Target == want.Target {
			return true
		}
	case OrderMove:
		if at, ok := s.Cell.Value(); ok && at == want.Cell {
			return true
		}
	}
	for _, o := range m.Issued {
		if o.Pawn == want.Pawn && o.CombatOrder == want {
			// A live stance that contradicts the order (idle away from the
			// cell) re-issues it; an unknown stance trusts the last order.
			return !(s.Stance == StanceIdle)
		}
	}
	return false
}

// want is the order the role asks of a pawn in state s.
func (r CombatRole) want(s CombatPawnState) (CombatOrder, bool) {
	if r.Cell != nil {
		if at, known := s.Cell.Value(); !known || at != *r.Cell {
			reason := ReasonFormation
			if r.Retreat {
				reason = ReasonRetreat
			}
			return CombatOrder{Pawn: r.Pawn, Kind: OrderMove, Cell: *r.Cell, Reason: reason}, true
		}
	}
	if r.Target == "" {
		return CombatOrder{}, false
	}
	return CombatOrder{Pawn: r.Pawn, Kind: OrderAttack, Target: r.Target, Reason: ReasonFormation}, true
}

// interruptsAim reports a pawn aiming or cooling down: an order now would
// throw the shot away.
func interruptsAim(s CombatPawnState) bool {
	return s.Stance == StanceWarmup || s.Stance == StanceCooldown
}

// reform is the reaction table's formation row: no formation yet, a raid
// phase change or breach, a hold the raid has crossed, or a squad whose
// target is down. Every other stop kind keeps the formation; their own
// responses (pull back when hurt #860, peel #865, rescue #867, scatter)
// are later rows.
func reform(view CombatView, stop StopEvent, m CombatMemory) bool {
	if len(m.Roles) == 0 || stop.Kind == StopRaidPhase || stop.Kind == StopBreach {
		return true
	}
	switch m.Tactic {
	case TacticHold:
		layout, ok := view.Layout.Value()
		return !ok || HoldCompromised(holdLine(layout, m), layout.Toward, view.Positional)
	case TacticSquad:
		down := map[domain.PawnID]bool{}
		for _, t := range view.Threats {
			if positive(t.Dead) || positive(t.Downed) {
				down[domain.PawnID(t.ID)] = true
			}
		}
		for _, p := range view.Pawns {
			if p.Dead || p.Downed {
				down[p.ID] = true
			}
		}
		for _, r := range m.Roles {
			if down[r.Target] {
				return true
			}
		}
	}
	return false
}

// formationAsk asks the game for covered cells behind the layout's firing
// line against the live threats. Without a layout or a live hostile there
// is nothing to ask. adjacent_to_choke and firing_cells have no caller
// yet: the squad formation targets pawns, not cells.
func formationAsk(view CombatView) *GeometryRequest {
	layout, ok := view.Layout.Value()
	if !ok || len(layout.Firing) == 0 {
		return nil
	}
	ask := &GeometryRequest{Propose: RoleCoverBehindLine, Line: slices.Clone(layout.Firing)}
	if choke, ourSide, ok := blockingChoke(view); ok {
		// A blocking formation spends the stop's one proposal on blocker
		// cells; the named line is still scored.
		ask.Propose, ask.Choke, ask.OurSide = RoleAdjacentToChoke, choke, ourSide
	}
	// The top-scored hostiles first (#863), so the cap drops the least urgent.
	for _, h := range rankThreats(view) {
		if len(ask.Hostiles) < maxGeometryHostiles {
			ask.Hostiles = append(ask.Hostiles, h.ID)
		}
	}
	return ask
}

// maxGeometryHostiles is bridge.CombatGeometryMaxHostiles.
const maxGeometryHostiles = 16

// formation is the one-shot planner's choice as the first tactic: hold the
// layout's firing line against an ordinary edge assault, otherwise tribal
// or squad defense. The hold's candidate cells are the layout's firing
// cells first, then the game's covered cells behind the line (#871), so a
// defender the line has no room for still gets a covered cell.
func formation(view CombatView, geometry GeometryReply) (CombatTactic, []CombatRole, string) {
	refusal := "no complete defense layout"
	if layout, ok := view.Layout.Value(); ok {
		cells := slices.Clone(layout.Firing)
		_, _, blocking := blockingChoke(view)
		for _, c := range geometry.Proposals {
			if !blocking && !slices.Contains(cells, c) {
				cells = append(cells, c)
			}
		}
		var positions []DefensivePosition
		cells = RankByCover(cells, geometry.Scored)
		positions, refusal = ExplainDefensivePositions(cells, layout.Toward, view.Positional, view.Defenders)
		if refusal == "" {
			roles := make([]CombatRole, 0, len(positions))
			for _, p := range positions {
				cell := p.Cell
				roles = append(roles, CombatRole{Pawn: p.Defender, Cell: &cell, Target: domain.PawnID(p.Target), Ranged: true})
			}
			roles = append(roles, brawlerRoles(view, blocking, geometry.Proposals)...)
			return TacticHold, sortRoles(roles), ""
		}
	}
	var assignments []SquadAssignment
	var ok bool
	if len(view.Threats) == 1 {
		assignments, ok = SelectTribalRaiderDefense(view.Threats[0], view.Defenders)
	}
	if !ok {
		assignments, ok = SelectSquadDefense(view.Threats, view.Defenders)
	}
	if !ok {
		return "", nil, refusal
	}
	roles := make([]CombatRole, 0, len(assignments))
	for _, a := range assignments {
		roles = append(roles, CombatRole{Pawn: a.Defender, Target: domain.PawnID(a.Target), Ranged: a.Mode == SquadRanged})
	}
	return TacticSquad, sortRoles(roles), refusal
}

func sortRoles(roles []CombatRole) []CombatRole {
	sort.SliceStable(roles, func(i, j int) bool {
		if roles[i].Pawn != roles[j].Pawn {
			return roles[i].Pawn < roles[j].Pawn
		}
		return roles[i].Target < roles[j].Target
	})
	// One role per pawn: an order batch names a pawn once.
	return slices.CompactFunc(roles, func(a, b CombatRole) bool { return a.Pawn == b.Pawn })
}

// live is every defender neither dead nor downed in the live state or the
// formation facts.
func (v CombatView) live() map[domain.PawnID]bool {
	out := map[domain.PawnID]bool{}
	for _, d := range v.Defenders {
		out[d.ID] = !positive(d.Dead) && !positive(d.Downed)
	}
	for _, p := range v.Pawns {
		if p.Dead || p.Downed {
			out[p.ID] = false
		}
	}
	return out
}

// sorted is the view with every list in id order: the selectors take the
// first eligible in input order, so input order must not matter.
func (v CombatView) sorted() CombatView {
	v.Pawns = slices.Clone(v.Pawns)
	sort.SliceStable(v.Pawns, func(i, j int) bool { return v.Pawns[i].ID < v.Pawns[j].ID })
	v.Defenders = slices.Clone(v.Defenders)
	sort.SliceStable(v.Defenders, func(i, j int) bool { return v.Defenders[i].ID < v.Defenders[j].ID })
	v.Threats = slices.Clone(v.Threats)
	sort.SliceStable(v.Threats, func(i, j int) bool { return v.Threats[i].ID < v.Threats[j].ID })
	v.Positional = slices.Clone(v.Positional)
	sort.SliceStable(v.Positional, func(i, j int) bool { return v.Positional[i].ID < v.Positional[j].ID })
	v.Orderable = slices.Clone(v.Orderable)
	slices.Sort(v.Orderable)
	return v
}
