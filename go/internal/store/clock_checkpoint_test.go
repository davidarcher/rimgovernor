package store

import (
	"context"
	"errors"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
)

func appendReviewedBenign(t *testing.T, s *Store, profile string) ClockReviewState {
	t.Helper()
	ctx := context.Background()
	review, err := s.ReadClockReview(ctx, profile)
	if err != nil {
		t.Fatal(err)
	}
	r, p := inboxPage(review.InboxCursor, 1, 0)
	if _, _, err = s.AppendClockEvents(ctx, profile, r, p); err != nil {
		t.Fatal(err)
	}
	review, err = s.ReviewClockEvents(ctx, profile, review.Revision)
	if err != nil {
		t.Fatal(err)
	}
	return review
}

// TestClockCompactionRepeatedWindowsAndAcknowledgementReplay exercises the
// repeated review-log compaction with a short capacity and history tail.
// It retains a real database so closing and reopening proves persistence.
// It must not run in parallel with any other test in this package: it
// mutates shared package state for its duration and restores it on cleanup.
func TestClockCompactionRepeatedWindowsAndAcknowledgementReplay(t *testing.T) {
	const capacity = 16
	original := clock.ReviewCapacity
	originalTail := clock.HistoryTail
	clock.ReviewCapacity = capacity
	clock.HistoryTail = capacity
	t.Cleanup(func() {
		clock.ReviewCapacity = original
		clock.HistoryTail = originalTail
	})

	ctx := context.Background()
	s, path, profile := boundInbox(t)
	reviewAppend(t, s, profile, 0, 2)
	review, err := s.ReviewClockEvents(ctx, profile, 0)
	if err != nil {
		t.Fatal(err)
	}
	ack := ClockAcknowledgement{RequestID: "inspected", ExpectedRevision: review.Revision, ThroughCursor: review.ReviewedCursor}
	originalAck, err := s.AcknowledgeClockEvents(ctx, profile, ack)
	if err != nil {
		t.Fatal(err)
	}
	compactions := 0
	for i := range clock.ReviewCapacity + 16 {
		review = appendReviewedBenign(t, s, profile)
		result, err := s.CompactClockHistory(ctx, profile)
		if err != nil {
			t.Fatal(i, err)
		}
		if result.RemovedPages > 0 {
			compactions++
		}
	}
	if compactions < 2 {
		t.Fatalf("only %d compactions; want repeated windows", compactions)
	}
	s.Close()
	s = open(t, path)
	current, err := s.ReadClockReview(ctx, profile)
	if err != nil || !reflect.DeepEqual(current, review) {
		t.Fatal(current, review, err)
	}
	state, err := s.ReadClockInbox(ctx, profile)
	if err != nil || state.Cursor != int64(clock.ReviewCapacity)+19 || state.LostCount != 2 || !state.Gap || state.PageCount >= clock.HistoryTail {
		t.Fatal(state, err)
	}
	replayed, err := s.AcknowledgeClockEvents(ctx, profile, ack)
	if err != nil || !reflect.DeepEqual(replayed, originalAck) {
		t.Fatal(replayed, originalAck, err)
	}
	ack.ThroughCursor = current.ReviewedCursor
	if _, err = s.AcknowledgeClockEvents(ctx, profile, ack); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	// Capturing another interruption must not be covered by the archived reply.
	reviewAppend(t, s, profile, current.InboxCursor, 0)
	current, err = s.ReviewClockEvents(ctx, profile, current.Revision)
	if err != nil || len(current.Holds) != 1 {
		t.Fatal(current, err)
	}
	old, _ := inboxPage(0, 1, 2)
	_, page := inboxPage(0, 1, 2)
	if _, _, err = s.AppendClockEvents(ctx, profile, old, page); !errors.Is(err, ErrConflict) {
		t.Fatal("retired page reset cursor", err)
	}
}

