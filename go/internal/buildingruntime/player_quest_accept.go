package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// SubmitQuestAccept stores explicit player intent to accept one
// already-observed quest offer with one already-chosen accepter pawn and
// reward choice, under the shared player gate. Quest acceptance is
// player-command-driven, the same as caravan departure: it never runs
// through a routine planner, only this direct submission. Submission
// neither acquires authority nor issues a native command.
func (p *Player) SubmitQuestAccept(ctx context.Context, request store.QuestAcceptSubmissionRequest) (store.QuestAcceptSubmission, bool, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.QuestAcceptSubmission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupQuestAcceptSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.QuestAcceptSubmission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.QuestAcceptSubmission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.QuestAcceptSubmission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.QuestAcceptSubmission{}, false, err
	}
	return p.journal.SubmitQuestAccept(call, request)
}
