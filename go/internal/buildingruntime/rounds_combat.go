package buildingruntime

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"log/slog"
	"os"
	"slices"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

	"google.golang.org/protobuf/proto"
)

// combatMethodPrefix names the ActiveCombat method that owns a fight:
// one plan with no actions, its fight row holding the defenders'
// roster. Its orders are DecideCombat's, sent through
// combat_orders at each stop and recorded as the plan's evidence
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

// admitFight commits the fight: its empty plan and open fight row
// rostering every defender the formation gave a role, then one
// combat_orders batch on this stop that drafts them all and gives the
// formation's orders. Once the fight closes the undraft sweep undrafts
// its roster.
func (r *RoundsDefensePlanner) admitFight(call, epoch context.Context, incident store.IncidentState, state ControlState, started time.Time, arbiter *stepArbiter, a fightAdmission) (RoundsDefenseResult, error) {
	p := r.reviewer.player
	memory := a.memory
	if len(memory.Roles) == 0 {
		return RoundsDefenseResult{Verdict: BuildingReasonNoSquad}, nil
	}
	pawns := make([]domain.PawnID, 0, len(memory.Roles))
	hash := sha256.New()
	fmt.Fprintf(hash, "%s\n", memory.Tactic)
	for _, role := range memory.Roles {
		pawns = append(pawns, role.Pawn)
		fmt.Fprintf(hash, "%s/%s\n", role.Pawn, role.Target)
	}
	if !arbiter.tryClaim(pawns) {
		return RoundsDefenseResult{Verdict: waitFor(policy.CauseMethodUsed, "combat_pawn_claim")}, nil
	}
	// The threat loadout is the fight plan's own equip and wear
	// actions, committed before the first combat.orders batch; its pawns
	// draft once their action settles.
	loadout := r.fightLoadout(call, state, combatView(a.combat, a.in, pawns, a.held), a.in.rows, pawns, a.combat.Catalog)
	for _, order := range loadout {
		fmt.Fprintf(hash, "loadout %s/%s/%t\n", order.Pawn, order.Thing, order.Wear)
	}
	method, id := defenseMethodID(strings.TrimSuffix(combatMethodPrefix, "-"), len(incident.Methods), hash), domain.MintPlanID()
	actions, held, err := loadoutActions(id, loadout)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	drafts := make([]domain.PawnID, 0, len(pawns))
	for _, pawn := range pawns {
		if !held[pawn] {
			drafts = append(drafts, pawn)
		}
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsDefenseResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsDefenseResult{}, fmt.Errorf("%w: admitFight: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	world := store.World{Colony: state.Snapshot.Colony, Load: state.Snapshot.Load, Map: state.Snapshot.Map}
	if _, err = p.journal.CommitCombatFight(call, incident.Incident.ID, method, plan, memory, world, drafts); err != nil {
		return RoundsDefenseResult{}, err
	}
	admitted := RoundsDefenseResult{Verdict: BuildingReasonAdmitted, Plan: id}
	// The formation's orders as the defenders will be once drafted: the
	// same decision with them orderable. An ask (a rescue path) waits for
	// the next stop; the batch then only drafts.
	view := combatView(a.combat, a.in, drafts, a.held)
	if view.Drugs, err = r.reviewer.combatDrugs(call, state.Snapshot); err != nil {
		return RoundsDefenseResult{}, err
	}
	orders, ask, next := policy.DecideCombat(view, a.reply, policy.StopEvent{}, memory)
	if ask != nil {
		orders, next = nil, memory
	}
	// The admission stop makes no permit call: its first stop does.
	orders, _ = splitPermitCalls(orders)
	next = withoutPermitMarks(next, memory)
	if len(drafts)+len(orders) == 0 {
		return admitted, p.journal.SaveCombatMemory(call, id, next)
	}
	results, orders, err := r.sendCombatBatch(call, state, id, fmt.Sprintf("%s-admit", id), drafts, orders)
	if err != nil {
		// Nothing is known of the batch: the next stop's rows show who
		// was drafted.
		telemetry.Decide(call, telemetry.Decision{Kind: "admission", Component: "routine-defense", Level: slog.LevelWarn, Verdict: "refused", Reason: "batch_failed", Target: string(id), Attrs: map[string]any{"combat_plan": string(id), "error": err}})
		return admitted, nil
	}
	results, next, err = r.recordDrafts(call, id, drafts, results, next)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	record, next := combatStopRecord(view, orders, results, next)
	if len(record.Orders) == 0 {
		return admitted, p.journal.SaveCombatMemory(call, id, next)
	}
	batchPlan, _ := combatBatchPlan(id, fmt.Sprintf("%s-admit", id), drafts, orders)
	record.Batch = batchPlan.ID()
	record.Stop = a.stop
	return admitted, p.journal.RecordCombatStop(call, id, record, next)
}

// clearFightDoors restores every original temporary door and animal setting.
func (r *RoundsDefensePlanner) clearFightDoors(call context.Context, state ControlState, plan domain.PlanID) error {
	kept, ok, err := r.reviewer.player.journal.LoadCombatRestoration(call)
	if err != nil || !ok {
		return err
	}
	if kept.Owner != plan {
		return ErrControl
	}
	return r.restoreCombatSettings(call, state, kept)
}

// recordDrafts records a batch's leading draft results on the fight's
// roster: an applied draft joins it, a refused one leaves it with its
// orders forgotten. It returns the remaining (order) results; an uncertain
// receipt (nil results) leaves the roster alone for the next stop's rows.
func (r *RoundsDefensePlanner) recordDrafts(call context.Context, plan domain.PlanID, pawns []domain.PawnID, results []bridge.CombatOrderResult, memory policy.CombatMemory) ([]bridge.CombatOrderResult, policy.CombatMemory, error) {
	if results == nil || len(pawns) == 0 {
		return results, memory, nil
	}
	var add, drop []domain.PawnID
	for i, pawn := range pawns {
		if results[i].Applied {
			add = append(add, pawn)
		} else {
			drop = append(drop, pawn)
			memory = memory.Forget(pawn)
		}
	}
	return results[len(pawns):], memory, r.reviewer.player.journal.UpdateCombatRoster(call, plan, add, drop)
}

// undraftedRoles are the live role pawns not among the fight's drafted
// defenders (orderable), sorted: a later formation's evacuee or responder,
// or a defender undrafted since.
func undraftedRoles(m policy.CombatMemory, orderable []domain.PawnID, view policy.CombatView) []domain.PawnID {
	down := map[domain.PawnID]bool{}
	for _, p := range view.Pawns {
		down[p.ID] = p.Dead || p.Downed
	}
	var out []domain.PawnID
	for _, role := range m.Roles {
		if !slices.Contains(orderable, role.Pawn) && !down[role.Pawn] && !slices.Contains(out, role.Pawn) {
			out = append(out, role.Pawn)
		}
	}
	slices.Sort(out)
	return out
}

// issueCombatOrders sends one stop's batch, drafting drafts and
// giving orders, and returns the stop's evidence and the memory without
// the refused orders, which the next stop gives again.
func (r *RoundsDefensePlanner) issueCombatOrders(call context.Context, state ControlState, plan domain.PlanID, view policy.CombatView, drafts []domain.PawnID, orders []policy.CombatOrder, memory policy.CombatMemory) (store.CombatStopRecord, policy.CombatMemory, error) {
	results, orders, err := r.sendCombatBatch(call, state, plan, fmt.Sprintf("%s-stop-%d", plan, view.Tick), drafts, orders)
	if err != nil {
		return store.CombatStopRecord{}, memory, err
	}
	if results, memory, err = r.recordDrafts(call, plan, drafts, results, memory); err != nil {
		return store.CombatStopRecord{}, memory, err
	}
	record, memory := combatStopRecord(view, orders, results, memory)
	batchPlan, _ := combatBatchPlan(plan, fmt.Sprintf("%s-stop-%d", plan, view.Tick), drafts, orders)
	record.Batch = batchPlan.ID()
	return record, memory, nil
}

// combatStopRecord is a stop's evidence from its orders' results and the
// memory without the refused ones; an attack refused cannot_hit is
// remembered from the shooter's cell. An uncertain receipt carries
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
		row := store.CombatOrderRecord{CombatOrder: order, Uncertain: results == nil}
		if results != nil {
			row.Applied, row.Refusal = results[i].Applied, results[i].Refusal
		}
		if !row.Applied && results != nil && order.Kind == policy.OrderDoor && row.Refusal == bridge.CombatRefusalNotADoor {
			memory = memory.RefuseDoor(order.Cell)
		} else if !row.Applied && results != nil && order.Kind == policy.OrderMortarFire {
			memory = memory.RefuseFire(order, row.Refusal == bridge.CombatRefusalNoShell || row.Refusal == bridge.CombatRefusalUnknownShell)
		} else if !row.Applied && results != nil && order.Pawn != "" {
			if policy.AnimalOrderKind(order.Kind) {
				memory = memory.RefuseAnimal(order, row.Refusal)
			} else if from, ok := cells[order.Pawn]; ok && row.Refusal == bridge.CombatRefusalCannotHit {
				memory = memory.RefuseHit(order, from)
			} else if row.Refusal == bridge.CombatRefusalNoShell || row.Refusal == bridge.CombatRefusalUnknownShell {
				memory = memory.RefuseShell(order)
			} else if order.Kind == policy.OrderMove && row.Refusal == bridge.CombatRefusalUnreachable {
				memory = memory.RefuseCell(order.Cell).Forget(order.Pawn)
			} else {
				memory = memory.Forget(order.Pawn)
			}
		}
		record.Orders = append(record.Orders, row)
	}
	return record, memory
}

