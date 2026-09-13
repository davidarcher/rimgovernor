package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitSettlementGift stores explicit player intent to gift an exact
// silver amount from one already-observed, already-visiting caravan to the
// exact faction of the settlement it currently sits at, under the shared
// player gate. Settlement gifting is player-command-driven, the same as
// caravan departure and quest acceptance: it never runs through a routine
// planner, only this direct submission. Submission neither acquires
// authority nor issues a native command.
func (p *Player) SubmitSettlementGift(ctx context.Context, request store.SettlementGiftSubmissionRequest) (store.SettlementGiftSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.SettlementGiftSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupSettlementGiftSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.SettlementGiftSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.SettlementGiftSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.SettlementGiftSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.SettlementGiftSubmission{}, false, err
	}
	return p.journal.SubmitSettlementGift(call, request)
}
