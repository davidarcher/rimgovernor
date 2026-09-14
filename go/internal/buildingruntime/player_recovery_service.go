package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitRecoveryService stores explicit player intent to send one
// already-observed undrafted pawn to repair, restore or refuel one
// already-observed building, under the shared player gate. Recovery service
// is player-command-driven, the same as tend: it never runs through a
// routine planner, only this direct submission. Submission neither acquires
// authority nor issues a native command.
func (p *Player) SubmitRecoveryService(ctx context.Context, request store.RecoveryServiceSubmissionRequest) (store.RecoveryServiceSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.RecoveryServiceSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupRecoveryServiceSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.RecoveryServiceSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.RecoveryServiceSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.RecoveryServiceSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.RecoveryServiceSubmission{}, false, err
	}
	return p.journal.SubmitRecoveryService(call, request)
}
