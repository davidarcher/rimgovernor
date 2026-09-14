package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitGoalCreate records explicit player intent to force-activate one
// already-known maintained goal kind right now, under the shared player gate.
//
// Every kind CreateGoal accepts is already an autopilot-managed goal with its
// own deficit assessment (policy.EnsureFoodSupply and friends); this does not
// invent a goal family, it asserts the deficit on the player's word and sources
// the goal to the player. The composed native work that follows is unchanged:
// whichever routine planner already knows how to build a method for that kind
// commits it through the same CommitGoalMethod, which has always admitted
// player-sourced goals -- admitRoutineDevelopment exempts them from the routine
// development arbitration gate, and routineCommitments already counts their
// open work against the autopilot's concurrent-project capacity.
//
// Like every other player command this acquires no authority and issues no
// native call. The world half of the requested snapshot is checked against live
// native identity before submission, the same gate SubmitResourcePolicy uses;
// the direction, plan revision and tick in the snapshot are the caller's, the
// same way the routine reviewer supplies its own.
func (p *Player) SubmitGoalCreate(ctx context.Context, request store.GoalCreateSubmissionRequest) (store.GoalCreateSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.GoalCreateSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupGoalCreateSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.GoalCreateSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.GoalCreateSubmission{}, false, err
	}
	if err = p.world(call, request.World()); err != nil {
		return store.GoalCreateSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.GoalCreateSubmission{}, false, err
	}
	return p.journal.SubmitGoalCreate(call, request)
}

// CancelGoal is the player-facing cancellation path for one already-observed
// goal identity, under the shared player gate.
//
// The cancellation itself is entirely pre-existing: store.CancelPlayerGoal
// reuses the same body store.CancelGoal has always used, so method
// cancellation and progress-journal cancellation are unchanged, and effects
// still reconcile. What is added here is reachability -- a player entry point
// with the world bound and the local CAS revision -- not new goal-cancellation
// logic. Native orders already issued are not erased; cancellation stops new
// controller orders for the goal.
func (p *Player) CancelGoal(ctx context.Context, w store.World, id domain.GoalID, revision uint64) (store.GoalState, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.GoalState{}, err
	}
	defer done()
	if err = p.world(call, w); err != nil {
		return store.GoalState{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.GoalState{}, err
	}
	return p.journal.CancelPlayerGoal(call, w, id, revision)
}

// PlayerGoals reports every goal identity the player has activated for one
// world, keyed by kind: the read side of SubmitGoalCreate, and the source of
// the cancellable goal identities a cancel_goal command is bounded against.
func (p *Player) PlayerGoals(ctx context.Context, w store.World) (map[domain.GoalKind]domain.GoalID, error) {
	return p.journal.PlayerGoals(ctx, w)
}

// LookupGoalCreateSubmission returns one stored goal activation request by
// request ID.
func (p *Player) LookupGoalCreateSubmission(ctx context.Context, requestID string) (store.GoalCreateSubmission, error) {
	return p.journal.LookupGoalCreateSubmission(ctx, requestID)
}
