package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitCaravanDeparture stores explicit player intent to form and send an
// already-selected crew/cargo toward an already-scouted world tile, under
// the shared player gate. Caravan departure is player-command-driven, unlike
// the fixed-priority a-e routine families: it never runs through a routine
// planner, only this direct submission (mirrors SubmitDraft/Submit).
// Submission neither acquires authority nor issues a native command.
func (p *Player) SubmitCaravanDeparture(ctx context.Context, request store.CaravanDepartureSubmissionRequest) (store.CaravanDepartureSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.CaravanDepartureSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupCaravanDepartureSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.CaravanDepartureSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.CaravanDepartureSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.CaravanDepartureSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.CaravanDepartureSubmission{}, false, err
	}
	return p.journal.SubmitCaravanDeparture(call, request)
}
