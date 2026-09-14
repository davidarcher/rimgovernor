package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitCancelConstruction stores explicit player intent to withdraw the
// still-pending placements of one named build-room intent, under the shared
// player gate. Like the room placement it withdraws, cancellation is
// player-command-driven and never runs through a routine planner or the
// autopilot-goal-bound admission gate. Submission neither acquires authority
// nor issues a native command; a submission whose placements have all already
// resolved commits as an observed-absent record with no plan, which is a
// successful outcome and not a conflict.
func (p *Player) SubmitCancelConstruction(ctx context.Context, request store.CancelConstructionSubmissionRequest) (store.CancelConstructionSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.CancelConstructionSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupCancelConstructionSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.CancelConstructionSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.CancelConstructionSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.CancelConstructionSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.CancelConstructionSubmission{}, false, err
	}
	return p.journal.SubmitCancelConstruction(call, request)
}
