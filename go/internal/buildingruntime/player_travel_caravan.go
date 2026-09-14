package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitTravelCaravan stores explicit player intent to route or hold one
// already-observed, already-formed player caravan, under the shared player
// gate. This covers both hold_caravan (TravelStop) and route_caravan
// (Move/Visit/ReturnHome) interpreter commands, since both compile to the
// same domain.TravelCaravan action distinguished only by TravelKind. Travel
// is player-command-driven, the same as quest acceptance: it never runs
// through a routine planner, only this direct submission. Submission
// neither acquires authority nor issues a native command.
func (p *Player) SubmitTravelCaravan(ctx context.Context, request store.TravelCaravanSubmissionRequest) (store.TravelCaravanSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.TravelCaravanSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupTravelCaravanSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.TravelCaravanSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.TravelCaravanSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.TravelCaravanSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.TravelCaravanSubmission{}, false, err
	}
	return p.journal.SubmitTravelCaravan(call, request)
}
