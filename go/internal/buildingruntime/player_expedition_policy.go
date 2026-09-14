package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitExpeditionPolicy stores explicit player intent to change some of the
// world's expedition risk limits, under the shared player gate every other
// player submission uses. Like SubmitPopulationPolicy it commits no plan and
// no action: the limits are pure colony configuration read later by caravan
// and world-evaluation policy and issue no native command, so there is
// nothing to admit or dispatch. The world and current epoch are still
// checked, so limits can only be recorded against the colony/load/map the
// player is actually looking at, and the request is a partial patch that the
// store merges over whatever is already in force.
func (p *Player) SubmitExpeditionPolicy(ctx context.Context, request store.ExpeditionPolicySubmissionRequest) (store.ExpeditionPolicySubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.ExpeditionPolicySubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupExpeditionPolicySubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.ExpeditionPolicySubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.ExpeditionPolicySubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.ExpeditionPolicySubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.ExpeditionPolicySubmission{}, false, err
	}
	return p.journal.SubmitExpeditionPolicy(call, request)
}

// ExpeditionPolicy reads the expedition limits in force for one world, which
// are the contract defaults until the player has submitted a request.
func (p *Player) ExpeditionPolicy(ctx context.Context, world store.World) (domain.ExpeditionPolicy, error) {
	return p.journal.CurrentExpeditionPolicy(ctx, world)
}
