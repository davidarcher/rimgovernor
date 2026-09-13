package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitQuestFulfill stores explicit player intent to fulfill one
// already-accepted quest's native settlement trade-request objective with
// one already-chosen visiting caravan, under the shared player gate. Quest
// fulfillment is player-command-driven, the same as quest acceptance and
// settlement gifting: it never runs through a routine planner, only this
// direct submission. Submission neither acquires authority nor issues a
// native command.
func (p *Player) SubmitQuestFulfill(ctx context.Context, request store.QuestFulfillSubmissionRequest) (store.QuestFulfillSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.QuestFulfillSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupQuestFulfillSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.QuestFulfillSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.QuestFulfillSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.QuestFulfillSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.QuestFulfillSubmission{}, false, err
	}
	return p.journal.SubmitQuestFulfill(call, request)
}
