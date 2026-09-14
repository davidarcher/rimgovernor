package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitBedAssign stores explicit player intent to assign one
// already-observed undrafted pawn to one already-observed bed, under the
// shared player gate. Bed assignment is player-command-driven, the same as
// tend: it never runs through a routine planner, only this direct
// submission. Submission neither acquires authority nor issues a native
// command.
func (p *Player) SubmitBedAssign(ctx context.Context, request store.BedAssignSubmissionRequest) (store.BedAssignSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.BedAssignSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupBedAssignSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.BedAssignSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.BedAssignSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.BedAssignSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.BedAssignSubmission{}, false, err
	}
	return p.journal.SubmitBedAssign(call, request)
}
