package clock

import (
	"bytes"
	"context"
	"database/sql"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

const clockInboxCapacity = 4096
const clockInboxBytes = 16 << 20

// InboxCapacity is the maximum number of retained clock inbox pages/events.
const InboxCapacity = clockInboxCapacity

type InboxState struct {
	Profile               string
	Cursor                int64
	Gap                   bool
	LostCount             uint64
	EventCount, PageCount int
}
type EventPage struct {
	Request *k.EventsRequest
	Page    *k.EventsPage
}
type Inbox struct {
	State      InboxState
	Pages      []EventPage
	checkpoint clockHistoryCheckpoint
}

func InitializeInbox(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, `CREATE TABLE clock_inbox(singleton INTEGER PRIMARY KEY CHECK(singleton=1),profile TEXT NOT NULL) STRICT;
 CREATE TABLE clock_event_pages(sequence INTEGER PRIMARY KEY CHECK(sequence>0),after_cursor INTEGER NOT NULL UNIQUE CHECK(after_cursor>=0),next_cursor INTEGER NOT NULL UNIQUE CHECK(next_cursor>after_cursor),request BLOB NOT NULL,page BLOB NOT NULL) STRICT;
 CREATE TABLE clock_inbox_events(cursor INTEGER PRIMARY KEY CHECK(cursor>0),page_sequence INTEGER NOT NULL REFERENCES clock_event_pages(sequence),payload BLOB NOT NULL) STRICT;`)
	return err
}
func CheckInboxSchema(ctx context.Context, tx *sql.Tx) error {
	for _, q := range []string{"SELECT singleton,profile FROM clock_inbox LIMIT 0", "SELECT sequence,after_cursor,next_cursor,request,page FROM clock_event_pages LIMIT 0", "SELECT cursor,page_sequence,payload FROM clock_inbox_events LIMIT 0"} {
		if _, err := tx.ExecContext(ctx, q); err != nil {
			return err
		}
	}
	return nil
}
func canonicalClockProfile(profile string) (string, error) {
	if !filepath.IsAbs(profile) {
		return "", errors.New("clock profile must be absolute")
	}
	result, err := filepath.EvalSymlinks(profile)
	if err != nil {
		return "", err
	}
	info, err := os.Stat(result)
	if err != nil {
		return "", err
	}
	if !info.IsDir() {
		return "", errors.New("clock profile must be a directory")
	}
	return filepath.Clean(result), nil
}
func sameClockProfile(a, b string) bool {
	if runtime.GOOS == "windows" {
		return strings.EqualFold(a, b)
	}
	return a == b
}

// CanonicalProfile validates and normalizes a clock profile path.
func CanonicalProfile(profile string) (string, error) { return canonicalClockProfile(profile) }

// BindInbox binds one native profile journal, independent of loaded worlds.
func BindInbox(ctx context.Context, tx *sql.Tx, path string) (InboxState, error) {
	var saved string
	err := tx.QueryRowContext(ctx, "SELECT profile FROM clock_inbox WHERE singleton=1").Scan(&saved)
	if errors.Is(err, sql.ErrNoRows) {
		var retained int
		if err = tx.QueryRowContext(ctx, "SELECT (SELECT count(*) FROM clock_event_pages)+(SELECT count(*) FROM clock_inbox_events)+(SELECT count(*) FROM clock_review_log)+(SELECT count(*) FROM clock_acknowledgements)").Scan(&retained); err != nil {
			return InboxState{}, err
		}
		if retained != 0 {
			return InboxState{}, errors.New("clock history has no profile binding")
		}
		checkpoint, e := loadClockCheckpoint(ctx, tx)
		if e != nil {
			return InboxState{}, e
		}
		if checkpoint != (clockHistoryCheckpoint{}) {
			return InboxState{}, errors.New("clock checkpoint has no profile binding")
		}
		_, err = tx.ExecContext(ctx, "INSERT INTO clock_inbox(singleton,profile) VALUES(1,?)", path)
	} else if err == nil && !sameClockProfile(saved, path) {
		return InboxState{}, ErrConflict
	}
	if err != nil {
		return InboxState{}, err
	}
	inbox, _, err := loadClockInbox(ctx, tx, path, clockInboxCapacity)
	if err != nil {
		return InboxState{}, err
	}
	return inbox.State, nil
}