// sendCombatBatch sends one combat.orders batch under action: a draft
// order for each of drafts, then orders.
// It returns every order's result in that order (nil for an uncertain
// receipt) and the orders sent.
func combatBatchCommands(drafts []domain.PawnID, orders []policy.CombatOrder) []domain.CombatCommand {
	var commands []domain.CombatCommand
	for _, p := range drafts {
		commands = append(commands, domain.CombatCommand{Kind: "draft", Pawn: p})
	}
	for _, o := range orders {
		commands = append(commands, domain.CombatCommand{Kind: string(o.Kind), Pawn: o.Pawn, Target: o.Target, Cell: o.Cell, Aim: o.Aim, Door: string(o.Door), FireMode: o.FireMode, Clear: o.Clear, Shell: o.Shell, Drug: o.Drug})
	}
	return commands
}
func combatBatchPlan(fight domain.PlanID, action string, drafts []domain.PawnID, orders []policy.CombatOrder) (domain.PlanSpec, error) {
	commands := combatBatchCommands(drafts, orders)
	raw, err := json.Marshal(commands)
	if err != nil {
		return domain.PlanSpec{}, err
	}
	sum := sha256.Sum256(append([]byte(action+"\n"), raw...))
	key := fmt.Sprintf("combat-%x", sum[:])
	batch, err := domain.NewCombatBatch(fight, key, commands)
	if err != nil {
		return domain.PlanSpec{}, err
	}
	a, err := domain.NewCombatBatchAction(domain.ActionID(key), batch)
	if err != nil {
		return domain.PlanSpec{}, err
	}
	return domain.NewPlan(domain.PlanID(key), 1, []domain.Action{a})
}

