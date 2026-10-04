package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func clockPollOutcome(attempt uint64) *k.OperationOutcome {
	key := &c.AttemptKey{ControllerSessionId: proto.String("session"), ActionId: proto.String("wall"), AttemptId: proto.Uint64(attempt)}
	return &k.OperationOutcome{Attempt: key, LatchedTick: proto.Int64(40), Outcome: &k.OperationOutcome_Completed{Completed: &r.CompletedEffect{Evidence: &r.EffectEvidence{Effect: &r.EffectEvidence_Construction{Construction: &r.ConstructionEffect{}}}}}}
}

// A page carrying a latched outcome and its WATCH_LATCHED stop is benign:
// nothing is held, authority stays enabled, and the committed evidence is
// summarized as a wake. An owner-less authority change is accepted too.
func TestClockPollWatchLatchedIsBenignAndWakes(t *testing.T) {
	if testing.Short() {
		t.Skip("slow: runs under cmd/test -full and nightly")
	}
	t.Parallel()
	s, f, _ := clockPollFixture(t)
	if _, err := s.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	page := clockPollPage(f, 0, "benign")
	owner := page.Events[0].Owner
	context1 := page.Events[0].Context
	page.Events = []*k.Event{
		{Cursor: proto.Int64(1), Owner: owner, Context: context1, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_OperationOutcome{OperationOutcome: clockPollOutcome(3)}},
		{Cursor: proto.Int64(2), Owner: owner, Context: context1, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_WATCH_LATCHED.Enum(), Evidence: &k.StopEvent_Watch{Watch: &k.WatchLatched{Outcome: clockPollOutcome(3), TickDeadline: proto.Int64(612)}}}}},
		{Cursor: proto.Int64(3), Context: context1, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{Generation: proto.Uint64(9), Reason: proto.String("acquired")}}},
	}
	page.NextCursor, page.NewestCursor = proto.Int64(3), proto.Int64(3)
	native := &clockPollNative{core: f.clockCoreFake, page: page}
	result, err := s.PollEvents(context.Background(), native, 128, 1500*time.Millisecond)
	if err != nil || result.Interrupted || !result.Captured || len(result.Review.Holds) != 0 || result.Review.ReviewedCursor != 3 || !s.session.State().Enabled {
		t.Fatal(result, err, s.session.State())
	}
	if native.request.GetWaitMs() != 1500 {
		t.Fatal("wait not forwarded", native.request)
	}
	if len(result.Wake) != 2 || result.Wake[0] != (WakeOutcome{Action: domain.ActionID("wall"), Attempt: 3, Terminal: true}) || !result.AuthorityChanged {
		t.Fatal(result.Wake, result.AuthorityChanged)
	}
	// An empty page commits nothing and therefore wakes nothing.
	result, err = s.PollEvents(context.Background(), &clockPollNative{core: f.clockCoreFake, page: clockPollPage(f, 3, "empty")}, 128, 0)
	if err != nil || result.Captured || len(result.Wake) != 0 || result.AuthorityChanged {
		t.Fatal(result, err)
	}
	if _, err = s.PollEvents(context.Background(), native, 128, 6*time.Second); err == nil {
		t.Fatal("wait above the bound must be refused")
	}
}

// A page whose external-pause stop precedes the acquisition of the
// generation authority holds now (the service's own pause, save and
// resume, as a checkpoint does) acknowledges the interruption hold and does
// not disable the fresh grant: the stop was answered by that grant, and the
// answered hold is not left for anyone else to clear (#322). The same stop
// after the grant still disables and holds.
func TestClockPollStopBeforeTheCurrentGrantKeepsAuthority(t *testing.T) {
	if testing.Short() {
		t.Skip("slow: runs under cmd/test -full and nightly")
	}
	t.Parallel()
	for _, stopAfterGrant := range []bool{false, true} {
		s, f, _ := clockPollFixture(t)
		if _, err := s.Step(context.Background()); err != nil {
			t.Fatal(err)
		}
		generation := uint64(s.session.State().Snapshot.Native)
		page := clockPollPage(f, 0, "benign")
		owner, context1 := page.Events[0].Owner, page.Events[0].Context
		stop := &k.Event{Owner: owner, Context: context1, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_EXTERNAL_PAUSE.Enum(), Evidence: &k.StopEvent_Pause{Pause: &k.PauseEvidence{}}}}}
		grant := &k.Event{Context: context1, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{Generation: proto.Uint64(generation), PreviousGeneration: proto.Uint64(generation - 1), Reason: proto.String("None"), Active: proto.Bool(true)}}}
		page.Events = []*k.Event{stop, grant}
		if stopAfterGrant {
			page.Events = []*k.Event{grant, stop}
		}
		for i, event := range page.Events {
			event.Cursor = proto.Int64(int64(i + 1))
		}
		page.NextCursor, page.NewestCursor = proto.Int64(2), proto.Int64(2)
		result, err := s.PollEvents(context.Background(), &clockPollNative{core: f.clockCoreFake, page: page}, 128, 0)
		if held := errors.Is(err, executor.ErrHeld); held != stopAfterGrant || len(result.Review.Holds) != map[bool]int{true: 1}[stopAfterGrant] || (!stopAfterGrant && err != nil) {
			t.Fatal(stopAfterGrant, result, err)
		}
		if enabled := s.session.State().Enabled; enabled == stopAfterGrant || result.Interrupted != stopAfterGrant {
			t.Fatal(stopAfterGrant, enabled, result.Interrupted)
		}
		if review, err := s.player.journal.ReadClockReview(context.Background(), s.config.Profile); err != nil || (review.AcknowledgedCursor == 2) == stopAfterGrant {
			t.Fatal(stopAfterGrant, review, err)
		}
	}
}