// LoadInbox returns every retained page or fails; limit never truncates.
// Ingestion is not processing or acknowledgement of an interruption.
func LoadInbox(ctx context.Context, tx *sql.Tx, path string, limit int) (Inbox, error) {
	if limit < 1 || limit > clockInboxCapacity {
		return Inbox{}, errors.New("invalid clock inbox limit")
	}
	inbox, _, err := loadClockInbox(ctx, tx, path, limit)
	if err != nil {
		return Inbox{}, err
	}
	return inbox, nil
}
func canonicalClockBytes(value proto.Message) ([]byte, error) {
	return proto.MarshalOptions{Deterministic: true}.Marshal(value)
}

// AppendEvents commits a scanned page, surviving events and cursor together.
// Only exact retained request/page replay is a no-op. A changed stale page must
// be refetched at the current cursor; its new context does not rewrite history.
func AppendEvents(ctx context.Context, tx *sql.Tx, path string, request *k.EventsRequest, page *k.EventsPage) (InboxState, bool, error) {
	if err := bridge.ValidateClockEventsPage(page, request); err != nil {
		return InboxState{}, false, err
	}
	request = proto.Clone(request).(*k.EventsRequest)
	page = proto.Clone(page).(*k.EventsPage)
	rb, err := canonicalClockBytes(request)
	if err != nil {
		return InboxState{}, false, err
	}
	pb, err := canonicalClockBytes(page)
	if err != nil {
		return InboxState{}, false, err
	}
	inbox, size, err := loadClockInbox(ctx, tx, path, clockInboxCapacity)
	if err != nil {
		return InboxState{}, false, err
	}
	for _, previous := range inbox.Pages {
		a, _ := canonicalClockBytes(previous.Request)
		b, _ := canonicalClockBytes(previous.Page)
		if bytes.Equal(a, rb) && bytes.Equal(b, pb) {
			return inbox.State, false, nil
		}
	}
	if request.GetAfterCursor() != inbox.State.Cursor {
		return InboxState{}, false, ErrConflict
	}
	if page.GetNextCursor() == inbox.State.Cursor {
		return inbox.State, false, nil
	}
	if inbox.State.PageCount == clockInboxCapacity || inbox.State.EventCount+len(page.Events) > clockInboxCapacity || int64(len(rb))+int64(len(pb)) > clockInboxBytes-size {
		return InboxState{}, false, ErrCapacity
	}
	sequence := inbox.checkpoint.Pages + int64(inbox.State.PageCount) + 1
	if _, err = tx.ExecContext(ctx, "INSERT INTO clock_event_pages(sequence,after_cursor,next_cursor,request,page) VALUES(?,?,?,?,?)", sequence, request.GetAfterCursor(), page.GetNextCursor(), rb, pb); err != nil {
		return InboxState{}, false, err
	}
	for _, event := range page.Events {
		payload, e := canonicalClockBytes(event)
		if e != nil {
			return InboxState{}, false, e
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO clock_inbox_events(cursor,page_sequence,payload) VALUES(?,?,?)", event.GetCursor(), sequence, payload); err != nil {
			return InboxState{}, false, err
		}
	}
	next := inbox.State
	next.Cursor = page.GetNextCursor()
	next.Gap = next.Gap || page.GetGap()
	next.LostCount += page.GetLostCount()
	next.PageCount++
	next.EventCount += len(page.Events)
	return next, true, nil
}

