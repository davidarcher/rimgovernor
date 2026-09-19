package clock

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"math"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// ReviewCapacity is a var (not const) so capacity-boundary tests can shrink it
// and exercise wraparound without paying the cost of filling a
// production-scale ring buffer.
var ReviewCapacity = 4096

type ReviewEntry = clockReviewEntry

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
	inbox   Inbox
}

func InitializeReview(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE clock_review(singleton INTEGER PRIMARY KEY CHECK(singleton=1),payload BLOB NOT NULL) STRICT;
 CREATE TABLE clock_review_log(sequence INTEGER PRIMARY KEY CHECK(sequence>0),payload BLOB NOT NULL) STRICT;`)
	if err != nil {
		return err
	}
	payload, _ := json.Marshal(clockReviewHead{})
	_, err = tx.ExecContext(ctx, "INSERT INTO clock_review(singleton,payload) VALUES(1,?)", payload)
	return err
}
func CheckReviewSchema(ctx context.Context, tx *sql.Tx) error {
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
func clockReviewBoundary(inbox Inbox, cursor int64) bool {
	if cursor == inbox.checkpoint.Cursor {
		return true
	}
	for _, p := range inbox.Pages {
		if p.Page.GetNextCursor() == cursor {
			return true
		}
	}
	return false
}

// BenignStop reports whether a stop reason is the controller's own doing (a
// budget, a requested pause or a latched watch) rather than an interruption
// that needs review before time may resume.
func BenignStop(reason k.StopReason) bool {
	switch reason {
	case k.StopReason_STOP_REASON_TICK_BUDGET, k.StopReason_STOP_REASON_REQUESTED_PAUSE, k.StopReason_STOP_REASON_WATCH_LATCHED:
		return true
	}
	return false
}

// EventInterrupts classifies one journal event. Operation outcomes,
// authority changes and observation invalidations are typed facts the
// controller reacts to, not holds; so is a game alert (a High-priority
// alert such as "Need colonist beds" is routine planning evidence, and the
// native supervisor never stops play for one; the stop tier is danger, a
// coupled order and player input, #244). It
// is shared with the poll loop so both classifications cannot drift.
func EventInterrupts(event *k.Event) bool {
	switch e := event.Event.(type) {
	case *k.Event_Started, *k.Event_SpeedChanged, *k.Event_HostilesCleared, *k.Event_ForcePauseCleared, *k.Event_OperationOutcome, *k.Event_AuthorityChanged, *k.Event_ObservationInvalidated, *k.Event_Alert:
		return false
	case *k.Event_Stopped:
		return !BenignStop(e.Stopped.GetReason())
	default:
		return true
	}
}
func clockEventInterrupts(event *k.Event) bool { return EventInterrupts(event) }
func clockReviewState(head clockReviewHead, inbox Inbox, cursor int64) ReviewState {
	state := ReviewState{Revision: head.Revision, InboxCursor: cursor, ReviewedCursor: head.Reviewed, AcknowledgedCursor: head.Acknowledged, Holds: []Hold{}}
	for _, p := range inbox.Pages {
		if p.Page.GetNextCursor() > head.Reviewed {
			break
		}
		if p.Page.GetNextCursor() <= head.Acknowledged {
			continue
		}
		if p.Page.GetGap() {
			state.Holds = append(state.Holds, Hold{Kind: GapHold, FromCursor: p.Request.GetAfterCursor() + 1, ThroughCursor: p.Page.GetNextCursor()})
		}
		for _, event := range p.Page.Events {
			if event.GetCursor() > head.Acknowledged && clockEventInterrupts(event) {
				state.Holds = append(state.Holds, Hold{Kind: InterruptionHold, FromCursor: event.GetCursor(), ThroughCursor: event.GetCursor()})
			}
		}
	}
	return state
}
func clockReviewApply(head clockReviewHead, entry clockReviewEntry, inbox Inbox) (clockReviewHead, error) {
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
	replay.head = inbox.checkpoint.Review
	if !clockReviewBoundary(inbox, replay.head.Reviewed) || !clockReviewBoundary(inbox, inbox.checkpoint.ReviewInboxCursor) {
		return replay, errors.New("clock checkpoint lacks inbox boundary")
	}
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
	if count > ReviewCapacity {
		return replay, ErrCapacity
	}
	rows, err := tx.QueryContext(ctx, "SELECT sequence,payload FROM clock_review_log ORDER BY sequence")
	if err != nil {
		return replay, err
	}
	defer rows.Close()
	seen := make(map[string]bool)
	previousInbox := inbox.checkpoint.ReviewInboxCursor
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
func ReadReview(ctx context.Context, tx *sql.Tx, profile string) (ReviewState, error) {
	path, err := canonicalClockProfile(profile)
	if err != nil {
		return ReviewState{}, err
	}
	replay, err := loadClockReview(ctx, tx, path)
	if err != nil {
		return ReviewState{}, err
	}
	return clockReviewState(replay.head, replay.inbox, replay.inbox.State.Cursor), nil
}
func saveClockReview(ctx context.Context, tx *sql.Tx, replay clockReviewReplay, entry clockReviewEntry) (ReviewState, error) {
	if len(replay.entries) >= ReviewCapacity {
		return ReviewState{}, ErrCapacity
	}
	next, err := clockReviewApply(replay.head, entry, replay.inbox)
	if err != nil {
		return ReviewState{}, err
	}
	data, _ := json.Marshal(entry)
	head, _ := json.Marshal(next)
	if _, err = tx.ExecContext(ctx, "INSERT INTO clock_review_log(sequence,payload) VALUES(?,?)", len(replay.entries)+1, data); err != nil {
		return ReviewState{}, err
	}
	if _, err = tx.ExecContext(ctx, "UPDATE clock_review SET payload=? WHERE singleton=1", head); err != nil {
		return ReviewState{}, err
	}
	return clockReviewState(next, replay.inbox, entry.InboxCursor), nil
}
func ReviewEvents(ctx context.Context, tx *sql.Tx, profile string, expectedRevision uint64) (ReviewState, error) {
	path, err := canonicalClockProfile(profile)
	if err != nil {
		return ReviewState{}, err
	}
	replay, err := loadClockReview(ctx, tx, path)
	if err != nil {
		return ReviewState{}, err
	}
	if replay.head.Revision != expectedRevision {
		return ReviewState{}, ErrConflict
	}
	result := clockReviewState(replay.head, replay.inbox, replay.inbox.State.Cursor)
	if replay.head.Reviewed != replay.inbox.State.Cursor {
		result, err = saveClockReview(ctx, tx, replay, clockReviewEntry{Kind: "review", InboxCursor: replay.inbox.State.Cursor, ExpectedRevision: expectedRevision, ThroughCursor: replay.inbox.State.Cursor})
	}
	if err != nil {
		return ReviewState{}, err
	}
	return result, nil
}
func AcknowledgeEvents(ctx context.Context, tx *sql.Tx, profile string, ack Acknowledgement) (ReviewState, error) {
	if err := submissionID(ack.RequestID); err != nil {
		return ReviewState{}, err
	}
	path, err := canonicalClockProfile(profile)
	if err != nil {
		return ReviewState{}, err
	}
	replay, err := loadClockReview(ctx, tx, path)
	if err != nil {
		return ReviewState{}, err
	}
	archived, found, err := loadClockAcknowledgement(ctx, tx, ack, replay)
	if err != nil {
		return ReviewState{}, err
	}
	if found {
		return archived, nil
	}
	for i, entry := range replay.entries {
		if entry.Kind == "ack" && entry.RequestID == ack.RequestID {
			if entry.ExpectedRevision != ack.ExpectedRevision || entry.ThroughCursor != ack.ThroughCursor {
				return ReviewState{}, ErrConflict
			}
			return clockReviewState(replay.heads[i], replay.inbox, entry.InboxCursor), nil
		}
	}
	if ack.ExpectedRevision != replay.head.Revision || ack.ThroughCursor != replay.head.Reviewed {
		return ReviewState{}, ErrConflict
	}
	return saveClockReview(ctx, tx, replay, clockReviewEntry{Kind: "ack", InboxCursor: replay.inbox.State.Cursor, ExpectedRevision: ack.ExpectedRevision, ThroughCursor: ack.ThroughCursor, RequestID: ack.RequestID})
}
