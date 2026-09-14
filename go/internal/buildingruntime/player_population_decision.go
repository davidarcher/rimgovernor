package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitPopulationDecision stores explicit player intent to rescue, capture or
// recruit one exact observed individual, or to withdraw future population
// orders for them, under the shared player gate every other player submission
// uses. Like SubmitPopulationPolicy it commits no plan and no action: a
// decision is a persistent player-sourced record read later, and issues no
// native command of its own, so there is nothing to admit or dispatch here.
// The world and current epoch are still checked, so a decision can only be
// recorded against the colony/load/map the player is actually looking at, and
// the store enforces the population-policy precondition for the three
// non-ignore decisions.
func (p *Player) SubmitPopulationDecision(ctx context.Context, request store.PopulationDecisionSubmissionRequest) (store.PopulationDecisionSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.PopulationDecisionSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupPopulationDecisionSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.PopulationDecisionSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.PopulationDecisionSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.PopulationDecisionSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.PopulationDecisionSubmission{}, false, err
	}
	return p.journal.SubmitPopulationDecision(call, request)
}

// PopulationDecisions reads every per-pawn direction recorded for one world.
func (p *Player) PopulationDecisions(ctx context.Context, world store.World) ([]domain.PopulationDirective, error) {
	return p.journal.PopulationDecisions(ctx, world)
}
