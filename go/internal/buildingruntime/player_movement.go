package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitMovement stores explicit player intent to walk one already-observed
// pawn to one already-observed cell, under the shared player gate. Like
// tend, it never runs through a routine planner, only this direct
// submission — but unlike tend, the committed plan carries two actions (an
// OwnedDraft action followed by the dependent Movement action), the same
// shape interpreter.moveActions builds. Submission neither acquires
// authority nor issues a native command.
func (p *Player) SubmitMovement(ctx context.Context, request store.MovementSubmissionRequest) (store.MovementSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.MovementSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupMovementSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.MovementSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.MovementSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.MovementSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.MovementSubmission{}, false, err
	}
	return p.journal.SubmitMovement(call, request)
}