func TestClockCompactionPreservesUnreviewedAndUnacknowledged(t *testing.T) {
	// Keep enough pages to compact beyond the retained eight-page tail.
	// Run serially because HistoryTail is shared with the other clock tests.
	originalTail := clock.HistoryTail
	clock.HistoryTail = 16
	t.Cleanup(func() { clock.HistoryTail = originalTail })
	for _, kind := range []string{"unreviewed", "interruption", "gap"} {
		t.Run(kind, func(t *testing.T) {
			ctx := context.Background()
			s, _, profile := boundInbox(t)
			tail := clock.HistoryTail
			for range tail {
				appendReviewedBenign(t, s, profile)
			}
			before, err := s.ReadClockReview(ctx, profile)
			if err != nil {
				t.Fatal(err)
			}
			lost := uint64(0)
			if kind == "gap" {
				lost = 2
			}
			reviewAppend(t, s, profile, before.InboxCursor, lost)
			if kind != "unreviewed" {
				if _, err = s.ReviewClockEvents(ctx, profile, before.Revision); err != nil {
					t.Fatal(err)
				}
			}
			before, err = s.ReadClockReview(ctx, profile)
			if err != nil {
				t.Fatal(err)
			}
			compacted, err := s.CompactClockHistory(ctx, profile)
			if err != nil || compacted.RemovedPages != tail+1-8 {
				t.Fatal(compacted, err)
			}
			after, err := s.ReadClockReview(ctx, profile)
			if err != nil || !reflect.DeepEqual(after, before) {
				t.Fatal(before, after, err)
			}
			inbox, err := s.LoadClockInbox(ctx, profile, 4096)
			if err != nil || len(inbox.Pages) != 8 || inbox.Pages[7].Page.Events[0].GetCursor() != int64(tail)+1 {
				t.Fatal(inbox, err)
			}
			if kind == "unreviewed" {
				after, err = s.ReviewClockEvents(ctx, profile, after.Revision)
				if err != nil || len(after.Holds) != 1 {
					t.Fatal(after, err)
				}
			}
		})
	}
}

func TestClockCompactionPinsEarlyHoldUntilExplicitAcknowledgement(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, profile := boundInbox(t)
	reviewAppend(t, s, profile, 0, 2)
	if _, err := s.ReviewClockEvents(ctx, profile, 0); err != nil {
		t.Fatal(err)
	}
	tail := clock.HistoryTail
	for range tail {
		appendReviewedBenign(t, s, profile)
	}
	before, err := s.ReadClockReview(ctx, profile)
	if err != nil {
		t.Fatal(err)
	}
	compacted, err := s.CompactClockHistory(ctx, profile)
	if err != nil || compacted.RemovedPages != 0 || compacted.RemovedReviews != tail+1 {
		t.Fatal(compacted, err)
	}
	after, err := s.ReadClockReview(ctx, profile)
	if err != nil || !reflect.DeepEqual(before, after) || len(after.Holds) != 2 {
		t.Fatal(after, err)
	}
	ack := ClockAcknowledgement{RequestID: "early-hold", ExpectedRevision: after.Revision, ThroughCursor: after.ReviewedCursor}
	original, err := s.AcknowledgeClockEvents(ctx, profile, ack)
	if err != nil {
		t.Fatal(err)
	}
	compacted, err = s.CompactClockHistory(ctx, profile)
	// Every page but the retained eight-page tail.
	if err != nil || compacted.RemovedPages != tail+1-8 {
		t.Fatal(compacted, err)
	}
	replayed, err := s.AcknowledgeClockEvents(ctx, profile, ack)
	if err != nil || !reflect.DeepEqual(original, replayed) {
		t.Fatal(original, replayed, err)
	}
}

func TestClockCompactionRollbackAndCheckpointValidation(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, profile := boundInbox(t)
	tail := clock.HistoryTail
	for range tail {
		appendReviewedBenign(t, s, profile)
	}
	before, err := s.ReadClockReview(ctx, profile)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec(`CREATE TRIGGER fail_compact BEFORE DELETE ON clock_review_log BEGIN SELECT RAISE(ABORT,'fixture'); END`); err != nil {
		t.Fatal(err)
	}
	if _, err = s.CompactClockHistory(ctx, profile); err == nil {
		t.Fatal("expected rollback")
	}
	after, err := s.ReadClockReview(ctx, profile)
	if err != nil || !reflect.DeepEqual(after, before) {
		t.Fatal(before, after, err)
	}
	inbox, err := s.ReadClockInbox(ctx, profile)
	if err != nil || inbox.PageCount != tail {
		t.Fatal(inbox, err)
	}
	if _, err = s.db.Exec("DROP TRIGGER fail_compact"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.CompactClockHistory(ctx, profile); err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec("DELETE FROM clock_history_checkpoint"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.ReadClockReview(ctx, profile); err == nil {
		t.Fatal("missing checkpoint accepted")
	}
	if _, err = s.ReadClockInbox(ctx, profile); err == nil {
		t.Fatal("missing checkpoint reset cursor")
	}
}
