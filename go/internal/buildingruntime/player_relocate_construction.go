package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitRelocateConstruction stores explicit player intent to move one named
// construction somewhere else, under the shared player gate. Like the placement
// it supersedes and the cancellation it subsumes, relocation is
// player-command-driven and never runs through a routine planner or the
// autopilot-goal-bound admission gate. Submission neither acquires authority nor
// issues a native command: it commits one plan holding the withdrawal of the old
// orders and the placement of the new ones, ordered so that no replacement can
// be admitted before the withdrawals have completed.
func (p *Player) SubmitRelocateConstruction(ctx context.Context, request store.RelocateConstructionSubmissionRequest) (store.RelocateConstructionSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.RelocateConstructionSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupRelocateConstructionSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.RelocateConstructionSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.RelocateConstructionSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.RelocateConstructionSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.RelocateConstructionSubmission{}, false, err
	}
	return p.journal.SubmitRelocateConstruction(call, request)
}
