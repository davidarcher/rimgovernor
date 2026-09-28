package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"log/slog"
	"os"
	"slices"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// combatMethodPrefix names the ActiveCombat method that owns a fight
// (#852): one plan with no actions, its fight row holding the defenders'
// draft claims (#910). Its orders are DecideCombat's, sent through
// combat.orders at each stop and recorded as the plan's evidence
// (store.RecordCombatStop).
const combatMethodPrefix = "combat-"

// fightAdmission is what the admitting stop decided from: its frame, the
// held layout, the geometry reply and stop it formed with, and the memory
// the formation left.
type fightAdmission struct {
	combat bridge.Combat
	in     combatInputs
	held   domain.Fact[policy.CombatLayout]
	reply  policy.GeometryReply
	stop   policy.StopEvent
	memory policy.CombatMemory
}

// admitFight commits the fight (#910): its empty plan and open fight row
// holding a claim for every defender the formation gave a role, then one
// combat.orders batch on this stop that drafts them all and gives the
// formation's orders. The batch's draft results are the fight's claims;
// the controller releases them once the fight closes (draftSweep).
func (r *RoutineDefensePlanner) admitFight(call, epoch context.Context, incident store.IncidentState, state ControlState, started time.Time, arbiter *stepArbiter, a fightAdmission) (RoutineDefenseResult, error) {
	p := r.reviewer.player
	memory := a.memory
	if len(memory.Roles) == 0 {
		return RoutineDefenseResult{Reason: BuildingMethodNoSquad}, nil
	}
	pawns := make([]domain.PawnID, 0, len(memory.Roles))
	hash := sha256.New()
	fmt.Fprintf(hash, "%s\n", memory.Tactic)
	for _, role := range memory.Roles {
		pawns = append(pawns, role.Pawn)
		fmt.Fprintf(hash, "%s/%s\n", role.Pawn, role.Target)
	}
	if !arbiter.tryClaim(pawns) {
		return RoutineDefenseResult{Reason: BuildingMethodUsed}, nil
	}
	// The threat loadout (#1115) is the fight plan's own equip and wear
	// actions, committed before the first combat.orders batch; its pawns
	// draft once their action settles.
	loadout := r.fightLoadout(call, state, combatView(a.combat, a.in, pawns, a.held), a.in.rows, pawns)
	for _, order := range loadout {
		fmt.Fprintf(hash, "loadout %s/%s/%t\n", order.Pawn, order.Thing, order.Wear)
	}
	method, id := defenseMethodID(strings.TrimSuffix(combatMethodPrefix, "-"), len(incident.Methods), hash), domain.MintPlanID("routine-defense")
	actions, held, err := loadoutActions(id, loadout)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	drafts := make([]domain.PawnID, 0, len(pawns))
	for _, pawn := range pawns {
		if !held[pawn] {
			drafts = append(drafts, pawn)
		}
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineDefenseResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineDefenseResult{}, fmt.Errorf("%w: admitFight: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	world := store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}
	if _, err = p.journal.CommitCombatFight(call, incident.Incident.ID, method, plan, memory, world, drafts); err != nil {
		return RoutineDefenseResult{}, err
	}
	admitted := RoutineDefenseResult{Reason: BuildingMethodAdmitted, Plan: id}
	// The formation's orders as the defenders will be once drafted: the
	// same decision with them orderable. An ask (a rescue path) waits for
	// the next stop; the batch then only drafts.
	view := combatView(a.combat, a.in, drafts, a.held)
	orders, ask, next := policy.DecideCombat(view, a.reply, policy.StopEvent{}, memory)
	if ask != nil {
		orders, next = nil, memory
	}
	if len(drafts)+len(orders) == 0 {
		return admitted, p.journal.SaveCombatMemory(call, id, next)
	}
	results, orders, err := r.sendCombatBatch(call, state, fmt.Sprintf("%s-admit", id), drafts, orders)
	if err != nil {
		// Nothing is known of the batch: the claims stay unknown and the
		// next stop's rows settle them.
		slog.Default().WarnContext(call, "fight admission batch failed", telemetry.ComponentKey, "routine-defense", telemetry.KindKey, "fight_admission", "plan", string(id), "error", err)
		return admitted, nil
	}
	results, next, err = r.recordDrafts(call, id, drafts, results, next)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	record, next := combatStopRecord(view, orders, results, next)
	if len(record.Orders) == 0 {
		return admitted, p.journal.SaveCombatMemory(call, id, next)
	}
	record.Stop = a.stop
	return admitted, p.journal.RecordCombatStop(call, id, record, next)
}

// recordDrafts records a batch's draft results as the fight's claims
// (#910): an applied draft's claim, a refused one dropped with its orders
// forgotten. It returns the remaining (order) results; an uncertain
// receipt (nil results) leaves the claims unknown for the next stop's rows.
func (r *RoutineDefensePlanner) recordDrafts(call context.Context, plan domain.PlanID, pawns []domain.PawnID, results []bridge.CombatOrderResult, memory policy.CombatMemory) ([]bridge.CombatOrderResult, policy.CombatMemory, error) {
	if results == nil || len(pawns) == 0 {
		return results, memory, nil
	}
	set := map[domain.PawnID]string{}
	var drop []domain.PawnID
	for i, pawn := range pawns {
		if results[i].Applied {
			set[pawn] = results[i].Claim
		} else {
			drop = append(drop, pawn)
			memory = memory.Forget(pawn)
		}
	}
	return results[len(pawns):], memory, r.reviewer.player.journal.RecordCombatClaims(call, plan, set, drop)
}

// unclaimedRoles are the live role pawns the fight holds no claim on,
// sorted.
func unclaimedRoles(m policy.CombatMemory, claims map[domain.PawnID]string, view policy.CombatView) []domain.PawnID {
	down := map[domain.PawnID]bool{}
	for _, p := range view.Pawns {
		down[p.ID] = p.Dead || p.Downed
	}
	var out []domain.PawnID
	for _, role := range m.Roles {
		if _, held := claims[role.Pawn]; !held && !down[role.Pawn] && !slices.Contains(out, role.Pawn) {
			out = append(out, role.Pawn)
		}
	}
	slices.Sort(out)
	return out
}

// issueCombatOrders sends one stop's batch, drafting drafts (#911) and
// giving orders, and returns the stop's evidence and the memory without
// the refused orders, which the next stop gives again.
func (r *RoutineDefensePlanner) issueCombatOrders(call context.Context, state ControlState, plan domain.PlanID, view policy.CombatView, drafts []domain.PawnID, orders []policy.CombatOrder, memory policy.CombatMemory) (store.CombatStopRecord, policy.CombatMemory, error) {
	results, orders, err := r.sendCombatBatch(call, state, fmt.Sprintf("%s-stop-%d", plan, view.Tick), drafts, orders)
	if err != nil {
		return store.CombatStopRecord{}, memory, err
	}
	if results, memory, err = r.recordDrafts(call, plan, drafts, results, memory); err != nil {
		return store.CombatStopRecord{}, memory, err
	}
	record, memory := combatStopRecord(view, orders, results, memory)
	return record, memory, nil
}

// combatStopRecord is a stop's evidence from its orders' results and the
// memory without the refused ones; an attack refused cannot_hit is
// remembered from the shooter's cell (#912). An uncertain receipt carries
// no results: the orders stay issued and the next mirror read shows what
// took.
func combatStopRecord(view policy.CombatView, orders []policy.CombatOrder, results []bridge.CombatOrderResult, memory policy.CombatMemory) (store.CombatStopRecord, policy.CombatMemory) {
	cells := map[domain.PawnID]domain.Cell{}
	for _, p := range view.Pawns {
		if c, ok := p.Cell.Value(); ok {
			cells[p.ID] = c
		}
	}
	record := store.CombatStopRecord{Tick: view.Tick}
	for i, order := range orders {
		row := store.CombatOrderRecord{CombatOrder: order, Applied: results == nil}
		if results != nil {
			row.Applied, row.Refusal = results[i].Applied, results[i].Refusal
		}
		if !row.Applied && results != nil && order.Pawn != "" {
			if from, ok := cells[order.Pawn]; ok && row.Refusal == bridge.CombatRefusalCannotHit {
				memory = memory.RefuseHit(order, from)
			} else {
				memory = memory.Forget(order.Pawn)
			}
		}
		record.Orders = append(record.Orders, row)
	}
	return record, memory
}

// sendCombatBatch sends one combat.orders batch under action: a draft
// order for each of drafts (#910), then orders.
// It returns every order's result in that order (nil for an uncertain
// receipt) and the orders sent.
func (r *RoutineDefensePlanner) sendCombatBatch(call context.Context, state ControlState, action string, drafts []domain.PawnID, orders []policy.CombatOrder) ([]bridge.CombatOrderResult, []policy.CombatOrder, error) {
	session, err := r.reviewer.player.journal.Identity(call)
	if err != nil {
		return nil, nil, err
	}
	command := &op.CombatOrders{}
	for _, pawn := range drafts {
		command.Orders = append(command.Orders, &op.CombatOrder{Pawn: &op.EntityPrecondition{EntityId: proto.String(string(pawn))}, Order: &op.CombatOrder_Draft{Draft: &op.Clear{}}})
	}
	for _, order := range orders {
		wire := &op.CombatOrder{Pawn: &op.EntityPrecondition{EntityId: proto.String(string(order.Pawn))}}
		switch order.Kind {
		case policy.OrderMove:
			wire.Order = &op.CombatOrder_Move{Move: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}}
		case policy.OrderAttack:
			wire.Order = &op.CombatOrder_Attack{Attack: &op.EntityPrecondition{EntityId: proto.String(string(order.Target))}}
		case policy.OrderRescue:
			wire.Order = &op.CombatOrder_Rescue{Rescue: &op.CombatRescue{Downed: &op.EntityPrecondition{EntityId: proto.String(string(order.Target))}}}
		case policy.OrderMortar:
			wire.Order = &op.CombatOrder_Mortar{Mortar: &op.CombatMortar{Mortar: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}, Target: &c.Cell{X: proto.Int32(order.Aim.X), Z: proto.Int32(order.Aim.Z)}}}
		case policy.OrderRepair:
			wire.Order = &op.CombatOrder_Repair{Repair: &op.CombatRepair{Cell: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}}}
		case policy.OrderDoor:
			mode := op.CombatDoorMode_COMBAT_DOOR_MODE_FORBID
			switch order.Door {
			case policy.DoorAllow:
				mode = op.CombatDoorMode_COMBAT_DOOR_MODE_ALLOW
			case policy.DoorHoldOpen:
				mode = op.CombatDoorMode_COMBAT_DOOR_MODE_HOLD_OPEN
			case policy.DoorClose:
				mode = op.CombatDoorMode_COMBAT_DOOR_MODE_CLOSE
			}
			wire.Pawn = nil
			wire.Order = &op.CombatOrder_Door{Door: &op.CombatDoor{Cell: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}, Mode: mode.Enum()}}
		case policy.OrderStop:
			wire.Order = &op.CombatOrder_Stop{Stop: &op.Clear{}}
		case policy.OrderFireMode:
			mode := op.CombatFireMode_COMBAT_FIRE_MODE_AT_WILL
			if order.FireMode == policy.HoldFire {
				mode = op.CombatFireMode_COMBAT_FIRE_MODE_HOLD
			}
			wire.Order = &op.CombatOrder_FireMode{FireMode: mode}
		default:
			return nil, nil, fmt.Errorf("%w: sendCombatBatch: case policy.OrderFireMode", ErrControl)
		}
		command.Orders = append(command.Orders, wire)
	}
	pre := &a.WritePrecondition{
		Identity: boundary.Identity(state.Snapshot), ExpectedGeneration: proto.Uint64(uint64(state.Snapshot.Native)),
		Attempt: &c.AttemptKey{ControllerSessionId: proto.String(string(session)), ActionId: proto.String(action), AttemptId: proto.Uint64(1)},
	}
	results, _, _, err := r.native.CombatOrders(call, pre, command)
	if err != nil {
		return nil, nil, err
	}
	return results, orders, nil
}

