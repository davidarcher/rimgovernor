package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitPopulationPolicy stores explicit player intent to set the colony's
// population capacity policy, under the shared player gate that every other
// player submission uses. Unlike SubmitZoneEdit and its siblings this
// commits no plan and no action: the policy is pure colony configuration
// read later by population and food-reserve policy, and issues no native
// command, so there is nothing to admit or dispatch. The world and current
// epoch are still checked, so a policy can only be recorded against the
// colony/load/map the player is actually looking at.
func (p *Player) SubmitPopulationPolicy(ctx context.Context, request store.PopulationPolicySubmissionRequest) (store.PopulationPolicySubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.PopulationPolicySubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupPopulationPolicySubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.PopulationPolicySubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.PopulationPolicySubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.PopulationPolicySubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.PopulationPolicySubmission{}, false, err
	}
	return p.journal.SubmitPopulationPolicy(call, request)
}

// PopulationPolicy reads the current population policy for one world.
func (p *Player) PopulationPolicy(ctx context.Context, world store.World) (domain.PopulationPolicy, error) {
	return p.journal.CurrentPopulationPolicy(ctx, world)
}
