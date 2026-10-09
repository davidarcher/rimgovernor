package buildingruntime

import (
	"context"
	"fmt"
	"log/slog"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// stepMonolith is the custody step's monolith advance, reached when
// no custody or containment upkeep stands: while the monolith is in play,
// policy.MonolithAdvanceOwed decides, and an owed order sends one colonist at
// the monolith with the investigate or activate job (a recovery-service
// give-job), or at the awakening quest's next void structure, the Gleaming
// monolith or the void node with the interact job. The investigate dialog and the awakening confirmation the job
// opens are answered by the dialog planner. A hostile census read here
// completes the awakening gate; unread facts are logged and hold the order.
// ok is false when no order was committed and the step goes on.
func (r *RoundsPopulationCustodyPlanner) stepMonolith(call, epoch context.Context, p *Player, state ControlState, started time.Time, goal store.StandardState, facts policy.RoundsFacts, arbiter *stepArbiter) (result RoundsPopulationCustodyResult, ok bool, err error) {
	if !policy.MonolithInPlay(facts.Monolith) {
		return result, false, nil
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return result, false, err
	}
	identity := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identity)
	if err != nil {
		return result, false, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return result, false, fmt.Errorf("%w: stepMonolith: emergency read outside the review", ErrControl)
	}
	snapshot, err := policy.NewEmergencySnapshot(state.Snapshot, review.Tick, emergency.Facts)
	if err != nil {
		return result, false, err
	}
	facts.Hostiles, _ = policy.EmergencyNeeds(snapshot, state.Snapshot, review.Tick)
	advance := policy.MonolithAdvanceOwed(facts.Monolith, policy.MonolithGate(facts, r.reviewer.staged().ColonyStage))
	for _, issue := range advance.Issues {
		defenseAction(call, "routine-population-custody", slog.LevelWarn, "refused", "monolith_advance_unread", "monolith", map[string]any{"detail": issue})
	}
	if advance.Order == "" {
		return result, false, nil
	}
	var able []domain.PawnID
	target := advance.Monolith
	method, job := domain.RecoveryServiceActivateMonolith, fmt.Sprintf("activate-%d", advance.Level)
	switch advance.Order {
	case policy.MonolithInvestigate:
		method, job = domain.RecoveryServiceInvestigateMonolith, fmt.Sprintf("investigate-%d", advance.Level)
	case policy.MonolithInteract:
		target = advance.Target
		method, job = domain.RecoveryServiceInteract, "interact-"+target
	}
	if len(advance.Performers) > 0 {
		// The void node's performers were read on the node's map (the pocket
		// map the skipped colonist stands on), not the colony map's roster.
		for _, id := range advance.Performers {
			able = append(able, domain.PawnID(id))
		}
	} else {
		complete, known := emergency.Facts.ColonistsComplete.Value()
		if !known || !complete {
			return RoundsPopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "colonists_complete")}, true, nil
		}
		for _, pawn := range emergency.Facts.Colonists {
			dead, dk := pawn.Dead.Value()
			downed, wk := pawn.Downed.Value()
			if _, inMental := pawn.MentalState.Value(); dk && !dead && wk && !downed && !inMental {
				able = append(able, domain.PawnID(pawn.ID))
			}
		}
		sort.Slice(able, func(i, j int) bool { return able[i] < able[j] })
	}
	// An attempt native refused (the colonist cannot reach the target) is
	// retried with the next colonist.
	prefix := fmt.Sprintf("population-monolith-%s-", job)
	attemptMethod, verdict, admitted, err := admitStandardMethod(call, p.journal, goal, prefix, state.Snapshot)
	if err != nil {
		return result, false, err
	}
	if !admitted {
		return RoundsPopulationCustodyResult{Verdict: verdict}, true, nil
	}
	if len(able) == 0 {
		return RoundsPopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "monolith_performer")}, true, nil
	}
	pawn := able[len(standardMethodPlans(goal.History, goal.Standard.Episode, prefix))%len(able)]
	if !arbiter.tryClaim([]domain.PawnID{pawn}) {
		return RoundsPopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "pawn_claim")}, true, nil
	}
	service, err := domain.NewRecoveryService(pawn, target, method)
	if err != nil {
		return result, false, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewRecoveryServiceAction(domain.ActionID(fmt.Sprintf("%s-0", id)), service)
	if err != nil {
		return result, false, err
	}
	result, err = r.commit(call, epoch, p, state, started, goal, attemptMethod, id, action)
	return result, true, err
}
