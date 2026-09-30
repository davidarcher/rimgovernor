package store

import (
	"context"
	"testing"
)

// A fresh journal adopts the native backlog as reviewed history, reads on
// from its cursor across a reopen, and refuses once it holds history (#1251).
func TestAdoptClockBacklog(t *testing.T) {
	ctx := context.Background()
	s, path, profile := boundInbox(t)
	adopted, err := s.AdoptClockBacklog(ctx, profile, 1000)
	if err != nil || !adopted {
		t.Fatal(adopted, err)
	}
	review, err := s.ReadClockReview(ctx, profile)
	if err != nil || review.InboxCursor != 1000 || review.ReviewedCursor != 1000 || review.AcknowledgedCursor != 1000 || len(review.Holds) != 0 {
		t.Fatal(review, err)
	}
	reviewAppend(t, s, profile, 1000, 0)
	if adopted, err = s.AdoptClockBacklog(ctx, profile, 2000); err != nil || adopted {
		t.Fatal("adopted a journal with history", adopted, err)
	}
	review, err = s.ReviewClockEvents(ctx, profile, review.Revision)
	if err != nil || review.ReviewedCursor != 1001 || len(review.Holds) != 1 {
		t.Fatal(review, err)
	}
	s.Close()
	s = open(t, path)
	if state, err := s.ReadClockInbox(ctx, profile); err != nil || state.Cursor != 1001 {
		t.Fatal(state, err)
	}
}
