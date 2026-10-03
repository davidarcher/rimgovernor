package buildingruntime

import (
	"context"
	"fmt"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// stripMethodPrefix names a fight's strip of one downed raider (#1079):
// an ActiveCombat method beside the open fight, its plan one strip action.
const stripMethodPrefix = "strip-"

// stripJob is vanilla's strip job def, the stripper's job while it works.
const stripJob = "Strip"

// postFight keeps the fight open after the raid until its downed raiders
// are handled (#1079): every one is stripped (a strip method at a time),
// then one not CaptureWorthy is finished by its stripper, drafted and
// given the attack order; a worthy one is left for the custody capture.
// held says the fight stays open; the result is this stop's.
func (r *RoutineDefensePlanner) postFight(call, epoch context.Context, incident store.IncidentState, state ControlState, arbiter *stepArbiter) (RoutineDefenseResult, bool, error) {
	p := r.reviewer.player
	var fight store.CombatFight
	var fightPlan domain.PlanID
	strips := map[domain.PawnID]domain.PlanID{}
	for _, method := range incident.Methods {
		if raider, ok := strings.CutPrefix(string(method.Method), stripMethodPrefix); ok {
			strips[domain.PawnID(raider)] = method.Plan
			continue
		}
		if !strings.HasPrefix(string(method.Method), combatMethodPrefix) {
			continue
		}
		record, ok, err := p.journal.LoadCombatFight(call, method.Plan)
		if err != nil {
			return RoutineDefenseResult{}, false, err
		}
		if ok && record.Open {
			fight, fightPlan = record, method.Plan
		}
	}
	if fightPlan == "" {
		return RoutineDefenseResult{}, false, nil
	}
	combat, err := r.native.ReadCombat(call, boundary.Identity(state.Snapshot))
	if err != nil {
		return RoutineDefenseResult{}, false, err
	}
	if _, err = boundary.Context(combat.Context, state.Snapshot); err != nil {
		return RoutineDefenseResult{}, false, fmt.Errorf("%w: postFight: combat frame outside the world", ErrControl)
	}
	for _, raider := range downedRaiders(combat) {
		id := domain.PawnID(raider.GetId())
		strip := policy.StripNone
		if plan, ok := strips[id]; ok {
			if strip, err = stripState(call, p.journal, plan); err != nil {
				return RoutineDefenseResult{}, false, err
			}
		}
		switch policy.PostFightNext(apparelWorn(combat.Detail.At(string(id))), strip) {
		case policy.PostFightWait:
			return RoutineDefenseResult{Reason: BuildingMethodExistingWork, Plan: fightPlan}, true, nil
		case policy.PostFightStrip:
			result, err := r.commitStrip(call, epoch, incident, id)
			return result, true, err
		}
		if policy.CaptureWorthy(policy.CombatPawnState{Luciferium: raider.GetLuciferiumAddicted()}) {
			continue // stripped; the custody capture takes it
		}
		result, err := r.finishRaider(call, epoch, state, fightPlan, fight.Memory, combat, raider, arbiter)
		return result, true, err
	}
	return RoutineDefenseResult{}, false, nil
}

// downedRaiders are the frame's downed live hostile humanlikes, by id.
func downedRaiders(combat bridge.Combat) []*mp.CombatPawn {
	var out []*mp.CombatPawn
	for _, row := range combat.Pawns {
		if row.GetSide() != mp.CombatSide_COMBAT_SIDE_HOSTILE || !row.GetDowned() || row.GetDead() {
			continue
		}
		if detail, _ := combat.Detail.Get(row.GetId()); detail != nil && detail.Humanlike != nil && !detail.GetHumanlike() {
			continue // an animal has nothing to strip and is never captured here
		}
		out = append(out, row)
	}
	slices.SortFunc(out, func(a, b *mp.CombatPawn) int { return strings.Compare(a.GetId(), b.GetId()) })
	return out
}

// apparelWorn is whether the frame's detail row shows worn apparel;
// unknown without the row's equipment.
func apparelWorn(row *n.PawnState) domain.Fact[bool] {
	if row == nil || row.Equipment == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(row.Equipment.Apparel) > 0)
}

