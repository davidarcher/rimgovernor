package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitRescue stores explicit player intent to have one already-observed
// rescuer rescue one already-observed patient, under the shared player
// gate. Rescue is player-command-driven, the same as tend: it never runs
// through a routine planner, only this direct submission. Submission
// neither acquires authority nor issues a native command.
func (p *Player) SubmitRescue(ctx context.Context, request store.RescueSubmissionRequest) (store.RescueSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.RescueSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupRescueSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.RescueSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.RescueSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.RescueSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.RescueSubmission{}, false, err
	}
	return p.journal.SubmitRescue(call, request)
}
