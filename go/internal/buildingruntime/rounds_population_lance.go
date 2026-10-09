package buildingruntime

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// lanceUsers are the colonists in rows able to use a worn lance now:
// alive, standing, out of a mental state, capable of violence, wearing a
// policy.LanceDefs item. Native checks charges and the target at apply.
func lanceUsers(rows []*n.PawnState, things bridge.Things) []policy.LanceUser {
	var out []policy.LanceUser
	for _, row := range rows {
		facts := squadDefenderFacts(row, nil, domain.Unknown[bool]())
		dead, dk := facts.Dead.Value()
		downed, wk := facts.Downed.Value()
		mental, mk := facts.MentalState.Value()
		violent, vk := facts.ViolenceCapable.Value()
		if !dk || !wk || !mk || !vk || dead || downed || mental || !violent {
			continue
		}
		for _, item := range row.GetEquipment().GetApparel() {
			if policy.LanceDefs[policy.Resource(gearDef(things, item.GetThing()))] && item.GetThing().GetId() != "" {
				out = append(out, policy.LanceUser{Pawn: domain.PawnID(row.Pawn.GetId()), Item: item.GetThing().GetId()})
				break
			}
		}
	}
	return out
}

// stepLance proposes one UseItem of a worn psychic shock lance on target,
// read from the same emergency colonist pool and combat pawn read
// the capture path uses. ok is false when no colonist can use one now, so
// the step goes on to capture and rescue.
func (r *RoundsPopulationCustodyPlanner) stepLance(call, epoch context.Context, p *Player, state ControlState, started time.Time, goal store.StandardState, reviewTick domain.Tick, target domain.PawnID, arbiter *stepArbiter) (RoundsPopulationCustodyResult, bool, error) {
	identityCtx := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityCtx)
	if err != nil {
		return RoundsPopulationCustodyResult{}, false, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(reviewTick) {
		return RoundsPopulationCustodyResult{}, false, fmt.Errorf("%w: stepLance: emergency context", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoundsPopulationCustodyResult{}, false, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		if domain.PawnID(pawn.ID) != target {
			ids = append(ids, string(pawn.ID))
		}
	}
	if len(ids) == 0 {
		return RoundsPopulationCustodyResult{}, false, nil
	}
	reply, _, err := r.native.ReadCombatPawns(call, identityCtx, ids)
	if err != nil {
		return RoundsPopulationCustodyResult{}, false, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsPopulationCustodyResult{}, false, fmt.Errorf("%w: stepLance: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoundsPopulationCustodyResult{}, false, fmt.Errorf("%w: stepLance: combat context", ErrControl)
	}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil {
			return RoundsPopulationCustodyResult{}, false, fmt.Errorf("%w: stepLance: row == nil || row.Pawn == nil", ErrControl)
		}
	}
	things, err := frameThings(call, r.native, identityCtx)
	if err != nil {
		return RoundsPopulationCustodyResult{}, false, err
	}
	choice, ok := policy.SelectLanceUse(target, lanceUsers(observed.Pawns, things))
	if !ok || !arbiter.tryClaim([]domain.PawnID{choice.User, choice.Target}) {
		return RoundsPopulationCustodyResult{}, false, nil
	}
	prefix := fmt.Sprintf("population-lance-%s-", choice.Target)
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoundsPopulationCustodyResult{}, false, nil
	}
	use, err := domain.NewUseItem(choice.User, choice.Item, choice.Target)
	if err != nil {
		return RoundsPopulationCustodyResult{}, false, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewUseItemAction(domain.ActionID(fmt.Sprintf("%s-0", id)), use)
	if err != nil {
		return RoundsPopulationCustodyResult{}, false, err
	}
	result, err := r.commit(call, epoch, p, state, started, goal, domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt)), id, action)
	return result, err == nil, err
}
