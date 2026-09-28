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
	// Contained raiders who will bleed down are left to go down (#1035).
	spared := sparedBleeders(view)
	view = view.spare(spared)
	next := memory.clone()
	next.Tick = view.Tick
	next.CannotHit = keepHitRefusals(view, next.CannotHit)
	live := view.live()
	// A downed or dead defender keeps no role; its rescue is rescueStep's
	// (#867, combat_rescue.go).
	next.Roles = slices.DeleteFunc(next.Roles, func(r CombatRole) bool { return !live[r.Pawn] })
	orderable := map[domain.PawnID]bool{}
	for _, id := range view.Orderable {
		orderable[id] = true
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	if orders, ok := prisonBreakTurn(view, &next, state, orderable); ok {
		// A prison break picks its own tactic (#1080) and answers every stop
		// itself: no geometry, no raid reactions.
		for _, o := range orders {
			next.issue(o, view.Tick)
		}
		return orders, nil, next
	}
	formed := false // a formation this stop picks the potshot door (#900, #1059)
	waitTurn(view, &next)
	siegeTurn(view, &next)
	if pods, ok := view.Pods.Value(); ok && next.Pods == nil {
		// The arrival row may leave a later frame; the fight keeps it (#891).
		next.Pods = &pods
	}
	if next.Pods != nil && (next.Tactic != TacticPods || reform(view, stop, next)) {
		// A pods arrival picks its own tactic (#891), not the squad fallback.
		if ask := podsAsk(view, *next.Pods); ask != nil && !geometry.Answered {
			// The doorway flank cells are used only where the game finds
			// them standable (#897), asked in the stop's one round trip.
			return nil, ask, memory
		}
		var doors []PodDoor
		next.Tactic, next.Refusal = TacticPods, ""
		next.Roles, doors = podFormation(view, *next.Pods, geometry)
		if podWait(view, stop, &next) {
			next.Roles, doors = holdBehindDoors(next.Roles, doors)
		}
		next.PodDoors = keepSent(next.PodDoors, doors)
		next.Formed = view.Tick
	} else if !relieveBlocker(view, stop, &next) && !fallBack(view, stop, &next) && reform(view, stop, next) {
		if ask := formationAsk(view); ask != nil && !geometry.Answered {
			// Formation asks the game for its candidate cells by role in the
			// stop's one geometry round trip; with nothing to ask (no
			// layout) it forms at once.
			return nil, ask, memory
		}
		if ManhunterPack(view) {
			// A manhunter pack picks its own tactic (#898).
			next.Tactic, next.Roles, next.Refusal = TacticManhunter, manhunterFormation(view, geometry, next.Relieved), ""
		} else if mode := siegeMode(view, next); mode != "" {
			// A siege picks its own tactic (#776).
			next.Tactic, next.Roles, next.Refusal = TacticSiege, siegeFormation(view, mode), ""
			next.SiegeMode = mode
		} else if b, ok := predictBreach(view); ok {
			// A sapper or breacher raid posts inside its predicted breach (#913).
			next.Tactic, next.Roles, next.Refusal = TacticSapper, sapperFormation(view, b), ""
			next.SapperBreach = &b.Wall
			next.Rushing = false
		} else {
			next.SapperBreach = nil
			formGeometry := geometry
			if geometry.Role == RoleFiringCells {
				// A flank answer (#1062) holds no cover candidates.
				formGeometry = GeometryReply{Answered: true, Lines: geometry.Lines}
			}
			next.Tactic, next.Roles, next.Refusal = formation(view, formGeometry, next.Relieved)
		}
		next.Formed, formed = view.Tick, true
		next.Flank = nil
	}
	if ask := flankAsk(view, next); ask != nil {
		// The flank detachment's cells take a later stop's round trip (#1062).
		if !geometry.Answered {
			return nil, ask, memory
		}
		if geometry.Role == RoleFiringCells {
			flankDetach(view, geometry, &next)
			geometry.Role = ""
		}
	}
	peel(view, stop, &next)
	pullBackTank(stop, &next)
	if !next.PodWait {
		// A pods fight waiting behind closed doors engages no one (#893).
		next.Roles = focusFire(view, next.Roles, memory.Roles)
	}
	next.Roles = dropMissingTargets(view, next.Roles)
	fromRange(view, &next)
	shipPartHitAndRun(view, &next)
	tribalStandoff(view, &next)
	siegeHold(&next)
	lure(view, &next)
	siegeSnipe(view, &next)
	kite(view, &next)
	pikemenCharge(view, &next)
	explosivesCharge(view, &next)
	sapperIntercept(view, &next)
	sapperRush(view, stop, &next)
	doorPotshot(view, formed, &next)
	shelter(view, &next)
	counterBattery(view, &next)
	rocketClumps(view, &next)
	flank(view, &next)
	rescue, ask := rescueStep(view, geometry, stop, &next, orderable, state)
	if ask != nil {
		return nil, ask, memory
	}
	orders := flankHoldFire(view, orderable, &next)
	for _, role := range next.Roles {
		if !orderable[role.Pawn] || next.Rescue.carrying(role.Pawn) {
			continue
		}
		want, ok := role.want(state[role.Pawn])
		if d := next.PotshotDoor; d != nil && d.Repairer == role.Pawn {
			want, ok = CombatOrder{Pawn: role.Pawn, Kind: OrderRepair, Cell: d.Cell, Reason: ReasonRepair}, true
		}
		if !ok || next.doing(want, state[role.Pawn]) {
			continue
		}
		if interruptsAim(state[role.Pawn]) && want.Reason != ReasonRetreat && want.Reason != ReasonRescue {
			continue
		}
		orders = append(orders, want)
	}
	orders = holdFire(view, slices.DeleteFunc(slices.Clone(next.Roles), func(r CombatRole) bool {
		return next.Rescue.carrying(r.Pawn) || next.Flank.waiting(r.Pawn)
	}), orders, memory, onlySpared(view, spared))
	if !geometry.Answered {
		// The attacks' lines of fire (#861) take the stop's geometry round
		// trip when Formation did not.
		if ask := lineAsk(view, orders, next); ask != nil {
			return nil, ask, memory
		}
	}
	orders, next.Roles, next.CannotHit = clearLines(view, orders, geometry, next.Roles, next)
	// The rescue's orders (#867) lead; a door order names no pawn to issue.
	orders = append(append(rescue, podDoorOrders(&next)...), orders...)
	for _, o := range orders {
		if o.Pawn != "" {
			next.issue(o, view.Tick)
		}
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
	// FireMode is FireAtWill or HoldFire for a drafted colonist, else "".
	FireMode string
	// Threat facts (#863): the equipped weapon def and its range (0
	// unknown), the pawn kind def, and a sapper or breacher at work.
	Weapon      string
	WeaponRange float64
	Kind        string
	Sapper      bool
	// Rescuer facts (#867): the current job def, a worn shield belt and
	// the Medicine skill level.
	Job          string
	ShieldBelt   bool
	MedicalSkill int
	// Shield is the worn shield's charge, a fraction of max (#866);
	// unknown without a shield.
	Shield domain.Fact[float64]
	// MoveSpeed is the pawn's MoveSpeed stat in cells/s, 0 unknown (#898).
	MoveSpeed float64
	// Health summary (#1035): BloodLoss severity, the bleed rate per day
	// and the hours until blood loss kills; unknown when unread.
	BloodLoss, BleedRatePerDay, HoursUntilBleedDeath domain.Fact[float64]
	// Prisoner is a prison-breaking prisoner (COMBAT_SIDE_PRISONER, #1080);
	// Health its summary health fraction.
	Prisoner bool                 `json:",omitempty"`
	Health   domain.Fact[float64] `json:",omitzero"`
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
	// Pods is the frame's drop-pod arrival (#870, #891); Rooms the map's
	// standing rectangular rooms from the frame (#897).
	Pods  domain.Fact[PodArrival]
	Rooms []CombatRoom
	// DamagedDoors are the frame's player doors below max hit points (#900).
	DamagedDoors []domain.Cell `json:",omitempty"`
	// Mortars are the colony's unroofed mortars (#931); Structures the
	// census's standing hostile buildings (#930).
	Mortars    []CombatMortar     `json:",omitempty"`
	Structures []HostileStructure `json:",omitempty"`
	// Population is the colony's colonist count; below
	// domain.PopulationTarget the fight spares contained bleeders (#1035).
	Population domain.Fact[int]
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
	Propose FormationRole
	// Cells are named cells to score: the shooters' cells (#861), their
	// lines of fire answered in GeometryReply.Lines, and Formation's
	// Go-computed cells (#881: tank, peeler and blocking-hold cover cells),
	// their standability answered in GeometryReply.Standable.
	Cells    []domain.Cell
	Line     []domain.Cell
	Hostiles []domain.PawnID
	// Pawn and To are the rescue_path role's walker and goal (#867).
	Pawn domain.PawnID
	To   domain.Cell
	// Choke and OurSide anchor the adjacent_to_choke role (#864).
	Choke, OurSide domain.Cell
	// From and Targets anchor the firing_cells role: a walled-in gunner's
	// cell and the hostiles' cells it needs a line to (#967).
	From    domain.Cell
	Targets []domain.Cell
}

// GeometryReply answers a GeometryRequest with the game's proposals, best
// first. Answered with no proposals is a valid answer (the game proposed
// nothing, or the read failed): Formation then keeps the layout's firing
// line alone.
type GeometryReply struct {
	Answered  bool
	Proposals []domain.Cell
	// Role is the answered ask's role; Route is a rescue_path answer (#867).
	Role  FormationRole
	Route []RouteCell
	// Lines are the named and proposed cells' sight lines to the ask's
	// hostiles.
	Lines []SightLine
	// Scored is the game's cover for the named and proposed cells (#862);
	// Formation ranks its candidate cells by it.
	Scored []ScoredCell `json:",omitempty"`
	// Standable are the named cells the game reported standable, and every
	// proposal (#881). A Go-computed cell is used only when it is here.
	Standable []domain.Cell `json:",omitempty"`
}

// stands reports a cell the reply says is standable.
func (g GeometryReply) stands(c domain.Cell) bool { return slices.Contains(g.Standable, c) }

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
	// Home is a checked standby cell (#881): where a peeler waits between
	// targets, or where a tank pulls back to when its shield breaks.
	Home *domain.Cell `json:",omitempty"`
	// Mortar is the colony mortar the pawn crews and Aim the cell it fires
	// at (#931); set, they win over Cell and Target.
	Mortar *domain.Cell `json:",omitempty"`
	Aim    *domain.Cell `json:",omitempty"`
	// Shell is the shell the mortar fires (#1051), "" whatever is loaded.
	Shell string `json:",omitempty"`
	// Ground is the cell a rocket carrier fires at (#1051); set, it wins
	// over Cell and Target.
	Ground *domain.Cell `json:",omitempty"`
}