// answerGeometry answers DecideCombat's geometry ask with one
// combat.geometry read: the ask's named cells scored (#861) and cells
// proposed for its role (#871). A failed or refused read is an answer with
// no proposals and no lines: Formation keeps the layout's firing line and
// attacks go out unchecked.
func (r *RoutineDefensePlanner) answerGeometry(ctx context.Context, identity *c.Identity, ask *policy.GeometryRequest) policy.GeometryReply {
	reply := policy.GeometryReply{Answered: true}
	if ask != nil && ask.Propose == policy.RoleRescuePath {
		return r.answerRescuePath(ctx, identity, ask)
	}
	// Named cells alone are asked with no hostile: their standability
	// (#897, before drop pods open).
	if ask == nil || len(ask.Hostiles) == 0 && (ask.Propose != "" || len(ask.Cells) == 0) {
		return reply
	}
	wire := func(cell domain.Cell) *c.Cell { return &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)} }
	line := make([]*c.Cell, 0, len(ask.Line))
	for _, cell := range ask.Line {
		line = append(line, wire(cell))
	}
	if len(line) >= bridge.CombatGeometryMaxCells {
		line = line[:bridge.CombatGeometryMaxCells-1]
	}
	var propose *mp.CombatGeometryPropose
	switch ask.Propose {
	case policy.RoleCoverBehindLine:
		propose = &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_CoverBehindLine{CoverBehindLine: &mp.CombatCoverBehindLine{Line: line}}}
	case policy.RoleAdjacentToChoke:
		propose = &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_AdjacentToChoke{AdjacentToChoke: &mp.CombatAdjacentToChoke{Choke: wire(ask.Choke), OurSide: wire(ask.OurSide)}}}
	case policy.RoleFiringCells:
		// A walled-in gunner's firing cells (#967).
		targets := make([]*c.Cell, 0, len(ask.Targets))
		for _, cell := range ask.Targets {
			targets = append(targets, wire(cell))
		}
		if len(targets) > bridge.CombatGeometryMaxHostiles {
			targets = targets[:bridge.CombatGeometryMaxHostiles]
		}
		propose = &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_FiringCells{FiringCells: &mp.CombatFiringCells{
			From: wire(ask.From), Radius: proto.Int32(bridge.CombatGeometryMaxRadius), Targets: targets,
		}}}
	}
	reply.Role = ask.Propose
	// The line is named, so its cells carry the game's cover and Formation
	// ranks line and proposals alike (#862); so are the shooters' cells, so
	// their attacks carry lines of fire (#861).
	named := slices.Clone(ask.Line)
	for _, cell := range ask.Cells {
		if !slices.Contains(named, cell) {
			named = append(named, cell)
		}
	}
	limit := bridge.CombatGeometryMaxCells
	if propose != nil {
		limit--
	}
	if len(named) > limit {
		named = named[:limit]
	}
	if propose == nil && len(named) == 0 {
		return reply
	}
	cells := make([]*c.Cell, 0, len(named))
	for _, cell := range named {
		cells = append(cells, wire(cell))
	}
	hostiles := make([]string, 0, len(ask.Hostiles))
	for _, h := range ask.Hostiles {
		hostiles = append(hostiles, string(h))
	}
	request := bridge.CombatGeometryAsk(identity, cells, hostiles, "")
	request.Propose = propose
	geometry, _, err := r.native.CombatGeometry(ctx, request)
	if err != nil {
		slog.Default().InfoContext(ctx, "combat geometry: "+err.Error(), telemetry.ComponentKey, "routine-defense")
		return reply
	}
	// Every proposal is standable by contract; a named cell is when the
	// game says so (#881).
	for _, row := range geometry.GetCells() {
		if cell := row.GetCell(); cell != nil && row.GetStandable() {
			reply.Standable = append(reply.Standable, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
		}
	}
	for _, row := range geometry.GetProposed() {
		if cell := row.GetCell(); cell != nil {
			reply.Proposals = append(reply.Proposals, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
			reply.Standable = append(reply.Standable, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
		}
	}
	for _, row := range append(slices.Clone(geometry.GetCells()), geometry.GetProposed()...) {
		scored := policy.ScoredCell{Cell: domain.Cell{X: row.GetCell().GetX(), Z: row.GetCell().GetZ()}}
		for _, l := range row.GetLines() {
			hostile := domain.PawnID(l.GetHostileId())
			scored.Lines = append(scored.Lines, policy.CoverLine{Hostile: hostile, Cover: l.GetCover(), HostileCover: l.GetHostileCover(), LineOfFire: l.GetLineOfFire()})
			reply.Lines = append(reply.Lines, policy.SightLine{Cell: scored.Cell, Hostile: hostile, LineOfFire: l.GetLineOfFire(), ColonistInPath: l.GetColonistInPath()})
		}
		reply.Scored = append(reply.Scored, scored)
	}
	return reply
}

// answerRescuePath answers a rescue_path ask (#867) with the rescuer's
// route, each cell flagged for hostile line of fire and a door. A failed
// read is an answer with no route: the rescue waits.
func (r *RoutineDefensePlanner) answerRescuePath(ctx context.Context, identity *c.Identity, ask *policy.GeometryRequest) policy.GeometryReply {
	reply := policy.GeometryReply{Answered: true, Role: policy.RoleRescuePath}
	if len(ask.Hostiles) == 0 || ask.Pawn == "" {
		return reply
	}
	hostiles := make([]string, 0, len(ask.Hostiles))
	for _, h := range ask.Hostiles {
		hostiles = append(hostiles, string(h))
	}
	propose := &mp.CombatGeometryPropose{Role: &mp.CombatGeometryPropose_RescuePath{RescuePath: &mp.CombatRescuePath{To: &c.Cell{X: proto.Int32(ask.To.X), Z: proto.Int32(ask.To.Z)}}}}
	geometry, _, err := r.native.CombatGeometry(ctx, bridge.CombatGeometryProposeAsk(identity, propose, hostiles, string(ask.Pawn)))
	if err != nil {
		slog.Default().InfoContext(ctx, "combat rescue path: "+err.Error(), telemetry.ComponentKey, "routine-defense")
		return reply
	}
	for _, row := range geometry.GetProposed() {
		if cell := row.GetCell(); cell != nil {
			reply.Route = append(reply.Route, policy.RouteCell{Cell: domain.Cell{X: cell.GetX(), Z: cell.GetZ()}, LineOfFire: row.GetHostileLineOfFire(), Door: row.GetDoor()})
		}
	}
	return reply
}

// combatPawnStates is the fight's live state: the frame's combat pawns
// (#851, #858) when the frame carries them, else the detail rows
// (position, downed, dead; no stance or target).
func combatPawnStates(combat bridge.Combat, rows map[string]*n.PawnState) []policy.CombatPawnState {
	var out []policy.CombatPawnState
	if len(combat.Pawns) > 0 {
		for _, row := range combat.Pawns {
			s := policy.CombatPawnState{ID: domain.PawnID(row.GetId()), Downed: row.GetDowned(), Dead: row.GetDead(), Target: domain.PawnID(row.GetTargetId()), Stance: combatStance(row.GetStance()),
				Weapon: row.GetWeapon(), WeaponRange: row.GetWeaponRange(), FireMode: row.GetFireMode(),
				Job: row.GetJob(), ShieldBelt: row.GetShieldBelt(), MedicalSkill: int(row.GetMedicalSkill()), MoveSpeed: row.GetMoveSpeed()}
			if cell := row.GetCell(); cell != nil && cell.X != nil && cell.Z != nil {
				s.Cell = domain.Known(domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
			}
			if row.ShieldEnergy != nil {
				s.Shield = domain.Known(row.GetShieldEnergy())
			}
			out = append(out, threatFacts(s, rows[row.GetId()]))
		}
		return out
	}
	ids := make([]string, 0, len(rows))
	for id := range rows {
		ids = append(ids, id)
	}
	sort.Strings(ids)
	for _, id := range ids {
		row := rows[id]
		s := policy.CombatPawnState{ID: domain.PawnID(id), Downed: row.GetDowned(), Dead: row.GetDead()}
		if position := row.GetPawn().GetPosition(); position != nil && position.X != nil && position.Z != nil {
			s.Cell = domain.Known(domain.Cell{X: position.GetX(), Z: position.GetZ()})
		}
		out = append(out, threatFacts(s, row))
	}
	return out
}

// recordCombatStop appends a stop that wrote to the fight's journal, and
// the frame it decided from, to the serve's snapshot stream (#853) when
// recording is on. A failed write is logged.
func recordCombatStop(ctx context.Context, combat bridge.Combat, s snap.CombatStop) {
	dir := os.Getenv(snap.DirEnv)
	if dir == "" {
		return
	}
	if err := snap.RecordCombatStop(dir, combat.Frame, s); err != nil {
		clockEvent(ctx, "defense", "snapshot", "combat stop not recorded: "+err.Error())
	}
}

// threatFacts adds the census row's threat facts (#863): the pawn kind,
// and a sapper or breacher by its lord toil or a mining job.
func threatFacts(s policy.CombatPawnState, row *n.PawnState) policy.CombatPawnState {
	if row == nil {
		return s
	}
	s.Kind = row.GetKindDefName()
	toil := row.GetLordToilClass()
	s.Sapper = strings.Contains(toil, "Sapper") || strings.Contains(toil, "Breach") || row.GetJob().GetDefName() == "Mine"
	return s
}

func combatStance(s mp.CombatStance) policy.CombatStance {
	switch s {
	case mp.CombatStance_COMBAT_STANCE_IDLE:
		return policy.StanceIdle
	case mp.CombatStance_COMBAT_STANCE_WARMUP:
		return policy.StanceWarmup
	case mp.CombatStance_COMBAT_STANCE_COOLDOWN:
		return policy.StanceCooldown
	case mp.CombatStance_COMBAT_STANCE_MOVING:
		return policy.StanceMoving
	case mp.CombatStance_COMBAT_STANCE_MELEE:
		return policy.StanceMelee
	}
	return policy.StanceUnknown
}

// combatStop is the stop being answered: the newest framed combat event
// (#851, #858) of a #849 stop kind after the fight's last decision, or none (the
// first decision, or the tick-budget backstop).
func combatStop(combat bridge.Combat, since domain.Tick) policy.StopEvent {
	var newest *mp.CombatEventRow
	for _, row := range combat.Events {
		if row.Stop == nil || row.GetAt().GetTick() <= int64(since) {
			continue
		}
		if newest == nil || bridge.CombatBefore(newest.GetAt(), row.GetAt()) {
			newest = row
		}
	}
	if newest == nil {
		return policy.StopEvent{}
	}
	return policy.StopEvent{
		Kind: policy.CombatStopKind(strings.ToLower(strings.TrimPrefix(newest.GetStop().String(), "COMBAT_EVENT_"))),
		Pawn: domain.PawnID(newest.GetThingId()), Target: domain.PawnID(newest.GetTargetId()),
	}
}
