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
// (#852): one plan of owned drafts for the formation's defenders. Its
// orders are DecideCombat's, sent through combat.orders at each stop and
// recorded as the plan's evidence (store.RecordCombatStop).
const combatMethodPrefix = "combat-"

// admitFight commits the fight's plan: an owned draft for every defender
// the formation gave a role, and the formation as the fight's memory.
// Orders follow at the stops once the drafts are held.
func (r *RoutineDefensePlanner) admitFight(call, epoch context.Context, goal store.GoalState, state ControlState, started time.Time, arbiter *stepArbiter, memory policy.CombatMemory) (RoutineDefenseResult, error) {
	p := r.reviewer.player
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
	method, id := defenseMethodIDs(strings.TrimSuffix(combatMethodPrefix, "-"), goal, hash)
	actions := make([]domain.Action, 0, len(pawns))
	for _, pawn := range pawns {
		draft, err := domain.NewOwnedDraft(pawn)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		action, err := domain.NewOwnedDraftAction(domain.ActionID(fmt.Sprintf("%s-draft-%s", id, pawn)), draft)
		if err != nil {
			return RoutineDefenseResult{}, err
		}
		actions = append(actions, action)
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
		return RoutineDefenseResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineDefenseResult{}, err
	}
	if err = p.journal.OpenCombatFight(call, id, memory); err != nil {
		return RoutineDefenseResult{}, err
	}
	return RoutineDefenseResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// issueCombatOrders sends one stop's orders as one combat.orders batch and
// returns the stop's evidence and the memory without the refused orders,
// which the next stop gives again.
func (r *RoutineDefensePlanner) issueCombatOrders(call context.Context, state ControlState, plan domain.PlanID, tick domain.Tick, orders []policy.CombatOrder, memory policy.CombatMemory) (store.CombatStopRecord, policy.CombatMemory, error) {
	session, err := r.reviewer.player.journal.Identity(call)
	if err != nil {
		return store.CombatStopRecord{}, memory, err
	}
	if len(orders) > bridge.MaxCombatOrders {
		orders = orders[:bridge.MaxCombatOrders]
	}
	command := &op.CombatOrders{}
	for _, order := range orders {
		wire := &op.CombatOrder{Pawn: &op.EntityPrecondition{EntityId: proto.String(string(order.Pawn))}}
		switch order.Kind {
		case policy.OrderMove:
			wire.Order = &op.CombatOrder_Move{Move: &c.Cell{X: proto.Int32(order.Cell.X), Z: proto.Int32(order.Cell.Z)}}
		case policy.OrderAttack:
			wire.Order = &op.CombatOrder_Attack{Attack: &op.EntityPrecondition{EntityId: proto.String(string(order.Target))}}
		case policy.OrderRescue:
			wire.Order = &op.CombatOrder_Rescue{Rescue: &op.CombatRescue{Downed: &op.EntityPrecondition{EntityId: proto.String(string(order.Target))}}}
		case policy.OrderDoor:
			mode := op.CombatDoorMode_COMBAT_DOOR_MODE_FORBID
			if order.Door == policy.DoorAllow {
				mode = op.CombatDoorMode_COMBAT_DOOR_MODE_ALLOW
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
			return store.CombatStopRecord{}, memory, ErrControl
		}
		command.Orders = append(command.Orders, wire)
	}
	pre := &a.WritePrecondition{
		Identity: boundary.Identity(state.Snapshot), ExpectedGeneration: proto.Uint64(uint64(state.Snapshot.Native)),
		Attempt: &c.AttemptKey{ControllerSessionId: proto.String(string(session)), ActionId: proto.String(fmt.Sprintf("%s-stop-%d", plan, tick)), AttemptId: proto.Uint64(1)},
	}
	results, _, _, err := r.native.CombatOrders(call, pre, command)
	if err != nil {
		return store.CombatStopRecord{}, memory, err
	}
	record := store.CombatStopRecord{Tick: tick}
	for i, order := range orders {
		// An uncertain receipt carries no results: the orders stay issued
		// and the next mirror read shows what took.
		row := store.CombatOrderRecord{CombatOrder: order, Applied: results == nil}
		if results != nil {
			row.Applied, row.Refusal = results[i].Applied, results[i].Refusal
		}
		if !row.Applied && results != nil && order.Pawn != "" {
			memory = memory.Forget(order.Pawn)
		}
		record.Orders = append(record.Orders, row)
	}
	return record, memory, nil
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
	if ask == nil || len(ask.Hostiles) == 0 {
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
	}
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
	for _, row := range geometry.GetProposed() {
		if cell := row.GetCell(); cell != nil {
			reply.Proposals = append(reply.Proposals, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
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
				Job: row.GetJob(), ShieldBelt: row.GetShieldBelt(), MedicalSkill: int(row.GetMedicalSkill())}
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