// CombatOrderKind is the combat.orders order an order becomes.
type CombatOrderKind string

const (
	OrderMove     CombatOrderKind = "move"
	OrderAttack   CombatOrderKind = "attack"
	OrderFireMode CombatOrderKind = "fire_mode"
	OrderStop     CombatOrderKind = "stop"
)

// CombatOrderReason says why an order was given; retreat and rescue pass
// the aim guard.
type CombatOrderReason string

const (
	ReasonFormation CombatOrderReason = "formation"
	ReasonRetreat   CombatOrderReason = "retreat"
	ReasonRescue    CombatOrderReason = "rescue"
	// ReasonHoldFire is a fire-mode toggle (and its stop) for a hostile in
	// melee with our blockers (#861).
	ReasonHoldFire CombatOrderReason = "hold_fire"
)

// CombatOrder is one changed order.
type CombatOrder struct {
	Pawn   domain.PawnID
	Kind   CombatOrderKind
	Cell   domain.Cell   `json:",omitempty"`
	Target domain.PawnID `json:",omitempty"`
	// FireMode is a fire_mode order's FireAtWill or HoldFire.
	FireMode string `json:",omitempty"`
	Reason   CombatOrderReason
	// Door is a door order's mode (#867); a door order names no pawn.
	Door DoorMode `json:",omitempty"`
	// Aim is a mortar order's target cell (#931); Cell is the mortar's.
	Aim domain.Cell `json:",omitzero"`
	// Shell is a mortar order's shell def (#1051), "" whatever is loaded.
	Shell string `json:",omitempty"`
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
	Refusal string      `json:",omitempty"`
	Formed  domain.Tick `json:",omitempty"`
	// Relieved are the blockers the reserve relieved (#864), in relief
	// order; a re-formation ranks them last among the brawlers (#881), so
	// it keeps the rotation.
	Relieved []domain.PawnID `json:",omitempty"`
	Issued   []IssuedOrder   `json:",omitempty"`
	Tick     domain.Tick     `json:",omitempty"`
	// Rescue is the rescue under way (#867).
	Rescue *CombatRescue `json:",omitempty"`
	// Pods is the drop-pod arrival this fight answers (#891).
	Pods *PodArrival `json:",omitempty"`
	// PodDoors are the doors the pods tactic holds open or shut (#892).
	PodDoors []PodDoor `json:",omitempty"`
	// PodWait is a pods fight holding behind closed doors, outmatched;
	// PodStruck one that struck when the raid fled or looted (#893).
	PodWait   bool `json:",omitempty"`
	PodStruck bool `json:",omitempty"`
	// PotshotDoor is the potshot door of a manhunter pack (#900) or a
	// squad-defense raid (#1059).
	PotshotDoor *PodDoor `json:",omitempty"`
	// Kiter is the manhunter tactic's kiter, Leading once it leads the
	// chaser past the line (#901).
	Kiter   domain.PawnID `json:",omitempty"`
	Leading bool          `json:",omitempty"`
	// Wait is a manhunter fight or humanoid raid sheltering, outmatched,
	// behind the doors WaitDoors closes and forbids (#902, #1065), since
	// WaitSince.
	Wait      bool        `json:",omitempty"`
	WaitSince domain.Tick `json:",omitempty"`
	WaitDoors []PodDoor   `json:",omitempty"`
	// WaitRooms pairs each sheltering room door with its floor cell
	// behind it, for the layout planner's hardening (#1065).
	WaitRooms []WaitDoor `json:",omitempty"`
	// SapperBreach is the wall cell a sapper formation guards (#913).
	SapperBreach *domain.Cell `json:",omitempty"`
	// Intercept is a sapper fight whose gunners went out to the diggers (#914).
	Intercept bool `json:",omitempty"`
	// MechLure is a hold whose gunners wait on the inner line for
	// outranging raiders to close (#922, #1052). The name predates #1052.
	MechLure bool `json:",omitempty"`
	// Rushing is a sapper fight whose posted brawlers rush the breach (#915).
	Rushing bool `json:",omitempty"`
	// SiegeCamp is the tick a siege camp was first seen; SiegeMode the
	// siege tactic's mode at its last formation (#776).
	SiegeCamp domain.Tick `json:",omitempty"`
	SiegeMode SiegeMode   `json:",omitempty"`
	// CannotHit are the attacks native refused cannot_hit (#912), kept
	// while the shooter stands on the cell it was refused from, so the
	// fight retargets or waits instead of re-sending them every stop.
	CannotHit []HitRefusal `json:",omitempty"`
	// Flank is the hold's flanking detachment (#1062).
	Flank *CombatFlank `json:",omitempty"`
	// NoShells are the shells native refused a mortar order for (#1051):
	// none in reach, or not a shell the mortar takes.
	NoShells []string `json:",omitempty"`
}

