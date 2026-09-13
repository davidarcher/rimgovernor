package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func TestClockPollMaintainsAttemptsWithoutPlayerGate(t *testing.T) {
	t.Parallel()
	s, f, _ := clockPollFixture(t)
	ctx := context.Background()
	_, _, _, intent := clockCoreFixture(t)
	var ids []string
	for range 2*clockHistoryTail + 1 {
		intent.RequestID = clockTestNextID(t, s.player.journal)
		intent.Key = intent.RequestID
		if _, _, err := s.player.journal.PrepareClock(ctx, intent); err != nil {
			t.Fatal(err)
		}
		ids = append(ids, intent.RequestID)
	}
	if _, err := s.player.journal.DispatchClock(ctx, ids[0]); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		replacement := proto.Clone(f.status.Context).(*c.ObservationContext)
		replacement.Identity.LoadToken = proto.String("replacement")
		if _, err := s.player.journal.MarkClockScopeSuperseded(ctx, ids[0], replacement); err != nil {
			t.Error(err)
		}
	})
	// Maintenance must remain available while player work is blocked.
	s.player.gate <- struct{}{}
	defer func() { <-s.player.gate }()
	result, err := s.PollEvents(ctx, &clockPollNative{page: clockPollPage(f, 0, "empty")}, 128)
	if err != nil || result.Interrupted {
		t.Fatal(result, err)
	}
	retained, err := s.player.journal.LoadClockAttempts(ctx, 4096)
	if err != nil || len(retained) != clockHistoryTail+1 {
		t.Fatal(len(retained), err)
	}
	unknown, err := s.player.journal.LookupClockAttempt(ctx, ids[0])
	if err != nil || unknown.Phase != store.ClockDispatched {
		t.Fatal(unknown, err)
	}
	if _, err = s.player.journal.LookupClockAttempt(ctx, ids[1]); !errors.Is(err, store.ErrRetired) {
		t.Fatal("old request can be reused", err)
	}
	head, err := s.player.journal.ReadClockSequence(ctx)
	if err != nil || head.RetiredThrough != uint64(len(ids)) {
		t.Fatal(head, err)
	}
	if err = s.maintainClockAttempts(ctx); err != nil {
		t.Fatal(err)
	}
	after, err := s.player.journal.ReadClockSequence(ctx)
	if err != nil || after != head {
		t.Fatal("idle maintenance changed allocation", after, err)
	}
}

func TestClockMaintenanceFailureDisablesAndRollsBack(t *testing.T) {
	t.Parallel()
	s, f, db := clockPollFixture(t)
	ctx := context.Background()
	_, _, _, intent := clockCoreFixture(t)
	for range clockHistoryTail + 1 {
		intent.RequestID = clockTestNextID(t, s.player.journal)
		intent.Key = intent.RequestID
		if _, _, err := s.player.journal.PrepareClock(ctx, intent); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := db.Exec(`CREATE TRIGGER fail_retirement BEFORE DELETE ON clock_attempts BEGIN SELECT RAISE(ABORT,'fixture'); END`); err != nil {
		t.Fatal(err)
	}
	result, err := s.PollEvents(ctx, &clockPollNative{page: clockPollPage(f, 0, "empty")}, 128)
	if err == nil || !result.Interrupted || s.session.State().Enabled {
		t.Fatal(result, err, s.session.State())
	}
	head, err := s.player.journal.ReadClockSequence(ctx)
	if err != nil || head.RetiredThrough != 0 || head.LastAllocated != clockHistoryTail+1 {
		t.Fatal("failed maintenance partially committed", head, err)
	}
}

func TestClockPollCompactsReviewedEventsAndFailsClosed(t *testing.T) {
	t.Parallel()
	s, f, db := clockPollFixture(t)
	ctx := context.Background()
	for i := range 130 {
		result, err := s.PollEvents(ctx, &clockPollNative{page: clockPollPage(f, int64(i), "benign")}, 128)
		if err != nil || result.Interrupted || result.Review.ReviewedCursor != int64(i+1) {
			t.Fatal(i, result, err)
		}
	}
	inbox, err := s.player.journal.ReadClockInbox(ctx, s.config.Profile)
	if err != nil || inbox.PageCount != 10 || inbox.Cursor != 130 {
		t.Fatal(inbox, err)
	}
	if _, err = db.Exec("DELETE FROM clock_history_checkpoint"); err != nil {
		t.Fatal(err)
	}
	result, err := s.PollEvents(ctx, &clockPollNative{page: clockPollPage(f, 130, "empty")}, 128)
	if err == nil || !result.Interrupted || s.session.State().Enabled {
		t.Fatal(result, err)
	}
}