// recordCombatDispatch uses the same dispatch evidence kind as Worker for
// synchronous Hands calls. An uncertain receipt still proves a call happened;
// neither that call nor an accepted receipt proves native pawn progress.
func recordCombatDispatch(ctx context.Context, items []executor.BatchItem) {
	for _, item := range items {
		if !item.Result.NativeCalled {
			continue
		}
		v := item.Result.Progress.View()
		telemetry.Decide(ctx, telemetry.Decision{Kind: "dispatch", Component: "routine-defense", Target: string(item.Action), Verdict: "dispatched", Reason: "native_called", Attrs: map[string]any{"dispatch_tick": int64(v.Tick), "attempt": int64(v.Attempt), "stage_after": string(v.Stage)}})
	}
}

func (r *RoundsDefensePlanner) sendCombatBatch(call context.Context, state ControlState, fight domain.PlanID, action string, drafts []domain.PawnID, orders []policy.CombatOrder) ([]bridge.CombatOrderResult, []policy.CombatOrder, error) {
	orders, err := r.captureCombatSettings(call, state, fight, orders)
	if err != nil {
		return nil, nil, err
	}
	if len(drafts)+len(orders) == 0 {
		return nil, nil, nil
	}
	plan, err := combatBatchPlan(fight, action, drafts, orders)
	if err != nil {
		return nil, nil, err
	}
	p := r.reviewer.player
	saved, err := p.journal.CommitCombatBatch(call, plan)
	if err != nil {
		return nil, nil, err
	}
	progress := saved.Progress[0]
	if !saved.Retired && !progress.View().Unresolved && (progress.View().Stage == domain.Pending || progress.View().Stage == domain.Prepared) {

		items, runErr := r.hands.RunBatch(call, plan.ID(), []domain.ActionID{plan.Actions()[0].ID()})
		recordCombatDispatch(call, items)
		if runErr != nil {
			return nil, nil, runErr
		}
		if len(items) != 1 {
			return nil, nil, ErrControl
		}
		if items[0].Err != nil && !items[0].Result.Progress.View().Unresolved {
			return nil, nil, items[0].Err
		}
		progress = items[0].Result.Progress
	}
	if err = p.journal.RetireCombatBatch(call, plan.ID()); err != nil {
		return nil, nil, err
	}
	if receipt, known := progress.View().Receipt.Value(); known && (receipt == domain.ReceiptRefused || receipt == domain.ReceiptUnsent) {
		return nil, nil, fmt.Errorf("%w: combat batch %s", ErrControl, receipt)
	}
	results, known := progress.View().Combat.Value()
	if !known {
		return nil, orders, nil
	}
	var out []bridge.CombatOrderResult
	for _, v := range results.Orders() {
		out = append(out, bridge.CombatOrderResult{Index: v.Index, PawnID: v.PawnID, Applied: v.Applied, Refusal: v.Refusal, JobDef: v.JobDef})
	}
	return out, orders, nil
}