// The grant is remembered across pages: a stop read by one poll and the
// resume's grant by the next: the poll reading the grant acknowledges the
// standing hold instead of disabling the new grant, and later polls admit
// windows again (#322).
func TestClockPollStandingHoldBeforeTheRememberedGrantKeepsAuthority(t *testing.T) {
	if testing.Short() {
		t.Skip("slow: runs under cmd/test -full and nightly")
	}
	t.Parallel()
	s, f, _ := clockPollFixture(t)
	ctx := context.Background()
	if _, err := s.Step(ctx); err != nil {
		t.Fatal(err)
	}
	page := clockPollPage(f, 0, "benign")
	owner, context1 := page.Events[0].Owner, page.Events[0].Context
	page.Events = []*k.Event{{Cursor: proto.Int64(1), Owner: owner, Context: context1, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_EXTERNAL_PAUSE.Enum(), Evidence: &k.StopEvent_Pause{Pause: &k.PauseEvidence{}}}}}}
	if _, err := s.PollEvents(ctx, &clockPollNative{core: f.clockCoreFake, page: page}, 128, 0); !errors.Is(err, executor.ErrHeld) || s.session.State().Enabled {
		t.Fatal(err, s.session.State())
	}
	// The checkpoint's pause revokes and its resume re-acquires; the grant
	// arrives on the next page.
	snapshot := s.session.State().Snapshot
	if err := s.session.Manual(ctx); err != nil {
		t.Fatal(err)
	}
	granted, err := s.session.Acquire(ctx, snapshot)
	if err != nil {
		t.Fatal(err)
	}
	f.status.Context.NativeGeneration = proto.Uint64(uint64(granted.Native))
	page = clockPollPage(f, 1, "benign")
	page.Events = []*k.Event{{Cursor: proto.Int64(2), Context: page.Events[0].Context, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{Generation: proto.Uint64(uint64(granted.Native)), Reason: proto.String("None"), Active: proto.Bool(true)}}}}
	for _, next := range []*k.EventsPage{page, clockPollPage(f, 2, "empty")} {
		result, err := s.PollEvents(ctx, &clockPollNative{core: f.clockCoreFake, page: next}, 128, 0)
		if err != nil || len(result.Review.Holds) != 0 || result.Interrupted || !s.session.State().Enabled {
			t.Fatal(result, err, s.session.State())
		}
	}
	if review, err := s.player.journal.ReadClockReview(ctx, s.config.Profile); err != nil || review.AcknowledgedCursor != 2 || len(review.Holds) != 0 {
		t.Fatal(review, err)
	}
}

