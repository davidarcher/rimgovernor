package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitResearchSelect stores explicit player intent to set the native
// current research project to one already-queued, prerequisite-ordered
// project, under the shared player gate. Research selection is
// player-command-driven, the same as quest acceptance: it never runs
// through a routine planner, only this direct submission. Submission
// neither acquires authority nor issues a native command.
func (p *Player) SubmitResearchSelect(ctx context.Context, request store.ResearchSelectSubmissionRequest) (store.ResearchSelectSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.ResearchSelectSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupResearchSelectSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.ResearchSelectSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.ResearchSelectSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.ResearchSelectSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.ResearchSelectSubmission{}, false, err
	}
	return p.journal.SubmitResearchSelect(call, request)
}
