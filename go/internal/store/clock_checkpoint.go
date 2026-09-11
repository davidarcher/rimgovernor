package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"math"
)

// The checkpoint replaces only validated history in one transaction. Public
// cursors and review revisions never reset when the active log is compacted.
type clockHistoryCheckpoint struct {
	Pages, Cursor     int64
	LostCount         uint64
	Review            clockReviewHead
	ReviewInboxCursor int64
}

type clockArchivedAcknowledgement struct {
	Request     ClockAcknowledgement
	Head        clockReviewHead
	InboxCursor int64
}

func initializeClockCheckpoint(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE clock_history_checkpoint(singleton INTEGER PRIMARY KEY CHECK(singleton=1),payload BLOB NOT NULL) STRICT;
CREATE TABLE clock_acknowledgements(request_id TEXT PRIMARY KEY,payload BLOB NOT NULL) STRICT;`)
	if err != nil {
		return err
	}
	data, _ := json.Marshal(clockHistoryCheckpoint{})
	_, err = tx.ExecContext(ctx, "INSERT INTO clock_history_checkpoint(singleton,payload) VALUES(1,?)", data)
	return err
}

func checkClockCheckpointSchema(ctx context.Context, tx *sql.Tx) error {
	if _, err := tx.ExecContext(ctx, "SELECT request_id,payload FROM clock_acknowledgements LIMIT 0"); err != nil {
		return err
	}
	_, err := loadClockCheckpoint(ctx, tx)
	return err
}

func loadClockCheckpoint(ctx context.Context, tx *sql.Tx) (clockHistoryCheckpoint, error) {
	var checkpoint clockHistoryCheckpoint
	var data []byte
	if err := tx.QueryRowContext(ctx, "SELECT payload FROM clock_history_checkpoint WHERE singleton=1").Scan(&data); err != nil {
		return checkpoint, err
	}
	if err := clockReviewDecode(data, &checkpoint); err != nil {
		return checkpoint, err
	}
	r := checkpoint.Review
	if checkpoint.Pages < 0 || checkpoint.Cursor < checkpoint.Pages || checkpoint.Pages > math.MaxInt64-clockInboxCapacity ||
		(checkpoint.Pages == 0 && (checkpoint.Cursor != 0 || checkpoint.LostCount != 0)) ||
		checkpoint.LostCount > uint64(checkpoint.Cursor) || r.Acknowledged < 0 || r.Reviewed < r.Acknowledged ||
		r.Reviewed < checkpoint.Cursor || checkpoint.ReviewInboxCursor < r.Reviewed ||
		(r.Revision == 0 && (r.Reviewed != 0 || r.Acknowledged != 0)) {
		return checkpoint, errors.New("invalid clock history checkpoint")
	}
	return checkpoint, nil
}

func loadClockAcknowledgement(ctx context.Context, tx *sql.Tx, ack ClockAcknowledgement, replay clockReviewReplay) (ClockReviewState, bool, error) {
	var data []byte
	err := tx.QueryRowContext(ctx, "SELECT payload FROM clock_acknowledgements WHERE request_id=?", ack.RequestID).Scan(&data)
	if errors.Is(err, sql.ErrNoRows) {
		return ClockReviewState{}, false, nil
	}
	if err != nil {
		return ClockReviewState{}, false, err
	}
	var archived clockArchivedAcknowledgement
	if err = clockReviewDecode(data, &archived); err != nil {
		return ClockReviewState{}, false, err
	}
	r := archived.Request
	h := archived.Head
	if r.RequestID != ack.RequestID || submissionID(r.RequestID) != nil || r.ThroughCursor < 0 ||
		h.Reviewed != r.ThroughCursor || h.Acknowledged != r.ThroughCursor || archived.InboxCursor < h.Reviewed ||
		archived.InboxCursor > replay.inbox.checkpoint.ReviewInboxCursor || h.Revision > replay.inbox.checkpoint.Review.Revision ||
		(h.Revision != r.ExpectedRevision && (r.ExpectedRevision == math.MaxUint64 || h.Revision != r.ExpectedRevision+1)) {
		return ClockReviewState{}, false, errors.New("invalid archived clock acknowledgement")
	}
	if r != ack {
		return ClockReviewState{}, false, ErrConflict
	}
	// An acknowledgement covers every hold through its reviewed cursor. Preserve
	// its historical captured cursor instead of applying it to today's evidence.
	return ClockReviewState{Revision: h.Revision, InboxCursor: archived.InboxCursor, ReviewedCursor: h.Reviewed, AcknowledgedCursor: h.Acknowledged, Holds: []ClockHold{}}, true, nil
}

type ClockHistoryCompaction struct {
	RemovedPages, RemovedEvents, RemovedReviews int
	Cursor                                      int64
}

// CompactClockHistory keeps a recent page tail and every unreviewed or
// unacknowledged interruption/gap. Acknowledgement replies move to an indexed
// archive; routine polling never loads that growing archive into memory.
func (s *Store) CompactClockHistory(ctx context.Context, profile string) (ClockHistoryCompaction, error) {
	var out ClockHistoryCompaction
	path, err := canonicalClockProfile(profile)
	if err != nil {
		return out, err
	}
	tx, err := s.begin(ctx)
	if err != nil {
		return out, err
	}
	defer tx.Rollback()
	replay, err := loadClockReview(ctx, tx, path)
	if err != nil {
		return out, err
	}
	out.Cursor = replay.inbox.State.Cursor
	var size int64
	if err = tx.QueryRowContext(ctx, "SELECT coalesce(sum(length(request)+length(page)),0) FROM clock_event_pages").Scan(&size); err != nil {
		return out, err
	}
	if len(replay.entries) < 128 && len(replay.inbox.Pages) < 128 && replay.inbox.State.EventCount < 256 && size < clockInboxBytes/2 {
		return out, tx.Commit()
	}
	checkpoint := replay.inbox.checkpoint
	// Compact a contiguous prefix so missing native cursor positions retain their
	// original loss attribution and ingestion still checks an exact next cursor.
	for i, page := range replay.inbox.Pages {
		if i >= len(replay.inbox.Pages)-8 || page.Page.GetNextCursor() > replay.head.Reviewed {
			break
		}
		if page.Page.GetNextCursor() > replay.head.Acknowledged {
			if page.Page.GetGap() {
				break
			}
			held := false
			for _, event := range page.Page.Events {
				held = held || clockEventInterrupts(event)
			}
			if held {
				break
			}
		}
		checkpoint.Pages++
		checkpoint.Cursor = page.Page.GetNextCursor()
		checkpoint.LostCount += page.Page.GetLostCount()
		out.RemovedPages++
		out.RemovedEvents += len(page.Page.Events)
	}
	if out.RemovedPages == 0 && len(replay.entries) == 0 {
		return out, tx.Commit()
	}
	for i, entry := range replay.entries {
		if entry.Kind != "ack" {
			continue
		}
		archived := clockArchivedAcknowledgement{Request: ClockAcknowledgement{RequestID: entry.RequestID, ExpectedRevision: entry.ExpectedRevision, ThroughCursor: entry.ThroughCursor}, Head: replay.heads[i], InboxCursor: entry.InboxCursor}
		data, _ := json.Marshal(archived)
		if _, err = tx.ExecContext(ctx, "INSERT INTO clock_acknowledgements(request_id,payload) VALUES(?,?)", entry.RequestID, data); err != nil {
			return ClockHistoryCompaction{}, err
		}
	}
	checkpoint.Review = replay.head
	checkpoint.ReviewInboxCursor = replay.inbox.State.Cursor
	data, _ := json.Marshal(checkpoint)
	if _, err = tx.ExecContext(ctx, "UPDATE clock_history_checkpoint SET payload=? WHERE singleton=1", data); err != nil {
		return ClockHistoryCompaction{}, err
	}
	if _, err = tx.ExecContext(ctx, "DELETE FROM clock_inbox_events WHERE page_sequence<=?", checkpoint.Pages); err != nil {
		return ClockHistoryCompaction{}, err
	}
	if _, err = tx.ExecContext(ctx, "DELETE FROM clock_event_pages WHERE sequence<=?", checkpoint.Pages); err != nil {
		return ClockHistoryCompaction{}, err
	}
	if _, err = tx.ExecContext(ctx, "DELETE FROM clock_review_log"); err != nil {
		return ClockHistoryCompaction{}, err
	}
	out.RemovedReviews = len(replay.entries)
	if _, err = loadClockReview(ctx, tx, path); err != nil {
		return ClockHistoryCompaction{}, err
	}
	return out, tx.Commit()
}
