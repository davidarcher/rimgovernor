package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Keep the need in deficit between admission and the first claim receipt.
// Otherwise a second review would recover it because its own pending draft
// correctly excludes the pawn from fresh adoption candidates.
func idleDraftWorkOpen(plan store.PlanState) bool {
	return strings.HasPrefix(string(plan.Spec.ID()), "routine-idle-draft-") && domain.GoalWorkOpen(plan.Progress)
}

// idleDrafts observes only candidates for adoption. Hands still previews and
// dispatches SetDrafted with its ordinary generation, pawn CAS and claim checks.
func (r *RoutineReviewer) idleDrafts(ctx context.Context, state ControlState, tick domain.Tick, emergency policy.EmergencyFacts, plans []store.PlanState) ([]domain.PawnID, error) {
	if emergency.ColonistsComplete != domain.Known(true) || emergency.ThreatsComplete != domain.Known(true) || len(emergency.Colonists) == 0 {
		return nil, nil
	}
	for _, threat := range emergency.Threats {
		if threat.Kind == policy.Hostile || threat.Building() || threat.Kind == policy.HuntingPredator && !threat.DistantThreat() {
			return nil, nil
		}
	}
	held := map[domain.PawnID]bool{}
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			draft, ok := progress.Action().OwnedDraft()
			if !ok {
				continue
			}
			v := progress.View()
			// Include unissued defensive-position drafts as well as claims and
			// in-flight attempts; none may race an adopt-and-release method.
			if domain.GoalWorkOpen([]domain.Progress{progress}) || draftOutstanding(progress) || workerPlanHoldsDraft(plan, v) {
				held[draft.Pawn()] = true
			}
		}
	}
	ids := make([]string, 0, len(emergency.Colonists))
	wanted := map[string]bool{}
	for _, pawn := range emergency.Colonists {
		id := string(pawn.ID)
		if id == "" || wanted[id] {
			return nil, ErrControl
		}
		wanted[id] = true
		ids = append(ids, id)
	}
	reply, _, err := r.native.ReadRoutinePawns(ctx, boundary.Identity(state.Snapshot), ids)
	if err != nil {
		return nil, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return nil, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || !domain.Tick(observed.Context.GetTick()).Covers(tick) {
		return nil, ErrControl
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return nil, nil
	}
	var candidates []domain.PawnID
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || !wanted[row.Pawn.GetId()] {
			return nil, ErrControl
		}
		delete(wanted, row.Pawn.GetId())
		id := domain.PawnID(row.Pawn.GetId())
		if held[id] || !idleUnclaimedDraft(row) {
			continue
		}
		if _, err = boundary.PawnToken(row, observed.Context); err != nil {
			continue
		}
		candidates = append(candidates, id)
	}
	sort.Slice(candidates, func(i, j int) bool { return candidates[i] < candidates[j] })
	return candidates, nil
}

func idleUnclaimedDraft(row *n.PawnState) bool {
	return boundary.FactBool(row.Colonist) == domain.Known(true) &&
		boundary.FactBool(row.Drafted) == domain.Known(true) &&
		boundary.FactBool(row.Dead) == domain.Known(false) &&
		boundary.FactBool(row.Downed) == domain.Known(false) &&
		boundary.FactPresence(row.MentalState, row.Issues, "mental_state") == domain.Known(false) &&
		row.GetDraftClaim().GetUnowned() != nil && row.Job != nil &&
		!boundary.IssueField(row.Job.Issues, "player_forced") && !boundary.IssueField(row.Job.Issues, "queued_jobs") &&
		boundary.FactBool(row.Job.PlayerForced) == domain.Known(false) &&
		boundary.FactUint(row.Job.QueuedJobs) == domain.Known(uint32(0))
}

// restoreIdleDrafts admits a draft with no dependent order: completion pairs
// adoption with the worker's ordinary undraft release, including uncertainty
// reconciliation. Existing claims never enter this method.
func (r *RoutineReviewer) restoreIdleDrafts(ctx, epoch context.Context, arbiter *stepArbiter) error {
	p := r.player
	state := p.session.State()
	if !state.Enabled {
		return nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return ErrControl
	}
	review, err := p.journal.LoadRoutineReview(ctx)
	if err != nil {
		return err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return nil
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == policy.RestoreWorkers {
			goal, err = p.journal.LoadGoal(ctx, binding.Goal)
			if err != nil {
				return err
			}
			break
		}
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return nil
	}
	plans, err := p.journal.LoadPlans(ctx, 256)
	if err != nil {
		return err
	}
	for _, method := range goal.Methods {
		for _, plan := range plans {
			if plan.Spec.ID() == method.Plan && domain.GoalWorkOpen(plan.Progress) {
				return nil
			}
		}
	}
	started := r.clock.Now()
	emergency, _, err := r.native.ReadEmergency(ctx, boundary.Identity(state.Snapshot))
	if err != nil {
		return err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || !domain.Tick(emergency.Context.GetTick()).Covers(review.Tick) {
		return ErrControl
	}
	pawns, err := r.idleDrafts(ctx, state, domain.Tick(emergency.Context.GetTick()), emergency.Facts, plans)
	if err != nil || len(pawns) == 0 {
		return err
	}
	if !arbiter.tryClaim(pawns) {
		return nil
	}
	hash := sha256.New()
	fmt.Fprint(hash, pawns)
	method, id := defenseMethodIDs("idle-draft", goal, hash)
	id = domain.PlanID("routine-idle-draft-" + strings.TrimPrefix(string(id), "routine-defense-"))
	var actions []domain.Action
	for _, pawn := range pawns {
		draft, err := domain.NewOwnedDraft(pawn)
		if err != nil {
			return err
		}
		action, err := domain.NewOwnedDraftAction(domain.ActionID(fmt.Sprintf("%s-draft-%s", id, pawn)), draft)
		if err != nil {
			return err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return err
	}
	if err = p.current(ctx, epoch); err != nil {
		return err
	}
	elapsed := r.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.maxAge {
		return ErrControl
	}
	_, err = p.journal.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, method, plan)
	return err
}