// RefuseShell is Forget for a mortar order native refused for its shell
// (#1051): the shell is not asked for again this fight.
func (m CombatMemory) RefuseShell(order CombatOrder) CombatMemory {
	m = m.Forget(order.Pawn)
	if order.Shell != "" && !slices.Contains(m.NoShells, order.Shell) {
		m.NoShells = append(m.NoShells, order.Shell)
	}
	return m
}

// HitRefusal is an attack native refused cannot_hit: Pawn could not hit
// Target from From.
type HitRefusal struct {
	Pawn, Target domain.PawnID
	From         domain.Cell
}

// RefuseHit is Forget for an attack native refused cannot_hit from the
// shooter's cell from (#912): the pair is remembered until the shooter
// moves or the target is gone.
func (m CombatMemory) RefuseHit(order CombatOrder, from domain.Cell) CombatMemory {
	m = m.Forget(order.Pawn)
	if order.Kind == OrderAttack && order.Target != "" {
		m.CannotHit = append(m.CannotHit, HitRefusal{Pawn: order.Pawn, Target: order.Target, From: from})
	}
	return m
}

// refusedHit reports an attack on target native refused from pawn's cell.
func (m CombatMemory) refusedHit(pawn, target domain.PawnID, from domain.Cell) bool {
	return slices.Contains(m.CannotHit, HitRefusal{Pawn: pawn, Target: target, From: from})
}

