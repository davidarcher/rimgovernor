package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitDraft stores explicit intent under the shared player gate. Submission
// neither acquires authority nor issues a native draft command.
func (p *Player) SubmitDraft(ctx context.Context, request store.DraftSubmissionRequest) (store.DraftSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.DraftSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupDraftSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.DraftSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.DraftSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.DraftSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.DraftSubmission{}, false, err
	}
	return p.journal.SubmitDraft(call, request)
}