// answerGeometry answers DecideCombat's geometry ask with one
// combat.geometry read: the ask's named cells scored and cells
// proposed for its role. A failed or refused read is an answer with
// no proposals and no lines: Formation keeps the layout's firing line and
// attacks go out unchecked.
func (r *RoundsDefensePlanner) answerGeometry(ctx context.Context, identity *c.Identity, ask *policy.GeometryRequest) policy.GeometryReply {
	reply := policy.GeometryReply{Answered: true}
	if ask != nil && ask.Propose == policy.RoleRescuePath {
		return r.answerRescuePath(ctx, identity, ask)
	}
	// Named cells alone are asked with no hostile: their standability
	// (before drop pods open).
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
		// A walled-in gunner's firing cells.
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
	// ranks line and proposals alike; so are the shooters' cells, so
	// their attacks carry lines of fire.
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
		defenseAction(ctx, "routine-defense", slog.LevelWarn, "failed", "geometry_read", "", map[string]any{"error": err})
		return reply
	}
	// Every proposal is standable by contract; a named cell is when the
	// game says so.
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

// answerRescuePath answers a rescue_path ask with the rescuer's
// route, each cell flagged for hostile line of fire and a door. A failed
// read is an answer with no route: the rescue waits.
func (r *RoundsDefensePlanner) answerRescuePath(ctx context.Context, identity *c.Identity, ask *policy.GeometryRequest) policy.GeometryReply {
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
		defenseAction(ctx, "routine-defense", slog.LevelWarn, "failed", "rescue_path_read", string(ask.Pawn), map[string]any{"error": err})
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
// when the frame carries them, else the detail rows
// (position, downed, dead; no stance or target).
func combatPawnStates(combat bridge.Combat, rows map[string]*n.PawnState, weapons map[string]policy.WeaponDef) []policy.CombatPawnState {
	var out []policy.CombatPawnState
	if len(combat.Pawns) > 0 {
		for _, row := range combat.Pawns {
			s := policy.CombatPawnState{ID: domain.PawnID(row.GetId()), Downed: row.GetDowned(), Dead: row.GetDead(), Target: domain.PawnID(row.GetTargetId()), Stance: combatStance(row.GetStance()),
				Weapon: row.GetWeapon(), WeaponFacts: weapons[row.GetWeapon()], WeaponRange: weapons[row.GetWeapon()].Reach, FireMode: bridge.FireModeName(row.GetFireMode()),
				Job: row.GetJob(), TargetMortar: row.GetTargetMortar(), ShieldBelt: row.GetShieldBelt(), MedicalSkill: int(row.GetMedicalSkill()), MoveSpeed: row.GetMoveSpeed(), StunTicks: int(row.GetStunTicksLeft()),
				GoJuice: row.GetGoJuiceHigh(), Luciferium: row.GetLuciferiumAddicted(),
				Animal: row.GetSide() == mp.CombatSide_COMBAT_SIDE_COLONY_ANIMAL}
			if drugs := row.GetCarriedDrugs(); drugs != nil {
				s.CarriedDrugs = domain.Known(slices.Clone(drugs.GetDefs()))
			}
			if cell := row.GetCell(); cell != nil && cell.X != nil && cell.Z != nil {
				s.Cell = domain.Known(domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
			}
			if row.ShieldEnergy != nil {
				s.Shield = domain.Known(row.GetShieldEnergy())
			}
			s.Prisoner = row.GetSide() == mp.CombatSide_COMBAT_SIDE_PRISONER
			s.Wild = row.GetSide() == mp.CombatSide_COMBAT_SIDE_WILD_ANIMAL
			if row.Health != nil {
				s.Health = domain.Known(row.GetHealth())
			}
			out = append(out, threatFacts(s, rows[row.GetId()], combat.Catalog))
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
		out = append(out, threatFacts(s, row, combat.Catalog))
	}
	return out
}

// recordCombatStop appends a stop that wrote to the fight's journal, and
// the frame it decided from, to the serve's snapshot stream when
// recording is on. A failed write is logged.
func recordCombatStop(ctx context.Context, combat bridge.Combat, s snap.CombatStop) {
	dir := os.Getenv(snap.DirEnv)
	if dir == "" {
		return
	}
	if err := snap.RecordCombatStop(dir, combat.Frame, s); err != nil {
		defenseSnapshotSkip(ctx, "defense", "combat", err)
	}
}

// threatFacts adds the census row's threat facts: the pawn kind,
// and a sapper or breacher by its lord toil or a mining job.
func threatFacts(s policy.CombatPawnState, row *n.PawnState, catalog *bridge.DefinitionCatalog) policy.CombatPawnState {
	if row == nil {
		return s
	}
	s.Kind = row.GetKindDefName()
	// The race flags are the game's own, off the catalog's race rows.
	s.Mech, s.Insect = catalog.RaceFlags(row.GetPawn().GetDefName())
	if h := row.GetHealth(); h != nil {
		if h.BloodLoss != nil {
			s.BloodLoss = domain.Known(h.GetBloodLoss())
		}
		if h.BleedRatePerDay != nil {
			s.BleedRatePerDay = domain.Known(h.GetBleedRatePerDay())
		}
		if h.HoursUntilDeathFromBloodLoss != nil {
			s.HoursUntilBleedDeath = domain.Known(h.GetHoursUntilDeathFromBloodLoss())
		}
	}
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
// of a combat stop kind after the fight's last decision, or none (the
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