// keepHitRefusals drops the refusals whose shooter moved or whose target
// left the view's threats.
func keepHitRefusals(view CombatView, refusals []HitRefusal) []HitRefusal {
	cells := map[domain.PawnID]domain.Cell{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			cells[p.ID] = c
		}
	}
	present := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		present[domain.PawnID(t.ID)] = true
	}
	out := slices.DeleteFunc(slices.Clone(refusals), func(r HitRefusal) bool {
		c, ok := cells[r.Pawn]
		return !ok || c != r.From || !present[r.Target]
	})
	if len(out) == 0 {
		return nil
	}
	return out
}

// Forget drops pawn's last order, so the next stop gives it again (native
// refused it).
func (m CombatMemory) Forget(pawn domain.PawnID) CombatMemory {
	m = m.clone()
	m.Issued = slices.DeleteFunc(m.Issued, func(o IssuedOrder) bool { return o.Pawn == pawn })
	if m.Rescue != nil && m.Rescue.Rescuer == pawn {
		m.Rescue.Rescuer = ""
	}
	return m
}

func (m CombatMemory) clone() CombatMemory {
	m.Roles = slices.Clone(m.Roles)
	m.Issued = slices.Clone(m.Issued)
	m.Relieved = slices.Clone(m.Relieved)
	if m.Rescue != nil {
		r := *m.Rescue
		r.Doors = slices.Clone(r.Doors)
		m.Rescue = &r
	}
	m.PodDoors = slices.Clone(m.PodDoors)
	m.WaitDoors = slices.Clone(m.WaitDoors)
	m.CannotHit = slices.Clone(m.CannotHit)
	m.NoShells = slices.Clone(m.NoShells)
	m.Flank = m.Flank.clone()
	if m.SapperBreach != nil {
		c := *m.SapperBreach
		m.SapperBreach = &c
	}
	if m.PotshotDoor != nil {
		d := *m.PotshotDoor
		m.PotshotDoor = &d
	}
	if m.Pods != nil {
		p := *m.Pods
		p.Landing = slices.Clone(p.Landing)
		m.Pods = &p
	}
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
	case OrderRepair:
		if s.Job == "Repair" {
			return true
		}
	case OrderMortar:
		// Manning the same mortar at the same aim; the stance of a crew
		// waiting on its gun says nothing.
		return s.Job == "ManTurret" && slices.ContainsFunc(m.Issued, func(o IssuedOrder) bool { return o.Pawn == want.Pawn && o.CombatOrder == want })
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
	if r.Mortar != nil && r.Aim != nil {
		return CombatOrder{Pawn: r.Pawn, Kind: OrderMortar, Cell: *r.Mortar, Aim: *r.Aim, Shell: r.Shell, Reason: ReasonCounterBattery}, true
	}
	if r.Ground != nil {
		return CombatOrder{Pawn: r.Pawn, Kind: OrderAttackGround, Cell: *r.Ground, Reason: ReasonRocketClump}, true
	}
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
	if m.Tactic == TacticSiege || siegeMode(view, m) != "" {
		return reformSiege(view, m)
	}
	if m.Tactic == TacticSapper || reformSapper(view, m) {
		return reformSapper(view, m)
	}
	switch m.Tactic {
	case TacticHold:
		layout, ok := view.Layout.Value()
		return !ok || HoldCompromised(holdLine(layout, m), layout.Toward, unpeeled(view, stop, m))
	case TacticPods:
		return reformPods(view, m)
	case TacticManhunter:
		return reformManhunter(view, m)
	case TacticSquad:
		return squadTargetDown(view, m)
	case TacticPrisonBreak:
		// The escapees are down or gone: whatever is left is a fight anew.
		return true
	}
	return false
}

