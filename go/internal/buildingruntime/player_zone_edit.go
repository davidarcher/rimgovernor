package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitZoneEdit stores explicit player intent to apply one bounded edit to
// an already-observed existing native zone, under the shared player gate.
// Zone editing is player-command-driven, the same as zone creation: it
// never runs through a routine planner or the autopilot-goal-bound
// admission gate, only this direct submission. Submission neither acquires
// authority nor issues a native command.
func (p *Player) SubmitZoneEdit(ctx context.Context, request store.ZoneEditSubmissionRequest) (store.ZoneEditSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.ZoneEditSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupZoneEditSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.ZoneEditSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.ZoneEditSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.ZoneEditSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.ZoneEditSubmission{}, false, err
	}
	return p.journal.SubmitZoneEdit(call, request)
}