func loadClockInbox(ctx context.Context, tx *sql.Tx, profile string, limit int) (Inbox, int64, error) {
	var inbox Inbox
	if err := tx.QueryRowContext(ctx, "SELECT profile FROM clock_inbox WHERE singleton=1").Scan(&inbox.State.Profile); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			err = ErrNotFound
		}
		return inbox, 0, err
	}
	if !sameClockProfile(inbox.State.Profile, profile) {
		return inbox, 0, ErrConflict
	}
	checkpoint, err := loadClockCheckpoint(ctx, tx)
	if err != nil {
		return inbox, 0, err
	}
	inbox.checkpoint = checkpoint
	inbox.State.Cursor = checkpoint.Cursor
	inbox.State.LostCount = checkpoint.LostCount
	inbox.State.Gap = checkpoint.LostCount != 0
	var pages, events int
	var size, eventBytes int64
	if err := tx.QueryRowContext(ctx, "SELECT count(*),coalesce(sum(length(request)+length(page)),0) FROM clock_event_pages").Scan(&pages, &size); err != nil {
		return inbox, 0, err
	}
	if err := tx.QueryRowContext(ctx, "SELECT count(*),coalesce(sum(length(payload)),0) FROM clock_inbox_events").Scan(&events, &eventBytes); err != nil {
		return inbox, 0, err
	}
	if pages > limit || pages > clockInboxCapacity || events > clockInboxCapacity || size > clockInboxBytes || eventBytes > clockInboxBytes {
		return inbox, 0, ErrCapacity
	}
	rows, err := tx.QueryContext(ctx, "SELECT sequence,after_cursor,next_cursor,request,page FROM clock_event_pages ORDER BY sequence")
	if err != nil {
		return inbox, 0, err
	}
	for rows.Next() {
		var seq, after, next int64
		var rb, pb []byte
		if err = rows.Scan(&seq, &after, &next, &rb, &pb); err != nil {
			rows.Close()
			return inbox, 0, err
		}
		request, page := &k.EventsRequest{}, &k.EventsPage{}
		if err = (proto.UnmarshalOptions{RecursionLimit: 64}).Unmarshal(rb, request); err == nil {
			err = (proto.UnmarshalOptions{RecursionLimit: 64}).Unmarshal(pb, page)
		}
		if err == nil {
			err = bridge.ValidateClockEventsPage(page, request)
		}
		if err != nil {
			rows.Close()
			return inbox, 0, err
		}
		cr, _ := canonicalClockBytes(request)
		cp, _ := canonicalClockBytes(page)
		if !bytes.Equal(rb, cr) || !bytes.Equal(pb, cp) || seq != checkpoint.Pages+int64(len(inbox.Pages)+1) || after != inbox.State.Cursor || after != request.GetAfterCursor() || next != page.GetNextCursor() || next <= after {
			rows.Close()
			return inbox, 0, errors.New("invalid clock inbox replay")
		}
		inbox.Pages = append(inbox.Pages, EventPage{Request: request, Page: page})
		inbox.State.Cursor = next
		inbox.State.Gap = inbox.State.Gap || page.GetGap()
		inbox.State.LostCount += page.GetLostCount()
		inbox.State.EventCount += len(page.Events)
	}
	err = rows.Err()
	rows.Close()
	if err != nil {
		return inbox, 0, err
	}
	inbox.State.PageCount = len(inbox.Pages)
	if inbox.State.EventCount != events || inbox.State.PageCount != pages {
		return inbox, 0, errors.New("clock inbox count mismatch")
	}
	rows, err = tx.QueryContext(ctx, "SELECT cursor,page_sequence,payload FROM clock_inbox_events ORDER BY cursor")
	if err != nil {
		return inbox, 0, err
	}
	defer rows.Close()
	for i, page := range inbox.Pages {
		for _, event := range page.Page.Events {
			if !rows.Next() {
				return inbox, 0, errors.New("missing clock inbox event")
			}
			var cursor, sequence int64
			var payload []byte
			if err = rows.Scan(&cursor, &sequence, &payload); err != nil {
				return inbox, 0, err
			}
			expected, _ := canonicalClockBytes(event)
			if cursor != event.GetCursor() || sequence != checkpoint.Pages+int64(i+1) || !bytes.Equal(payload, expected) {
				return inbox, 0, fmt.Errorf("invalid clock inbox event %d", cursor)
			}
		}
	}
	if rows.Next() {
		return inbox, 0, errors.New("extra clock inbox event")
	}
	if err = rows.Err(); err != nil {
		return inbox, 0, err
	}
	return inbox, size, nil
}