// dropMissingTargets clears a role's target that is gone from the view's
// threats (fled, despawned): native refuses an attack on it as not_found,
// and re-ordering it every stop only gets refused again (#904). The pawn
// keeps its cell and picks its own target on fire-at-will.
func dropMissingTargets(view CombatView, roles []CombatRole) []CombatRole {
	present := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		present[domain.PawnID(t.ID)] = true
	}
	out := slices.Clone(roles)
	for i := range out {
		if !present[out[i].Target] {
			out[i].Target = ""
		}
	}
	return out
}

// squadTargetDown reports a role whose target is dead or downed.
func squadTargetDown(view CombatView, m CombatMemory) bool {
	down := downPawns(view)
	for _, t := range view.Threats {
		if positive(t.Dead) || positive(t.Downed) {
			down[domain.PawnID(t.ID)] = true
		}
	}
	for _, r := range m.Roles {
		if down[r.Target] {
			return true
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
	// The Go-computed cells come first so the cap drops shooters, not them.
	named := formationChecks(view, layout)
	// Named cells share the cells cap with the proposals: half each at
	// most, or all but the choke's neighbours for a blocking formation.
	limit := maxGeometryCells / 2
	if choke, ourSide, ok := blockingChoke(view); ok {
		// A blocking formation spends the stop's one proposal on blocker
		// cells and names the cells around the line instead, so the game
		// scores their cover for the riflemen (#881).
		ask.Propose, ask.Choke, ask.OurSide = RoleAdjacentToChoke, choke, ourSide
		named = append(named, aroundLine(layout.Firing)...)
		limit = maxGeometryCells - 8 - len(ask.Line)
	}
	for _, c := range append(named, shooterCells(view)...) {
		if len(ask.Cells) < limit && !slices.Contains(ask.Cells, c) {
			ask.Cells = append(ask.Cells, c)
		}
	}
	// The top-scored hostiles first (#863), so the cap drops the least urgent.
	for _, h := range rankThreats(view) {
		if len(ask.Hostiles) < maxGeometryHostiles {
			ask.Hostiles = append(ask.Hostiles, h.ID)
		}
	}
	return ask
}

// formationChecks are the cells Formation computes itself and names for
// the game to check (#881): the peeler's home, and in front of and behind
// each firing cell (a tank's cell and its pull-back cell).
func formationChecks(view CombatView, layout CombatLayout) []domain.Cell {
	var out []domain.Cell
	add := func(c domain.Cell) {
		if c.X >= 0 && c.Z >= 0 && !slices.Contains(out, c) && !slices.Contains(layout.Firing, c) {
			out = append(out, c)
		}
	}
	if home := peelerHome(view); home != nil {
		add(*home)
	}
	if v, ok := towardVector(layout.Toward); ok {
		for _, f := range layout.Firing {
			add(domain.Cell{X: f.X - v.X, Z: f.Z - v.Z})
			add(domain.Cell{X: f.X + v.X, Z: f.Z + v.Z})
		}
	}
	return out
}

// maxGeometryHostiles is bridge.CombatGeometryMaxHostiles.
const maxGeometryHostiles = 16

// formation is the one-shot planner's choice as the first tactic: hold the
// layout's firing line against an ordinary edge assault, otherwise tribal
// or squad defense. The hold's candidate cells are the layout's firing
// cells first, then the game's covered cells behind the line (#871), so a
// defender the line has no room for still gets a covered cell.
func formation(view CombatView, geometry GeometryReply, relieved []domain.PawnID) (CombatTactic, []CombatRole, string) {
	refusal := "no complete defense layout"
	if layout, ok := view.Layout.Value(); ok {
		cells := slices.Clone(layout.Firing)
		_, _, blocking := blockingChoke(view)
		candidates := geometry.Proposals
		if blocking {
			// The proposals are blocker cells; the riflemen's covered
			// cells are the named cells around the line the game found
			// standable and covered (#881).
			candidates = coveredAround(layout.Firing, geometry)
		}
		for _, c := range candidates {
			if !slices.Contains(cells, c) {
				cells = append(cells, c)
			}
		}
		var positions []DefensivePosition
		if explosive := explosiveHostiles(view); len(explosive) > 0 {
			// Room to move over cover, out of the blasts' reach (#1054).
			cells = explosiveCells(view, cells, explosive)
		} else {
			cells = RankByCover(cells, geometry.Scored)
		}
		cells = spaceCells(cells, firingGap(view))
		defenders, tanks := splitTanks(view)
		positions, refusal = explainDefensivePositions(cells, layout.Toward, chokeHeld(view, layout), markMechs(view), defenders)
		if refusal == "" {
			roles := make([]CombatRole, 0, len(positions))
			for _, p := range positions {
				cell := p.Cell
				roles = append(roles, CombatRole{Pawn: p.Defender, Cell: &cell, Target: domain.PawnID(p.Target), Ranged: true})
			}
			roles = append(roles, brawlerRoles(view, defenders, blocking, geometry, relieved)...)
			roles = append(roles, tankRoles(tanks, positions, layout.Toward, geometry)...)
			return TacticHold, sortRoles(roles), ""
		}
	}
	var assignments []SquadAssignment
	var ok bool
	if len(view.Threats) == 1 {
		assignments, ok = SelectTribalRaiderDefense(view.Threats[0], view.Defenders)
	}
	if !ok {
		assignments, ok = SelectSquadDefense(markSquadMechs(view), view.Defenders)
	}
	if !ok {
		if roles := shelterRoles(view); len(roles) > 0 {
			return TacticShelter, roles, refusal
		}
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
