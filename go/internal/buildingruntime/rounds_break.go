package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

const subdueMethodPrefix = "combat-subdue-"

// Subdue uses its guarded melee actions, while the fight owns injury stops
// and cleanup. Cancel before stopping: uncertain dispatches still reconcile,
// and a later worker step cannot issue an attack after the interruption.
func (r *RoundsDefensePlanner) reconcileSubdueFight(call, epoch context.Context, incident store.IncidentState, state ControlState) (RoundsDefenseResult, bool, error) {
	p := r.reviewer.player
	for _, method := range incident.Methods {
		if !strings.HasPrefix(string(method.Method), subdueMethodPrefix) {
			continue
		}
		fight, found, err := p.journal.LoadCombatFight(call, method.Plan)
		if err != nil || !found || !fight.Open {
			if err != nil {
				return RoundsDefenseResult{}, true, err
			}
			continue
		}
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsDefenseResult{}, true, err
		}
		combat, err := r.native.ReadCombat(call, boundary.Identity(state.Snapshot))
		if err != nil {
			return RoundsDefenseResult{}, true, err
		}
		if _, err = boundary.Context(combat.Context, state.Snapshot); err != nil || combat.Emergency.Facts.ColonistsComplete != domain.Known(true) {
			return RoundsDefenseResult{}, true, fmt.Errorf("%w: subdue needs a complete current census", ErrControl)
		}
		participants := map[domain.PawnID]bool{}
		active := false
		for _, progress := range plan.Progress {
			if subdue, ok := progress.Action().Subdue(); ok {
				participants[subdue.Pawn()], participants[subdue.Target()] = true, true
				if progress.View().Stage != domain.Cancelled && progress.View().Stage != domain.Unsuccessful {
					for _, pawn := range combat.Emergency.Facts.Colonists {
						active = active || domain.PawnID(pawn.ID) == subdue.Target() && policy.AggressiveBreak(pawn)
					}
				}
			}
		}
		stop := policy.StopEvent{}
		// Inspect every new injury, including one followed by another event in
		// the same frame; the target is a colonist too.
		for _, event := range combat.Events {
			if event.GetAt().GetTick() > int64(fight.Memory.Tick) && participants[domain.PawnID(event.GetThingId())] && (event.GetStop() == k.CombatEvent_COMBAT_EVENT_SERIOUS_INJURY || event.GetStop() == k.CombatEvent_COMBAT_EVENT_DOWNED) {
				stop = policy.StopEvent{Kind: policy.CombatStopKind(strings.ToLower(strings.TrimPrefix(event.GetStop().String(), "COMBAT_EVENT_"))), Pawn: domain.PawnID(event.GetThingId())}
				active = false
			}
		}
		result := RoundsDefenseResult{Verdict: BuildingReasonExistingWork, Plan: method.Plan}
		if active {
			return result, true, nil
		}
		if err = p.current(call, epoch); err != nil || p.session.State() != state {
			return result, true, fmt.Errorf("%w: subdue cleanup authority changed: %v", ErrControl, err)
		}
		unresolved := false
		for _, progress := range plan.Progress {
			v := progress.View()
			unresolved = unresolved || v.Unresolved
			if v.Stage == domain.Cancelled || v.Stage == domain.Unsuccessful || v.Stage == domain.Completed && progress.Action().Kind() != domain.SubdueAction {
				continue
			}
			if _, err = p.journal.Cancel(call, method.Plan, v.Action); err != nil {
				return result, true, err
			}
		}
		var orders []policy.CombatOrder
		for _, action := range plan.Spec.Actions() {
			if draft, ok := action.OwnedDraft(); ok && fight.Roster[draft.Pawn()] {
				orders = append(orders, policy.CombatOrder{Pawn: draft.Pawn(), Kind: policy.OrderStop})
			}
		}
		batchLabel := fmt.Sprintf("%s-subdue-stop-%d", method.Plan, combat.Context.GetTick())
		batch, err := combatBatchPlan(method.Plan, batchLabel, nil, orders)
		if err != nil {
			return result, true, err
		}
		results, _, err := r.sendCombatBatch(call, state, method.Plan, batchLabel, nil, orders)
		if err != nil {
			return result, true, err
		}
		// Inspect after dispatch, including a lost reply: stopped current work
		// proves the cleanup postcondition without inventing an applied receipt.
		confirmed := results != nil
		if results == nil {
			fresh, readErr := r.native.ReadCombat(call, boundary.Identity(state.Snapshot))
			if readErr != nil {
				return result, true, readErr
			}
			if _, readErr = boundary.Context(fresh.Context, state.Snapshot); readErr != nil {
				return result, true, readErr
			}
			confirmed = true
			for _, order := range orders {
				row, ok := fresh.Detail.Get(string(order.Pawn))
				confirmed = confirmed && ok && row != nil && row.Job != nil && row.Job.PlayerForced != nil && !row.Job.GetPlayerForced() && row.Job.QueuedJobs != nil && row.Job.GetQueuedJobs() == 0 && len(row.Job.Issues) == 0
			}
			if confirmed {
				saved, loadErr := p.journal.LoadPlan(call, batch.ID())
				if loadErr != nil {
					return result, true, loadErr
				}
				v := saved.Progress[0].View()
				if v.Unresolved {
					scope := state.Snapshot
					scope.Plan, scope.Revision = batch.ID(), batch.Revision()
					_, err = p.journal.Observe(call, batch.ID(), domain.Observation{Action: v.Action, Attempt: v.Attempt, Snapshot: scope, Tick: domain.Tick(fresh.Context.GetTick()), Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, scope)
					if err != nil {
						return result, true, err
					}
					if err = p.journal.RetireCombatBatch(call, batch.ID()); err != nil {
						return result, true, err
					}
				}
			}
		}
		record := store.CombatStopRecord{Tick: domain.Tick(combat.Context.GetTick()), Stop: stop, Batch: batch.ID()}
		for i, order := range orders {
			entry := store.CombatOrderRecord{CombatOrder: order, Uncertain: results == nil}
			if results != nil {
				entry.Applied, entry.Refusal = results[i].Applied, results[i].Refusal
				confirmed = confirmed && (entry.Applied || entry.Refusal == bridge.CombatRefusalNotDrafted || entry.Refusal == bridge.CombatRefusalNotFound)
			}
			record.Orders = append(record.Orders, entry)
		}
		if len(record.Orders) > 0 {
			if err = p.journal.RecordCombatStop(call, method.Plan, record, fight.Memory); err != nil {
				return result, true, err
			}
		}
		if confirmed && !unresolved {
			if err = p.journal.CloseCombatFight(call, method.Plan); err != nil {
				return result, true, err
			}
		}
		return result, true, nil
	}
	return RoundsDefenseResult{}, false, nil
}

