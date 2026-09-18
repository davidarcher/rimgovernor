package buildingruntime

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"path/filepath"
	"testing"
	"time"
)

// clockPollNative serves the poll's bundle: the scope from the scheduler
// fake's Tick, the events page from this fake (issue #127).
type clockPollNative struct {
	core    *clockCoreFake
	page    *k.EventsPage
	err     error
	request *k.EventsRequest
	before  func()
}

func (f *clockPollNative) ReadClockEvents(ctx context.Context, request *k.EventsRequest) (*k.EventsReply, bridge.Result, error) {
	f.request = proto.Clone(request).(*k.EventsRequest)
	if f.err != nil {
		return nil, bridge.Result{}, f.err
	}
	return &k.EventsReply{Outcome: &k.EventsReply_Page{Page: proto.Clone(f.page).(*k.EventsPage)}}, bridge.Result{}, nil
}
func (f *clockPollNative) ReadBundle(ctx context.Context, request *o.BundleRequest) (*o.BundleReply, bridge.Result, error) {
	// before runs while the native call is out: the scope and the page it
	// answers with are both read after it.
	if f.before != nil {
		f.before()
	}
	return composeBundle(ctx, request, bundleParts{tick: f.core.Tick, events: f.ReadClockEvents})
}
func clockPollFixture(t *testing.T) (*ClockScheduler, *schedulerNative, *sql.DB) {
	t.Helper()
	_, _, fake, intent := clockCoreFixture(t)
	path := filepath.Join(t.TempDir(), "poll.sqlite")
	journal, err := store.Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { journal.Close() })
	session, _, profile := newClockSessionTest(t, journal, fake)
	player, err := NewPlayer(context.Background(), PlayerConfig{CallTimeout: 10 * time.Second, JournalTimeout: 10 * time.Second}, journal, session, playerWorldFunc(func(context.Context) (store.World, error) { return playerWorld(intent.Snapshot), nil }))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = player.Close(context.Background()) })
	if _, err = session.Acquire(context.Background(), intent.Snapshot); err != nil {
		t.Fatal(err)
	}
	native := &schedulerNative{clockCoreFake: fake, emergency: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}}
	scheduler, err := NewClockScheduler(player, session, native, ClockSchedulerConfig{Profile: profile, Start: *intent.Command.Start, MaxAge: time.Second}, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	connection, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { connection.Close() })
	return scheduler, native, connection
}
func clockPollPage(f *schedulerNative, after int64, kind string) *k.EventsPage {
	page := &k.EventsPage{Context: proto.Clone(f.status.Context).(*c.ObservationContext), NewestCursor: proto.Int64(after), NextCursor: proto.Int64(after), Gap: proto.Bool(false), LostCount: proto.Uint64(0)}
	if kind == "empty" {
		return page
	}
	page.NextCursor = proto.Int64(after + 1)
	page.NewestCursor = proto.Int64(after + 1)
	if kind == "gap" {
		page.Gap = proto.Bool(true)
		page.LostCount = proto.Uint64(1)
		return page
	}
	event := &k.Event{Cursor: proto.Int64(after + 1), Owner: &k.EpochOwner{ControllerSessionId: proto.String("session"), Epoch: proto.Int64(1)}, Context: proto.Clone(f.status.Context).(*c.ObservationContext), ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_SpeedChanged{SpeedChanged: &k.SpeedChanged{Speed: k.Speed_SPEED_NORMAL.Enum()}}}
	if kind == "alert" {
		event.Event = &k.Event_Alert{Alert: &k.Alert{Key: proto.String("danger"), Label: proto.String("danger"), Priority: proto.String("High")}}
	}
	page.Events = []*k.Event{event}
	return page
}
func TestClockPollPersistsAndReviewsWithoutPlayerGate(t *testing.T) {
	t.Parallel()
	s, f, _ := clockPollFixture(t)
	if _, err := s.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	s.player.gate <- struct{}{}
	defer func() { <-s.player.gate }()
	native := &clockPollNative{core: f.clockCoreFake, page: clockPollPage(f, 0, "alert")}
	result, err := s.PollEvents(context.Background(), native, 128, 0)
	if err == nil || !result.Interrupted || !result.Captured || result.Review.ReviewedCursor != 1 || len(result.Review.Holds) != 1 || s.session.State().Enabled || f.pauses != 1 {
		t.Fatal(result, err, f.pauses)
	}
	if native.request.GetAfterCursor() != 0 || native.request.GetLimit() != 128 {
		t.Fatal(native.request)
	}
}

