package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitTrade stores explicit player intent to run one trade sub-operation,
// under the shared player gate. Trade is player-command-driven, the same as
// quest fulfillment: it never runs through a routine planner, only this
// direct submission. Submission neither acquires authority nor issues a
// native command. Only TradeOpen resolves end to end through this surface;
// see store.TradeSubmissionRequest's doc comment for why set_lines/accept/end
// require a same-plan dependency this single-action submission cannot
// declare.
func (p *Player) SubmitTrade(ctx context.Context, request store.TradeSubmissionRequest) (store.TradeSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.TradeSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupTradeSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.TradeSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.TradeSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.TradeSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.TradeSubmission{}, false, err
	}
	return p.journal.SubmitTrade(call, request)
}