// breakMelee is whether the pawn's primary is a melee weapon; unarmed is
// known false, an unresolved primary unknown.
func breakMelee(row *n.PawnState, arms armament) (domain.Fact[bool], error) {
	weapon, known, err := arms.primary(row.GetEquipment())
	if err != nil || !known {
		return domain.Unknown[bool](), err
	}
	return domain.Known(weapon.Melee), nil
}
func hasAggressiveBreak(f policy.EmergencyFacts) bool {
	for _, p := range f.Colonists {
		if policy.AggressiveBreak(p) {
			return true
		}
	}
	return false
}

func (r *RoundsDefensePlanner) planBreak(call, epoch context.Context, incident store.IncidentState, state ControlState, started time.Time, arbiter *stepArbiter, tick domain.Tick, f policy.EmergencyFacts, rows map[string]*n.PawnState) (RoundsDefenseResult, error) {
	var targets []policy.EmergencyPawn
	// A cancelled response must not immediately start attacking the same pawn
	// again. Its incident keeps this history until the aggressive break clears.
	interrupted := map[policy.PawnID]bool{}
	for _, method := range incident.Methods {
		if !strings.HasPrefix(string(method.Method), subdueMethodPrefix) {
			continue
		}
		plan, err := r.reviewer.player.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsDefenseResult{}, err
		}
		for _, progress := range plan.Progress {
			if subdue, ok := progress.Action().Subdue(); ok && (progress.View().Stage == domain.Cancelled || progress.View().Stage == domain.Unsuccessful) {
				interrupted[policy.PawnID(subdue.Target())] = true
			}
		}
	}
	for _, p := range f.Colonists {
		if policy.AggressiveBreak(p) && !interrupted[p.ID] {
			targets = append(targets, p)
		}
	}
	if len(targets) == 0 {
		return RoundsDefenseResult{}, nil
	}
	sort.Slice(targets, func(i, j int) bool { return targets[i].ID < targets[j].ID })
	needed, err := plannedDrafts(call, r.reviewer.player.journal)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	var responders []policy.BreakResponder
	arms, err := readArmament(call, r.native, boundary.Identity(state.Snapshot))
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	for _, p := range f.Colonists {
		row := rows[string(p.ID)]
		ranged, err := rangedWeaponEquipped(row.Equipment, arms)
		if err != nil {
			return RoundsDefenseResult{}, err
		}
		d := squadDefenderFacts(row, needed, ranged)
		if d.MeleeEquipped, err = breakMelee(row, arms); err != nil {
			return RoundsDefenseResult{}, err
		}
		responders = append(responders, policy.BreakResponder{SquadDefenderFacts: d, Cell: breakCell(row)})
	}
	var actions []domain.Action
	h := sha256.New()
	var chosen []domain.PawnID
	var victim policy.PawnID
	for _, target := range targets {
		chosen = policy.SelectBreakSquad(target, breakCell(rows[string(target.ID)]), responders)
		if len(chosen) > 0 {
			victim = target.ID
			break
		}
	}
	if len(chosen) == 0 {
		return RoundsDefenseResult{Verdict: BuildingReasonNoSquad}, nil
	}
	if !arbiter.tryClaim(append(append([]domain.PawnID{}, chosen...), domain.PawnID(victim))) {
		return RoundsDefenseResult{Verdict: waitFor(policy.CauseMethodUsed, "break_pawn_claim")}, nil
	}
	fmt.Fprintf(h, "%s/%v", victim, chosen)
	method, id := defenseMethodID("combat-subdue", len(incident.Methods), h), domain.MintPlanID()
	for i, pawn := range chosen {
		draftID := domain.ActionID(fmt.Sprintf("%s-d%d", id, i))
		d, err := domain.NewOwnedDraft(pawn)
		if err != nil {
			return RoundsDefenseResult{}, err
		}
		da, err := domain.NewOwnedDraftAction(draftID, d)
		if err != nil {
			return RoundsDefenseResult{}, err
		}
		m, err := domain.NewSubdue(pawn, domain.PawnID(victim), draftID)
		if err != nil {
			return RoundsDefenseResult{}, err
		}
		ma, err := domain.NewSubdueAction(domain.ActionID(fmt.Sprintf("%s-s%d", id, i)), m)
		if err != nil {
			return RoundsDefenseResult{}, err
		}
		actions = append(actions, da, ma)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	p := r.reviewer.player
	if err = p.current(call, epoch); err != nil {
		return RoundsDefenseResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsDefenseResult{}, fmt.Errorf("%w: planBreak: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitCombatFight(call, incident.Incident.ID, method, plan, policy.CombatMemory{Tick: tick}, playerWorld(state.Snapshot), chosen); err != nil {
		return RoundsDefenseResult{}, err
	}
	return RoundsDefenseResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
