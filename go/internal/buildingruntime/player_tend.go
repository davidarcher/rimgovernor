package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitTend stores explicit player intent to have one already-observed
// doctor tend one already-observed patient, under the shared player gate.
// Tend is player-command-driven, the same as research selection: it never
// runs through a routine planner, only this direct submission. Submission
// neither acquires authority nor issues a native command.
func (p *Player) SubmitTend(ctx context.Context, request store.TendSubmissionRequest) (store.TendSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.TendSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupTendSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.TendSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.TendSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.TendSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.TendSubmission{}, false, err
	}
	return p.journal.SubmitTend(call, request)
}
