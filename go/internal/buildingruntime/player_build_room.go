package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitBuildRoom stores explicit player intent to build one room shell over
// an already-observed rectangle, under the shared player gate. Room shells are
// player-command-driven exactly as zone creation is: they never run through a
// routine planner or the autopilot-goal-bound admission gate, only this direct
// submission. Submission neither acquires authority nor issues any native
// placement; the committed building actions are dispatched later by the same
// worker path the autopilot shelter routine's own placements use.
func (p *Player) SubmitBuildRoom(ctx context.Context, request store.BuildRoomSubmissionRequest) (store.BuildRoomSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.BuildRoomSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupBuildRoomSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.BuildRoomSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.BuildRoomSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.BuildRoomSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.BuildRoomSubmission{}, false, err
	}
	return p.journal.SubmitBuildRoom(call, request)
}