// stripState reads a strip method's plan: open, placed (its action
// completed) or refused.
func stripState(call context.Context, journal *store.Store, id domain.PlanID) (policy.StripState, error) {
	plan, err := journal.LoadPlan(call, id)
	if err != nil {
		return policy.StripNone, err
	}
	if store.PlanOpen(plan) {
		return policy.StripOpen, nil
	}
	for _, progress := range plan.Progress {
		if progress.View().Stage == domain.Completed {
			return policy.StripPlaced, nil
		}
	}
	return policy.StripRefused, nil
}

// commitStrip admits the strip of raider beside the open fight.
func (r *RoutineDefensePlanner) commitStrip(call, epoch context.Context, incident store.IncidentState, raider domain.PawnID) (RoutineDefenseResult, error) {
	p := r.reviewer.player
	id := domain.MintPlanID()
	value, err := domain.NewStrip(string(raider))
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	action, err := domain.NewStripAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineDefenseResult{}, err
	}
	if _, err = p.journal.CommitFightStrip(call, incident.Incident.ID, domain.MethodID(stripMethodPrefix+string(raider)), plan); err != nil {
		return RoutineDefenseResult{}, err
	}
	return RoutineDefenseResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// stripper is the colonist to finish raider: one whose job targets it
// (the stripper at work, or already attacking), else the nearest standing
// colonist, which a strip just done leaves beside it; ties by id.
func stripper(combat bridge.Combat, raider *mp.CombatPawn) *mp.CombatPawn {
	var best *mp.CombatPawn
	bestDist := int64(-1)
	at := raider.GetCell()
	for _, row := range combat.Pawns {
		if row.GetSide() != mp.CombatSide_COMBAT_SIDE_COLONIST || row.GetDowned() || row.GetDead() {
			continue
		}
		dist := int64(1) << 40
		if row.GetTargetId() == raider.GetId() {
			dist = -1
		} else if c := row.GetCell(); c != nil && at != nil {
			dx, dz := int64(c.GetX()-at.GetX()), int64(c.GetZ()-at.GetZ())
			dist = dx*dx + dz*dz
		}
		if best == nil || dist < bestDist || dist == bestDist && row.GetId() < best.GetId() {
			best, bestDist = row, dist
		}
	}
	return best
}

// finishRaider has raider's stripper finish it: drafted when it is not,
// then the attack order. A stripper already attacking it is left to it.
func (r *RoutineDefensePlanner) finishRaider(call, epoch context.Context, state ControlState, fightPlan domain.PlanID, memory policy.CombatMemory, combat bridge.Combat, raider *mp.CombatPawn, arbiter *stepArbiter) (RoutineDefenseResult, error) {
	p := r.reviewer.player
	waiting := RoutineDefenseResult{Reason: BuildingMethodExistingWork, Plan: fightPlan}
	pawn := stripper(combat, raider)
	if pawn == nil || pawn.GetTargetId() == raider.GetId() && pawn.GetJob() != stripJob && pawn.GetDrafted() {
		return waiting, nil
	}
	id := domain.PawnID(pawn.GetId())
	if !arbiter.tryClaim([]domain.PawnID{id}) {
		return waiting, nil
	}
	var drafts []domain.PawnID
	if !pawn.GetDrafted() {
		drafts = []domain.PawnID{id}
	}
	orders := []policy.CombatOrder{{Pawn: id, Kind: policy.OrderAttack, Target: domain.PawnID(raider.GetId()), Reason: policy.ReasonFinish}}
	if err := p.current(call, epoch); err != nil {
		return RoutineDefenseResult{}, err
	}
	tick := domain.Tick(combat.Context.GetTick())
	results, orders, err := r.sendCombatBatch(call, state, fmt.Sprintf("%s-finish-%s-%d", fightPlan, raider.GetId(), tick), drafts, orders)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	if results, memory, err = r.recordDrafts(call, fightPlan, drafts, results, memory); err != nil {
		return RoutineDefenseResult{}, err
	}
	record, memory := combatStopRecord(policy.CombatView{Tick: tick}, orders, results, memory)
	if len(record.Orders) > 0 {
		if err = p.journal.RecordCombatStop(call, fightPlan, record, memory); err != nil {
			return RoutineDefenseResult{}, err
		}
	}
	return RoutineDefenseResult{Reason: BuildingMethodCombatOrders, Plan: fightPlan}, nil
}
