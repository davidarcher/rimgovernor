package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitSurgery stores explicit player intent to queue one exact native
// medical operation on one already-observed living patient, under the shared
// player gate. Surgery is player-command-driven, the same as tend: it never
// runs through a routine planner, only this direct submission. Submission
// neither acquires authority nor issues a native command.
func (p *Player) SubmitSurgery(ctx context.Context, request store.SurgerySubmissionRequest) (store.SurgerySubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.SurgerySubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupSurgerySubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.SurgerySubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.SurgerySubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.SurgerySubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.SurgerySubmission{}, false, err
	}
	return p.journal.SubmitSurgery(call, request)
}
