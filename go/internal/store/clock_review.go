package store

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"math"
)

const clockReviewCapacity = 4096

type clockReviewHead struct {
	Revision     uint64
	Reviewed     int64
	Acknowledged int64
}
type clockReviewEntry struct {
	Kind             string
	InboxCursor      int64
	ExpectedRevision uint64
	ThroughCursor    int64
	RequestID        string
}
type clockReviewReplay struct {
	head    clockReviewHead
	entries []clockReviewEntry
	heads   []clockReviewHead
	inbox   ClockInbox
}

func initializeClockReview(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE clock_review(singleton INTEGER PRIMARY KEY CHECK(singleton=1),payload BLOB NOT NULL) STRICT;
 CREATE TABLE clock_review_log(sequence INTEGER PRIMARY KEY CHECK(sequence>0),payload BLOB NOT NULL) STRICT;`)
	if err != nil {
		return err
	}
	payload, _ := json.Marshal(clockReviewHead{})
	_, err = tx.ExecContext(ctx, "INSERT INTO clock_review(singleton,payload) VALUES(1,?)", payload)
	return err
}
func checkClockReviewSchema(ctx context.Context, tx *sql.Tx) error {
	for _, q := range []string{"SELECT singleton,payload FROM clock_review LIMIT 0", "SELECT sequence,payload FROM clock_review_log LIMIT 0"} {
		if _, err := tx.ExecContext(ctx, q); err != nil {
			return err
		}
	}
	return nil
}
func clockReviewDecode(data []byte, value any) error {
	if len(data) > 4096 {
		return ErrCapacity
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(value); err != nil {
		return err
	}
	canonical, err := json.Marshal(value)
	if err != nil {
		return err
	}
	if !bytes.Equal(canonical, data) {
		return errors.New("noncanonical clock review record")
	}
	return nil
}
func clockReviewBoundary(inbox ClockInbox, cursor int64) bool {
	if cursor == 0 {
		return true
	}
	for _, p := range inbox.Pages {
		if p.Page.GetNextCursor() == cursor {
			return true
		}
	}
	return false
}
func clockEventInterrupts(event *k.Event) bool {
	switch e := event.Event.(type) {
	case *k.Event_Started, *k.Event_SpeedChanged, *k.Event_HostilesCleared, *k.Event_ForcePauseCleared:
		return false
	case *k.Event_Stopped:
		return e.Stopped.GetReason() != k.StopReason_STOP_REASON_TICK_BUDGET && e.Stopped.GetReason() != k.StopReason_STOP_REASON_REQUESTED_PAUSE
	default:
		return true
	}
}
func clockReviewState(head clockReviewHead, inbox ClockInbox, cursor int64) ClockReviewState {
	state := ClockReviewState{Revision: head.Revision, InboxCursor: cursor, ReviewedCursor: head.Reviewed, AcknowledgedCursor: head.Acknowledged, Holds: []ClockHold{}}
	for _, p := range inbox.Pages {
		if p.Page.GetNextCursor() > head.Reviewed {
			break
		}
		if p.Page.GetNextCursor() <= head.Acknowledged {
			continue
		}
		if p.Page.GetGap() {
			state.Holds = append(state.Holds, ClockHold{Kind: ClockGapHold, FromCursor: p.Request.GetAfterCursor() + 1, ThroughCursor: p.Page.GetNextCursor()})
		}
		for _, event := range p.Page.Events {
			if event.GetCursor() > head.Acknowledged && clockEventInterrupts(event) {
				state.Holds = append(state.Holds, ClockHold{Kind: ClockInterruptionHold, FromCursor: event.GetCursor(), ThroughCursor: event.GetCursor()})
			}
		}
	}
	return state
}
func clockReviewApply(head clockReviewHead, entry clockReviewEntry, inbox ClockInbox) (clockReviewHead, error) {
	if entry.ExpectedRevision != head.Revision || !clockReviewBoundary(inbox, entry.InboxCursor) || entry.InboxCursor < head.Reviewed {
		return head, errors.New("invalid clock review revision or captured cursor")
	}
	next := head
	switch entry.Kind {
	case "review":
		if entry.RequestID != "" || entry.ThroughCursor != entry.InboxCursor || entry.ThroughCursor <= head.Reviewed {
			return head, errors.New("invalid clock review provenance")
		}
		next.Reviewed = entry.ThroughCursor
	case "ack":
		if submissionID(entry.RequestID) != nil || entry.ThroughCursor != head.Reviewed {
			return head, errors.New("invalid clock acknowledgement provenance")
		}
		next.Acknowledged = entry.ThroughCursor
	default:
		return head, errors.New("unknown clock review operation")
	}
	if next != head {
		if head.Revision == math.MaxUint64 {
			return head, ErrCapacity
		}
		next.Revision++
	}
	return next, nil
}
func loadClockReview(ctx context.Context, tx *sql.Tx, profile string) (clockReviewReplay, error) {
	var replay clockReviewReplay
	inbox, _, err := loadClockInbox(ctx, tx, profile, clockInboxCapacity)
	if err != nil {
		return replay, err
	}
	replay.inbox = inbox
	var count int
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM clock_review").Scan(&count); err != nil {
		return replay, err
	}
	if count != 1 {
		return replay, errors.New("missing clock review head")
	}
	var payload []byte
	if err = tx.QueryRowContext(ctx, "SELECT payload FROM clock_review WHERE singleton=1").Scan(&payload); err != nil {
		return replay, err
	}
	var saved clockReviewHead
	if err = clockReviewDecode(payload, &saved); err != nil {
		return replay, err
	}
	if err = tx.QueryRowContext(ctx, "SELECT count(*) FROM clock_review_log").Scan(&count); err != nil {
		return replay, err
	}
	if count > clockReviewCapacity {
		return replay, ErrCapacity
	}
	rows, err := tx.QueryContext(ctx, "SELECT sequence,payload FROM clock_review_log ORDER BY sequence")
	if err != nil {
		return replay, err
	}
	defer rows.Close()
	seen := make(map[string]bool)
	var previousInbox int64
	for rows.Next() {
		var sequence int
		var data []byte
		if err = rows.Scan(&sequence, &data); err != nil {
			return replay, err
		}
		var entry clockReviewEntry
		if err = clockReviewDecode(data, &entry); err != nil {
			return replay, err
		}
		if sequence != len(replay.entries)+1 || entry.InboxCursor < previousInbox {
			return replay, errors.New("clock review log sequence or inbox regression")
		}
		if entry.Kind == "ack" {
			if seen[entry.RequestID] {
				return replay, errors.New("duplicate clock acknowledgement")
			}
			seen[entry.RequestID] = true
		}
		replay.head, err = clockReviewApply(replay.head, entry, inbox)
		if err != nil {
			return replay, err
		}
		replay.entries = append(replay.entries, entry)
		replay.heads = append(replay.heads, replay.head)
		previousInbox = entry.InboxCursor
	}
	if err = rows.Err(); err != nil {
		return replay, err
	}
	if replay.head != saved || len(replay.entries) != count {
		return replay, errors.New("clock review head lacks matching provenance")
	}
	return replay, nil
}
func (s *Store) ReadClockReview(ctx context.Context, profile string) (ClockReviewState, error) {
	path, err := canonicalClockProfile(profile)
	if err != nil {
		return ClockReviewState{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockReviewState{}, err
	}
	defer tx.Rollback()
	replay, err := loadClockReview(ctx, tx, path)
	if err != nil {
		return ClockReviewState{}, err
	}
	result := clockReviewState(replay.head, replay.inbox, replay.inbox.State.Cursor)
	return result, tx.Commit()
}
func saveClockReview(ctx context.Context, tx *sql.Tx, replay clockReviewReplay, entry clockReviewEntry) (ClockReviewState, error) {
	if len(replay.entries) >= clockReviewCapacity {
		return ClockReviewState{}, ErrCapacity
	}
	next, err := clockReviewApply(replay.head, entry, replay.inbox)
	if err != nil {
		return ClockReviewState{}, err
	}
	data, _ := json.Marshal(entry)
	head, _ := json.Marshal(next)
	if _, err = tx.ExecContext(ctx, "INSERT INTO clock_review_log(sequence,payload) VALUES(?,?)", len(replay.entries)+1, data); err != nil {
		return ClockReviewState{}, err
	}
	if _, err = tx.ExecContext(ctx, "UPDATE clock_review SET payload=? WHERE singleton=1", head); err != nil {
		return ClockReviewState{}, err
	}
	return clockReviewState(next, replay.inbox, entry.InboxCursor), nil
}
func (s *Store) ReviewClockEvents(ctx context.Context, profile string, expectedRevision uint64) (ClockReviewState, error) {
	path, err := canonicalClockProfile(profile)
	if err != nil {
		return ClockReviewState{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockReviewState{}, err
	}
	defer tx.Rollback()
	replay, err := loadClockReview(ctx, tx, path)
	if err != nil {
		return ClockReviewState{}, err
	}
	if replay.head.Revision != expectedRevision {
		return ClockReviewState{}, ErrConflict
	}
	result := clockReviewState(replay.head, replay.inbox, replay.inbox.State.Cursor)
	if replay.head.Reviewed != replay.inbox.State.Cursor {
		result, err = saveClockReview(ctx, tx, replay, clockReviewEntry{Kind: "review", InboxCursor: replay.inbox.State.Cursor, ExpectedRevision: expectedRevision, ThroughCursor: replay.inbox.State.Cursor})
	}
	if err != nil {
		return ClockReviewState{}, err
	}
	return result, tx.Commit()
}
func (s *Store) AcknowledgeClockEvents(ctx context.Context, profile string, ack ClockAcknowledgement) (ClockReviewState, error) {
	if err := submissionID(ack.RequestID); err != nil {
		return ClockReviewState{}, err
	}
	path, err := canonicalClockProfile(profile)
	if err != nil {
		return ClockReviewState{}, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return ClockReviewState{}, err
	}
	defer tx.Rollback()
	replay, err := loadClockReview(ctx, tx, path)
	if err != nil {
		return ClockReviewState{}, err
	}
	for i, entry := range replay.entries {
		if entry.Kind == "ack" && entry.RequestID == ack.RequestID {
			if entry.ExpectedRevision != ack.ExpectedRevision || entry.ThroughCursor != ack.ThroughCursor {
				return ClockReviewState{}, ErrConflict
			}
			return clockReviewState(replay.heads[i], replay.inbox, entry.InboxCursor), tx.Commit()
		}
	}
	if ack.ExpectedRevision != replay.head.Revision || ack.ThroughCursor != replay.head.Reviewed {
		return ClockReviewState{}, ErrConflict
	}
	result, err := saveClockReview(ctx, tx, replay, clockReviewEntry{Kind: "ack", InboxCursor: replay.inbox.State.Cursor, ExpectedRevision: ack.ExpectedRevision, ThroughCursor: ack.ThroughCursor, RequestID: ack.RequestID})
	if err != nil {
		return ClockReviewState{}, err
	}
	return result, tx.Commit()
}
