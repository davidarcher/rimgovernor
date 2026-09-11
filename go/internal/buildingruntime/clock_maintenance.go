package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const clockHistoryTail = 128

// maintainClockAttempts runs without the player gate. Retirement rechecks the
// catalog transactionally and retains unresolved writes and owned epochs. A
// concurrent allocation merely defers maintenance until the next poll.
func (s *ClockScheduler) maintainClockAttempts(ctx context.Context) error {
	head, err := s.player.journal.ReadClockSequence(ctx)
	if err != nil {
		return err
	}
	if head.LastAllocated-head.RetiredThrough < clockHistoryTail {
		return nil
	}
	_, err = s.player.journal.RetireClockHistory(ctx, head, clockHistoryTail)
	if errors.Is(err, store.ErrConflict) {
		return nil
	}
	return err
}
