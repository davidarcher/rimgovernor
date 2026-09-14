package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitResourcePolicy stores explicit player intent to change one resource's
// spending restriction or protected reserve, under the shared player gate.
// Both Python commands (ModifyResourcePolicy and SetResourceReserve) land here:
// they share one handler, one persistent per-resource policy and one native
// SetProductionPolicy dispatch of the whole merged set.
//
// Like zone creation this is player-command-driven: it never runs through the
// routine planner or the autopilot-goal-bound admission gate, only this direct
// submission, which commits its own one-action plan. The autopilot's own
// production-policy writer (RoutineProductionPolicyPlanner) is untouched and
// keeps committing through CommitGoalMethod; both converge on the same
// unchanged executor/bridge dispatch. Submission neither acquires authority nor
// issues a native command.
func (p *Player) SubmitResourcePolicy(ctx context.Context, request store.ResourcePolicySubmissionRequest) (store.ResourcePolicySubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.ResourcePolicySubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupResourcePolicySubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.ResourcePolicySubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.ResourcePolicySubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.ResourcePolicySubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.ResourcePolicySubmission{}, false, err
	}
	return p.journal.SubmitResourcePolicy(call, request)
}

// ResourcePolicies reports every per-resource directive the player has declared
// for one world, the read side of SubmitResourcePolicy.
func (p *Player) ResourcePolicies(ctx context.Context, w store.World) ([]domain.ResourceDirective, error) {
	return p.journal.ResourcePolicies(ctx, w)
}

// LookupResourcePolicySubmission returns one stored resource policy request by
// request ID.
func (p *Player) LookupResourcePolicySubmission(ctx context.Context, requestID string) (store.ResourcePolicySubmission, error) {
	return p.journal.LookupResourcePolicySubmission(ctx, requestID)
}
