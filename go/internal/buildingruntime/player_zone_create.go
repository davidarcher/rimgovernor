package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitZoneCreate stores explicit player intent to create one closed-preset
// zone over an already-observed connected footprint, under the shared
// player gate. Zone creation is player-command-driven, the same as quest
// acceptance: it never runs through a routine planner or the autopilot-goal-
// bound admission gate, only this direct submission. Submission neither
// acquires authority nor issues a native command.
func (p *Player) SubmitZoneCreate(ctx context.Context, request store.ZoneCreateSubmissionRequest) (store.ZoneCreateSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.ZoneCreateSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupZoneCreateSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.ZoneCreateSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.ZoneCreateSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.ZoneCreateSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.ZoneCreateSubmission{}, false, err
	}
	return p.journal.SubmitZoneCreate(call, request)
}
