package store

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

func reviewAppend(t *testing.T, s *Store, profile string, after int64, lost uint64) {
	t.Helper()
	r, p := inboxPage(after, 1, lost)
	p.Events[0].Event = &k.Event_Notification{Notification: &k.Notification{Source: &k.Notification_Letter{Letter: &k.Letter{Id: proto.String("letter"), Label: proto.String("warning")}}}}
	if _, _, err := s.AppendClockEvents(context.Background(), profile, r, p); err != nil {
		t.Fatal(err)
	}
}
func TestClockReviewCaptureReviewAckAndReplay(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, profile := boundInbox(t)
	empty, err := s.ReviewClockEvents(ctx, profile, 0)
	if err != nil || empty.Revision != 0 || len(empty.Holds) != 0 {
		t.Fatal(empty, err)
	}
	reviewAppend(t, s, profile, 0, 2)
	unread, err := s.ReadClockReview(ctx, profile)
	if err != nil || unread.InboxCursor != 3 || unread.ReviewedCursor != 0 || len(unread.Holds) != 0 {
		t.Fatal(unread, err)
	}
	reviewed, err := s.ReviewClockEvents(ctx, profile, 0)
	if err != nil || reviewed.Revision != 1 || reviewed.ReviewedCursor != 3 || !reflect.DeepEqual(reviewed.Holds, []ClockHold{{Kind: ClockGapHold, FromCursor: 1, ThroughCursor: 3}, {Kind: ClockInterruptionHold, FromCursor: 1, ThroughCursor: 1}}) {
		t.Fatal(reviewed, err)
	}
	if _, err = s.ReviewClockEvents(ctx, profile, 0); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	for _, cursor := range []int64{-1, 1, 4} {
		if _, err = s.AcknowledgeClockEvents(ctx, profile, ClockAcknowledgement{RequestID: "bad", ExpectedRevision: 1, ThroughCursor: cursor}); !errors.Is(err, ErrConflict) {
			t.Fatal(cursor, err)
		}
	}
	ack := ClockAcknowledgement{RequestID: "ack", ExpectedRevision: 1, ThroughCursor: 3}
	acknowledged, err := s.AcknowledgeClockEvents(ctx, profile, ack)
	if err != nil || acknowledged.Revision != 2 || acknowledged.AcknowledgedCursor != 3 || len(acknowledged.Holds) != 0 {
		t.Fatal(acknowledged, err)
	}
	reviewAppend(t, s, profile, 3, 0)
	next, err := s.ReviewClockEvents(ctx, profile, 2)
	if err != nil || next.Revision != 3 || len(next.Holds) != 1 || next.Holds[0].FromCursor != 4 {
		t.Fatal(next, err)
	}
	replay, err := s.AcknowledgeClockEvents(ctx, profile, ack)
	if err != nil || !reflect.DeepEqual(replay, acknowledged) {
		t.Fatal(replay, err)
	}
	if _, err = s.AcknowledgeClockEvents(ctx, profile, ClockAcknowledgement{RequestID: "ack", ExpectedRevision: 3, ThroughCursor: 4}); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	current, err := s.ReadClockReview(ctx, profile)
	if err != nil || !reflect.DeepEqual(current, next) {
		t.Fatal(current, err)
	}
	inbox, err := s.ReadClockInbox(ctx, profile)
	if err != nil || !inbox.Gap || inbox.LostCount != 2 || inbox.Cursor != 4 {
		t.Fatal(inbox, err)
	}
}
func TestClockReviewEmptyAckDoesNotCoverNewEvents(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, profile := boundInbox(t)
	ack := ClockAcknowledgement{RequestID: "empty", ExpectedRevision: 0, ThroughCursor: 0}
	initial, err := s.AcknowledgeClockEvents(ctx, profile, ack)
	if err != nil || initial.Revision != 0 {
		t.Fatal(initial, err)
	}
	reviewAppend(t, s, profile, 0, 0)
	replay, err := s.AcknowledgeClockEvents(ctx, profile, ack)
	if err != nil || !reflect.DeepEqual(replay, initial) {
		t.Fatal(replay, err)
	}
	current, err := s.ReadClockReview(ctx, profile)
	if err != nil || current.InboxCursor != 1 || current.ReviewedCursor != 0 || current.AcknowledgedCursor != 0 {
		t.Fatal(current, err)
	}
}
func TestClockReviewEventClassification(t *testing.T) {
	t.Parallel()
	benign := []*k.Event{{Event: &k.Event_Started{}}, {Event: &k.Event_SpeedChanged{}}, {Event: &k.Event_HostilesCleared{}}, {Event: &k.Event_ForcePauseCleared{}}, {Event: &k.Event_OperationOutcome{}}, {Event: &k.Event_AuthorityChanged{}}}
	for _, event := range benign {
		if clock.EventInterrupts(event) {
			t.Fatal(event)
		}
	}
	for number := range k.StopReason_name {
		if number == 0 {
			continue
		}
		reason := k.StopReason(number)
		event := &k.Event{Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: reason.Enum()}}}
		want := reason != k.StopReason_STOP_REASON_TICK_BUDGET && reason != k.StopReason_STOP_REASON_REQUESTED_PAUSE && reason != k.StopReason_STOP_REASON_WATCH_LATCHED
		if clock.EventInterrupts(event) != want || clock.BenignStop(reason) == want {
			t.Fatal(reason)
		}
	}
	for _, event := range []*k.Event{{Event: &k.Event_Notification{}}, {Event: &k.Event_PauseFailed{}}, {Event: &k.Event_ForcePauseWaiting{}}} {
		if !clock.EventInterrupts(event) {
			t.Fatal(event)
		}
	}
	// A game alert is a fact row for the planners and the flight recorder,
	// not a hold: the native supervisor never stops play for one (#244).
	if clock.EventInterrupts(&k.Event{Event: &k.Event_Alert{}}) {
		t.Fatal("alert interrupts")
	}
	// So is an injury observation: sub-threshold combat damage the native
	// supervisor coalesces without stopping; a threshold crossing is its
	// own COLONIST_HEALTH stop (#318).
	if clock.EventInterrupts(&k.Event{Event: &k.Event_InjuryObserved{}}) {
		t.Fatal("injury observation interrupts")
	}
	// A letter pause is the game's own pause; for an informational letter
	// (the classes the supervisor never stops for) it holds nothing (#228).
	// A threat letter, or a pause with no letter attributed, still holds.
	letterPause := func(def string) *k.Event {
		stop := &k.StopEvent{Reason: k.StopReason_STOP_REASON_LETTER_PAUSE.Enum()}
		if def != "" {
			stop.Evidence = &k.StopEvent_Pause{Pause: &k.PauseEvidence{Letter: &k.Letter{Id: proto.String("Letter_1"), DefName: proto.String(def)}}}
		}
		return &k.Event{Event: &k.Event_Stopped{Stopped: stop}}
	}
	for def, interrupts := range map[string]bool{"PositiveEvent": false, "NeutralEvent": false, "NegativeEvent": false, "ThreatBig": true, "ThreatSmall": true, "Death": true, "": true} {
		if clock.EventInterrupts(letterPause(def)) != interrupts {
			t.Fatalf("letter pause %q interrupts=%v", def, !interrupts)
		}
	}
	if !clock.BenignStopEvent(letterPause("PositiveEvent").GetStopped()) || clock.BenignStopEvent(letterPause("ThreatBig").GetStopped()) {
		t.Fatal("BenignStopEvent disagrees with EventInterrupts")
	}
}
func TestClockReviewRollbackAndCorruptProvenance(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	t.Run("rollback", func(t *testing.T) {
		s, _, profile := boundInbox(t)
		reviewAppend(t, s, profile, 0, 0)
		if _, err := s.db.Exec(`CREATE TRIGGER fail_review BEFORE UPDATE ON clock_review BEGIN SELECT RAISE(ABORT,'fixture'); END`); err != nil {
			t.Fatal(err)
		}
		if _, err := s.ReviewClockEvents(ctx, profile, 0); err == nil {
			t.Fatal("expected rollback")
		}
		state, err := s.ReadClockReview(ctx, profile)
		if err != nil || state.Revision != 0 || state.ReviewedCursor != 0 {
			t.Fatal(state, err)
		}
	})
	cases := map[string]string{
		"missing head":          "DELETE FROM clock_review",
		"missing log":           "DELETE FROM clock_review_log",
		"unknown field":         `UPDATE clock_review_log SET payload=CAST('{"Kind":"review","InboxCursor":1,"ExpectedRevision":0,"ThroughCursor":1,"RequestID":"","Extra":true}' AS BLOB)`,
		"revision skip":         `UPDATE clock_review_log SET payload=CAST('{"Kind":"review","InboxCursor":1,"ExpectedRevision":9,"ThroughCursor":1,"RequestID":""}' AS BLOB)`,
		"unsaved cursor":        `UPDATE clock_review_log SET payload=CAST('{"Kind":"review","InboxCursor":2,"ExpectedRevision":0,"ThroughCursor":2,"RequestID":""}' AS BLOB)`,
		"head mismatch":         `UPDATE clock_review SET payload=CAST('{"Revision":2,"Reviewed":1,"Acknowledged":0}' AS BLOB)`,
		"lost inbox provenance": "DELETE FROM clock_inbox_events",
	}
	for name, sql := range cases {
		t.Run(name, func(t *testing.T) {
			s, path, profile := boundInbox(t)
			reviewAppend(t, s, profile, 0, 0)
			if _, err := s.ReviewClockEvents(ctx, profile, 0); err != nil {
				t.Fatal(err)
			}
			if _, err := s.db.Exec(sql); err != nil {
				t.Fatal(err)
			}
			s.Close()
			s = open(t, path)
			if _, err := s.ReadClockReview(ctx, profile); err == nil {
				t.Fatal("accepted corruption")
			}
		})
	}
}
func TestClockReviewConcurrentAckAndCapacity(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, profile := boundInbox(t)
	other := open(t, path)
	done := make(chan error, 2)
	for _, db := range []*Store{s, other} {
		go func(db *Store) {
			_, err := db.AcknowledgeClockEvents(ctx, profile, ClockAcknowledgement{RequestID: "shared", ExpectedRevision: 0, ThroughCursor: 0})
			done <- err
		}(db)
	}
	for i := 0; i < 2; i++ {
		if err := <-done; err != nil {
			t.Fatal(err)
		}
	}
	var count int
	if err := s.db.QueryRow("SELECT count(*) FROM clock_review_log").Scan(&count); err != nil || count != 1 {
		t.Fatal(count, err)
	}
	tx, err := s.db.Begin()
	if err != nil {
		t.Fatal(err)
	}
	for i := 2; i <= clock.ReviewCapacity; i++ {
		data, _ := json.Marshal(clock.ReviewEntry{Kind: "ack", RequestID: fmt.Sprintf("empty-%d", i)})
		if _, err = tx.Exec("INSERT INTO clock_review_log(sequence,payload) VALUES(?,?)", i, data); err != nil {
			t.Fatal(err)
		}
	}
	if err = tx.Commit(); err != nil {
		t.Fatal(err)
	}
	if _, err = s.AcknowledgeClockEvents(ctx, profile, ClockAcknowledgement{RequestID: "shared", ExpectedRevision: 0, ThroughCursor: 0}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.AcknowledgeClockEvents(ctx, profile, ClockAcknowledgement{RequestID: "new", ExpectedRevision: 0, ThroughCursor: 0}); !errors.Is(err, ErrCapacity) {
		t.Fatal(err)
	}
}