// A kept game's backlog read while nothing is enabled is history: a page of
// stops at or before the watermark the first poll fixed leaves the control
// epoch (an acquisition in flight) in place, though its holds still stand.
// A stop past the watermark is fresh evidence and replaces it (#322).
func TestClockPollBacklogWhileDisabledKeepsAcquireEpoch(t *testing.T) {
	if testing.Short() {
		t.Skip("slow: runs under cmd/test -full and nightly")
	}
	t.Parallel()
	s, f, _ := clockPollFixture(t)
	ctx := context.Background()
	stop := func(cursor int64) *k.Event {
		return &k.Event{Cursor: proto.Int64(cursor), Owner: &k.EpochOwner{ControllerSessionId: proto.String("session"), Epoch: proto.Int64(1)}, Context: proto.Clone(f.status.Context).(*c.ObservationContext), ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_EXTERNAL_PAUSE.Enum(), Evidence: &k.StopEvent_Pause{Pause: &k.PauseEvidence{}}}}}
	}
	// Nothing is enabled yet, as at startup before the first resume.
	if err := s.session.Disable(); err != nil {
		t.Fatal(err)
	}
	// The backlog is three events deep; the first page carries two of them.
	page := clockPollPage(f, 0, "empty")
	page.Events = []*k.Event{stop(1), stop(2)}
	page.NextCursor, page.NewestCursor = proto.Int64(2), proto.Int64(3)
	var epoch context.Context
	capture := func() {
		s.session.control.mu.Lock()
		epoch = s.session.control.epoch
		s.session.control.mu.Unlock()
	}
	native := &clockPollNative{core: f.clockCoreFake, page: page, before: capture}
	result, err := s.PollEvents(ctx, native, 128, 0)
	if !errors.Is(err, executor.ErrHeld) || !result.Captured || len(result.Review.Holds) == 0 || s.session.State().Enabled {
		t.Fatal(result, err)
	}
	if epoch.Err() != nil {
		t.Fatal("a backlog page replaced the control epoch under a disabled session")
	}
	page = clockPollPage(f, 2, "empty")
	page.Events = []*k.Event{stop(3)}
	page.NextCursor, page.NewestCursor = proto.Int64(3), proto.Int64(3)
	native = &clockPollNative{core: f.clockCoreFake, page: page, before: capture}
	if _, err = s.PollEvents(ctx, native, 128, 0); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
	if epoch.Err() != nil {
		t.Fatal("the rest of the backlog replaced the control epoch under a disabled session")
	}
	// Past the watermark the stop is fresh.
	page = clockPollPage(f, 3, "empty")
	page.Events = []*k.Event{stop(4)}
	page.NextCursor, page.NewestCursor = proto.Int64(4), proto.Int64(4)
	native = &clockPollNative{core: f.clockCoreFake, page: page, before: capture}
	if _, err = s.PollEvents(ctx, native, 128, 0); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
	if epoch.Err() == nil {
		t.Fatal("a stop past the watermark left the control epoch in place")
	}
}

// Under a standing authority (serve --resume's auto-resume grant lands before
// the backlog is read) a backlog of earlier sessions' stops at or before the
// watermark is history: it neither disables authority nor replaces the epoch.
// A stop past the watermark still interrupts.
func TestClockPollBacklogWhileEnabledKeepsAuthority(t *testing.T) {
	if testing.Short() {
		t.Skip("slow: runs under cmd/test -full and nightly")
	}
	t.Parallel()
	s, f, _ := clockPollFixture(t)
	ctx := context.Background()
	stop := func(cursor int64) *k.Event {
		return &k.Event{Cursor: proto.Int64(cursor), Owner: &k.EpochOwner{ControllerSessionId: proto.String("session"), Epoch: proto.Int64(1)}, Context: proto.Clone(f.status.Context).(*c.ObservationContext), ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_EXTERNAL_PAUSE.Enum(), Evidence: &k.StopEvent_Pause{Pause: &k.PauseEvidence{}}}}}
	}
	snapshot := s.session.State().Snapshot
	if err := s.session.Disable(); err != nil {
		t.Fatal(err)
	}
	var epoch context.Context
	capture := func() {
		s.session.control.mu.Lock()
		epoch = s.session.control.epoch
		s.session.control.mu.Unlock()
	}
	poll := func(after int64, newest int64, events ...*k.Event) (ClockPollResult, error) {
		page := clockPollPage(f, after, "empty")
		page.Events = events
		page.NextCursor, page.NewestCursor = proto.Int64(events[len(events)-1].GetCursor()), proto.Int64(newest)
		return s.PollEvents(ctx, &clockPollNative{core: f.clockCoreFake, page: page, before: capture}, 128, 0)
	}
	// The first page, read before any authority, fixes the watermark at 3.
	if _, err := poll(0, 3, stop(1), stop(2)); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
	// Auto-resume acquires under a generation the backlog never names.
	granted, err := s.session.Acquire(ctx, snapshot)
	if err != nil {
		t.Fatal(err)
	}
	f.status.Context.NativeGeneration = proto.Uint64(uint64(granted.Native))
	if _, err = poll(2, 3, stop(3)); err != nil && !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
	if epoch.Err() != nil || !s.session.State().Enabled {
		t.Fatal("the backlog revoked standing authority", s.session.State())
	}
	result, _ := poll(3, 4, stop(4))
	if !result.Interrupted || epoch.Err() == nil || s.session.State().Enabled {
		t.Fatal("a stop past the watermark did not interrupt", result, s.session.State())
	}
}