func TestClockPollDoesNotInvalidateAcquireDuringEventRead(t *testing.T) {
	t.Parallel()
	for _, freshPage := range []bool{false, true} {
		t.Run(fmt.Sprint(freshPage), func(t *testing.T) {
			s, f, _ := clockPollFixture(t)
			ctx := context.Background()
			if err := s.session.Manual(ctx); err != nil {
				t.Fatal(err)
			}
			f.status.Context.NativeGeneration = proto.Uint64(uint64(s.session.State().Snapshot.Native))
			native := &clockPollNative{core: f.clockCoreFake, page: clockPollPage(f, 0, "empty")}
			native.before = func() {
				previous := s.session.State()
				next := previous.Snapshot
				next.Native++
				granted, err := s.session.Acquire(ctx, next)
				if err != nil {
					t.Fatal(err)
				}
				if err = s.session.control.disableObserved(previous); !errors.Is(err, store.ErrConflict) {
					t.Fatal("stale conditional invalidation accepted", err)
				}
				if freshPage {
					f.status.Context.NativeGeneration = proto.Uint64(uint64(granted.Native))
					native.page.Context.NativeGeneration = proto.Uint64(uint64(granted.Native))
				}
			}
			result, err := s.PollEvents(ctx, native, 128, 0)
			if result.Interrupted || !s.session.State().Enabled || (freshPage && err != nil) {
				t.Fatal("poll disabled a newer acquisition", result, err, s.session.State())
			}
		})
	}
}
func TestClockPollPersistenceFailuresAndReviewOrder(t *testing.T) {
	t.Parallel()
	for _, stage := range []string{"append", "review"} {
		t.Run(stage, func(t *testing.T) {
			s, f, db := clockPollFixture(t)
			table, operation := "clock_event_pages", "INSERT"
			if stage == "review" {
				table, operation = "clock_review", "UPDATE"
			}
			if _, err := db.Exec("CREATE TRIGGER fail_poll BEFORE " + operation + " ON " + table + " BEGIN SELECT RAISE(ABORT,'fixture'); END"); err != nil {
				t.Fatal(err)
			}
			result, err := s.PollEvents(context.Background(), &clockPollNative{core: f.clockCoreFake, page: clockPollPage(f, 0, "benign")}, 128, 0)
			if err == nil || !result.Interrupted || s.session.State().Enabled {
				t.Fatal(result, err)
			}
			state, err := s.player.journal.ReadClockReview(context.Background(), s.config.Profile)
			if err != nil {
				t.Fatal(err)
			}
			want := int64(0)
			if stage == "review" {
				want = 1
			}
			if state.InboxCursor != want || state.ReviewedCursor != 0 || result.Captured != (stage == "review") {
				t.Fatal(state, result)
			}
		})
	}
}
func TestClockPollGapExistingHoldEmptyAndDisabled(t *testing.T) {
	t.Parallel()
	s, f, _ := clockPollFixture(t)
	result, err := s.PollEvents(context.Background(), &clockPollNative{core: f.clockCoreFake, page: clockPollPage(f, 0, "gap")}, 128, 0)
	if err == nil || !result.Interrupted || len(result.Review.Holds) != 1 {
		t.Fatal(result, err)
	}
	result, err = s.PollEvents(context.Background(), &clockPollNative{core: f.clockCoreFake, page: clockPollPage(f, 1, "empty")}, 128, 0)
	if err == nil || result.Captured || !result.Interrupted || len(result.Review.Holds) != 1 || result.Review.AcknowledgedCursor != 0 {
		t.Fatal(result, err)
	}
	// Explicit acknowledgement records inspection; polling never re-enables.
	if _, err = s.player.journal.AcknowledgeClockEvents(context.Background(), s.config.Profile, store.ClockAcknowledgement{RequestID: "inspected", ExpectedRevision: result.Review.Revision, ThroughCursor: 1}); err != nil {
		t.Fatal(err)
	}
	page := clockPollPage(f, 1, "benign")
	page.Events[0].Context.Identity.LoadToken = proto.String("historical")
	result, err = s.PollEvents(context.Background(), &clockPollNative{core: f.clockCoreFake, page: page}, 128, 0)
	if err != nil || !result.Captured || result.Review.ReviewedCursor != 2 || s.session.State().Enabled {
		t.Fatal(result, err)
	}
	inbox, err := s.player.journal.LoadClockInbox(context.Background(), s.config.Profile, 4096)
	if err != nil || inbox.Pages[1].Page.Events[0].Context.Identity.GetLoadToken() != "historical" {
		t.Fatal(inbox, err)
	}
}
func TestClockPollReadFailureAndCancellationCleanup(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"read", "cancel", "stale generation", "backlog"} {
		t.Run(kind, func(t *testing.T) {
			s, f, _ := clockPollFixture(t)
			if _, err := s.Step(context.Background()); err != nil {
				t.Fatal(err)
			}
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			native := &clockPollNative{core: f.clockCoreFake, page: clockPollPage(f, 0, "empty")}
			switch kind {
			case "read":
				native.err = errors.New("transport")
			case "cancel":
				native.before = cancel
			case "stale generation":
				native.page.Context.NativeGeneration = proto.Uint64(1)
			case "backlog":
				native.page.NewestCursor = proto.Int64(2)
			}
			result, err := s.PollEvents(ctx, native, 128, 0)
			if err == nil || !result.Interrupted || s.session.State().Enabled || f.pauses != 1 {
				t.Fatal(result, err, f.pauses)
			}
			state, err := s.player.journal.ReadClockReview(context.Background(), s.config.Profile)
			if err != nil || state.InboxCursor != 0 {
				t.Fatal(state, err)
			}
		})
	}
}
