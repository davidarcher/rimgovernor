package store

import (
	"context"
	"errors"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"path/filepath"
	"strings"
	"testing"
)

func inboxPage(after int64, count int, lost uint64) (*k.EventsRequest, *k.EventsPage) {
	id := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	ctx := &c.ObservationContext{Identity: id, Tick: proto.Int64(10), NativeGeneration: proto.Uint64(2)}
	request := &k.EventsRequest{Identity: proto.Clone(id).(*c.Identity), AfterCursor: proto.Int64(after), Limit: proto.Uint32(128)}
	next := after + int64(count) + int64(lost)
	page := &k.EventsPage{Context: ctx, NewestCursor: proto.Int64(next), NextCursor: proto.Int64(next), Gap: proto.Bool(lost > 0), LostCount: proto.Uint64(lost)}
	for i := 0; i < count; i++ {
		page.Events = append(page.Events, &k.Event{Cursor: proto.Int64(after + int64(i) + 1), Owner: &k.EpochOwner{ControllerSessionId: proto.String("session"), Epoch: proto.Int64(1)}, Context: proto.Clone(ctx).(*c.ObservationContext), ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_SpeedChanged{SpeedChanged: &k.SpeedChanged{Speed: k.Speed_SPEED_NORMAL.Enum()}}})
	}
	return request, page
}
func boundInbox(t *testing.T) (*Store, string, string) {
	t.Helper()
	path := filepath.Join(t.TempDir(), "inbox.db")
	s := open(t, path)
	profile := t.TempDir()
	if _, err := s.BindClockInbox(context.Background(), profile); err != nil {
		t.Fatal(err)
	}
	return s, path, profile
}
func TestClockInboxReplayWorldLossAndIsolation(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, profile := boundInbox(t)
	r, p := inboxPage(0, 1, 2)
	p.Events[0].Context.Identity.LoadToken = proto.String("old-world")
	state, inserted, err := s.AppendClockEvents(ctx, profile, r, p)
	if err != nil || !inserted || state.Cursor != 3 || state.LostCount != 2 {
		t.Fatal(state, inserted, err)
	}
	if _, inserted, err = s.AppendClockEvents(ctx, profile, r, p); err != nil || inserted {
		t.Fatal(inserted, err)
	}
	changed := proto.Clone(p).(*k.EventsPage)
	changed.Context.Tick = proto.Int64(11)
	if _, _, err = s.AppendClockEvents(ctx, profile, r, changed); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	r2, p2 := inboxPage(3, 0, 2)
	r2.Identity.LoadToken = proto.String("new-world")
	p2.Context.Identity.LoadToken = proto.String("new-world")
	if _, _, err = s.AppendClockEvents(ctx, profile, r2, p2); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	inbox, err := s.LoadClockInbox(ctx, profile, 2)
	if err != nil || inbox.State.Cursor != 5 || inbox.State.LostCount != 4 || inbox.State.EventCount != 1 || !inbox.State.Gap {
		t.Fatal(inbox, err)
	}
	if inbox.Pages[0].Page.Events[0].Context.Identity.GetLoadToken() != "old-world" {
		t.Fatal("event world lost")
	}
	inbox.Pages[0].Page.Events[0].Detail = proto.String("modified")
	again, err := s.LoadClockInbox(ctx, profile, 2)
	if err != nil || again.Pages[0].Page.Events[0].Detail != nil {
		t.Fatal("alias", err)
	}
	if _, err = s.LoadClockInbox(ctx, profile, 1); !errors.Is(err, ErrCapacity) {
		t.Fatal(err)
	}
	if _, err = s.BindClockInbox(ctx, t.TempDir()); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err = s.BindClockInbox(ctx, "relative"); err == nil {
		t.Fatal("relative profile")
	}
	emptyR, emptyP := inboxPage(5, 0, 0)
	if st, added, err := s.AppendClockEvents(ctx, profile, emptyR, emptyP); err != nil || added || st.PageCount != 2 {
		t.Fatal(st, added, err)
	}
}
func TestClockInboxAtomicRollbackAndCorruption(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	t.Run("transaction rollback", func(t *testing.T) {
		s, _, profile := boundInbox(t)
		_, err := s.db.Exec(`CREATE TRIGGER reject_clock_event BEFORE INSERT ON clock_inbox_events BEGIN SELECT RAISE(ABORT,'fixture'); END`)
		if err != nil {
			t.Fatal(err)
		}
		r, p := inboxPage(0, 1, 0)
		if _, _, err = s.AppendClockEvents(ctx, profile, r, p); err == nil {
			t.Fatal("expected failure")
		}
		st, err := s.ReadClockInbox(ctx, profile)
		if err != nil || st.Cursor != 0 || st.PageCount != 0 {
			t.Fatal(st, err)
		}
	})
	for name, sql := range map[string]string{
		"event missing": "DELETE FROM clock_inbox_events", "event corrupt": "UPDATE clock_inbox_events SET payload=x'00'", "page corrupt": "UPDATE clock_event_pages SET page=x'00'", "request corrupt": "UPDATE clock_event_pages SET request=x'00'", "cursor corrupt": "UPDATE clock_event_pages SET next_cursor=2",
	} {
		t.Run(name, func(t *testing.T) {
			s, path, profile := boundInbox(t)
			r, p := inboxPage(0, 1, 0)
			if _, _, err := s.AppendClockEvents(ctx, profile, r, p); err != nil {
				t.Fatal(err)
			}
			if _, err := s.db.Exec(sql); err != nil {
				t.Fatal(err)
			}
			s.Close()
			s = open(t, path)
			if _, err := s.ReadClockInbox(ctx, profile); err == nil {
				t.Fatal("accepted corruption")
			}
		})
	}
}
func TestClockInboxEventAndByteCapacity(t *testing.T) {
	t.Parallel()
	for _, large := range []bool{false, true} {
		t.Run(map[bool]string{false: "events", true: "bytes"}[large], func(t *testing.T) {
			ctx := context.Background()
			s, _, profile := boundInbox(t)
			cursor := int64(0)
			var before ClockInboxState
			for i := 0; i < 33; i++ {
				r, p := inboxPage(cursor, 128, 0)
				if large {
					for _, e := range p.Events {
						e.Detail = proto.String(strings.Repeat("d", 4096))
						e.Event = &k.Event_Notification{Notification: &k.Notification{Source: &k.Notification_Message{Message: &k.TransientMessage{Id: proto.String("message"), Text: proto.String(strings.Repeat("m", 4096))}}}}
					}
				}
				st, added, err := s.AppendClockEvents(ctx, profile, r, p)
				if errors.Is(err, ErrCapacity) {
					actual, e := s.ReadClockInbox(ctx, profile)
					if e != nil || actual != before {
						t.Fatal("partial capacity append", actual, before, e)
					}
					if !large && actual.EventCount != 4096 {
						t.Fatal(actual)
					}
					return
				}
				if err != nil || !added {
					t.Fatal(err)
				}
				before = st
				cursor = st.Cursor
			}
			t.Fatal("capacity not enforced")
		})
	}
}
func TestClockInboxPageCapacity(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, profile := boundInbox(t)
	tx, err := s.begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < clockInboxCapacity; i++ {
		r, p := inboxPage(int64(i), 0, 1)
		rb, _ := canonicalClockBytes(r)
		pb, _ := canonicalClockBytes(p)
		if _, err = tx.ExecContext(ctx, "INSERT INTO clock_event_pages(sequence,after_cursor,next_cursor,request,page) VALUES(?,?,?,?,?)", i+1, i, i+1, rb, pb); err != nil {
			t.Fatal(err)
		}
	}
	if err = tx.Commit(); err != nil {
		t.Fatal(err)
	}
	r, p := inboxPage(clockInboxCapacity, 0, 1)
	if _, _, err = s.AppendClockEvents(ctx, profile, r, p); !errors.Is(err, ErrCapacity) {
		t.Fatal(err)
	}
	st, err := s.ReadClockInbox(ctx, profile)
	if err != nil || st.PageCount != clockInboxCapacity || st.Cursor != clockInboxCapacity {
		t.Fatal(st, err)
	}
}

func TestClockInboxNeverRepairsOrphanedProfileHistory(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, profile := boundInbox(t)
	r, p := inboxPage(0, 1, 0)
	if _, _, err := s.AppendClockEvents(ctx, profile, r, p); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec("DELETE FROM clock_inbox"); err != nil {
		t.Fatal(err)
	}
	for _, target := range []string{profile, t.TempDir()} {
		if _, err := s.BindClockInbox(ctx, target); err == nil {
			t.Fatal("adopted orphaned clock history")
		}
	}
	var count int
	if err := s.db.QueryRow("SELECT count(*) FROM clock_inbox").Scan(&count); err != nil || count != 0 {
		t.Fatal(count, err)
	}
}
