package buildingruntime

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// The scheduler tells the poll loop whether a window it admitted is still
// running (issue #162): set by the admitting step and by a step that sees
// it running, cleared by the step that finds it stopped. That step settles
// the owed epoch from the status its own bundle carried -- no identity or
// status round trip -- and reviews in the same step.
func TestClockSchedulerTracksTheRunningWindowAndSettlesInOneStep(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	ctx := context.Background()
	if s.WindowRunning() {
		t.Fatal("running before any admission")
	}
	got, err := s.Step(ctx)
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || !s.WindowRunning() {
		t.Fatal(got, err, s.WindowRunning())
	}
	if got, err = s.Step(ctx); err != nil || !got.Running || !s.WindowRunning() {
		t.Fatal(got, err, s.WindowRunning())
	}
	epoch := f.status.GetRunning().GetEpoch()
	f.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: k.StopReason_STOP_REASON_WATCH_LATCHED.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(false), StoppedAtUnixMs: proto.Int64(1)}}
	f.status.ActualPaused = proto.Bool(true)
	reads, writes := f.reads, f.writes
	got, err = s.Step(ctx)
	// One bundle read served the retirement and the review; the fixture's
	// status facts refuse the next window, so nothing was written.
	if !errors.Is(err, executor.ErrHeld) || got.Cleaned || got.Running || s.WindowRunning() || f.reads != reads+1 || f.writes != writes || f.pauses != 0 {
		t.Fatal(got, err, s.WindowRunning(), f.reads-reads, f.writes-writes, f.pauses)
	}
	epochs, err := s.player.journal.LoadClockEpochs(ctx, 16)
	if err != nil {
		t.Fatal(err)
	}
	for _, owned := range epochs {
		if !clockCoordinatorTerminal(owned.Stage) {
			t.Fatal("epoch still owed", owned)
		}
	}
}

// A poll page that carries a stopped event clears the running hint.
func TestClockPollStoppedPageClearsTheRunningWindow(t *testing.T) {
	t.Parallel()
	s, f, _ := clockPollFixture(t)
	if _, err := s.Step(context.Background()); err != nil || !s.WindowRunning() {
		t.Fatal(err, s.WindowRunning())
	}
	page := clockPollPage(f, 0, "benign")
	owner, context1 := page.Events[0].Owner, page.Events[0].Context
	page.Events = []*k.Event{
		{Cursor: proto.Int64(1), Owner: owner, Context: context1, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_OperationOutcome{OperationOutcome: clockPollOutcome(3)}},
	}
	page.NextCursor, page.NewestCursor = proto.Int64(1), proto.Int64(1)
	if result, err := s.PollEvents(context.Background(), &clockPollNative{core: f.clockCoreFake, page: page}, 128, 0); err != nil || !result.Captured || !s.WindowRunning() {
		t.Fatal(result, err, "an outcome alone does not stop the window")
	}
	// The committed outcome is remembered for the review's deferral even
	// before any step takes it as a wake reason.
	if got := s.latched.pending([]clockWorkItem{{Action: "wall", Kind: domain.BuildingAction, Stage: domain.Dispatched, Attempt: 3}}); len(got) != 1 {
		t.Fatal(got)
	}
	page = clockPollPage(f, 1, "stopped")
	page.Events = []*k.Event{
		{Cursor: proto.Int64(2), Owner: owner, Context: context1, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_WATCH_LATCHED.Enum(), Evidence: &k.StopEvent_Watch{Watch: &k.WatchLatched{Outcome: clockPollOutcome(3), TickDeadline: proto.Int64(612)}}}}},
	}
	page.NextCursor, page.NewestCursor = proto.Int64(2), proto.Int64(2)
	if result, err := s.PollEvents(context.Background(), &clockPollNative{core: f.clockCoreFake, page: page}, 128, 0); err != nil || !result.Captured || s.WindowRunning() {
		t.Fatal(result, err, s.WindowRunning())
	}
}

// The poll loop holds its read for PollWait only while the scheduler
// reports a window running; otherwise it polls at once at the PollInterval
// cadence, so a held read never queues ahead of a review's native reads.
func TestClockWorkerHoldsThePollOnlyUnderARunningWindow(t *testing.T) {
	t.Parallel()
	w := clockLoopFixture(t)
	w.config.PollInterval = 20 * time.Millisecond
	w.config.PollWait = 10 * time.Millisecond
	var mu sync.Mutex
	var running bool
	var waits []time.Duration
	w.held = func() bool { mu.Lock(); defer mu.Unlock(); return running }
	w.poll = func(ctx context.Context, wait time.Duration) (ClockPollResult, error) {
		mu.Lock()
		waits = append(waits, wait)
		n := len(waits)
		mu.Unlock()
		if wait > 0 {
			time.Sleep(wait)
		}
		if n == 3 {
			mu.Lock()
			running = true
			mu.Unlock()
		}
		return ClockPollResult{}, nil
	}
	w.start()
	time.Sleep(120 * time.Millisecond)
	mu.Lock()
	defer mu.Unlock()
	if len(waits) < 5 {
		t.Fatal(waits)
	}
	for i, wait := range waits {
		if held := i >= 3; (wait > 0) != held {
			t.Fatal(i, waits)
		}
	}
}

// A wake's terminal outcome for an attempt the journal still shows
// dispatched defers admission: the Worker owes that reconcile and the
// successor it unblocks, and a window admitted first would watch the
// settled attempt again for its whole budget. The hold is bounded, and it
// ends as soon as the plan no longer shows the attempt in flight.
func TestClockSchedulerDefersAdmissionOnAnUnreconciledLatchedOutcome(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	items := []clockWorkItem{
		{Action: "wall", Kind: domain.BuildingAction, Stage: domain.Dispatched, Attempt: 2},
		{Action: "allow", Kind: domain.SupplyAllowAction, Stage: domain.Dispatched, Attempt: 1},
	}
	s.latched.remember([]WakeOutcome{
		{Action: "wall", Attempt: 2, Terminal: true},
		{Action: "allow", Attempt: 1, Terminal: true},
		{Action: "other", Attempt: 1, Terminal: true},
		{Action: "unknown", Attempt: 1},
	})
	// Only a watched kind in flight under the latched attempt holds; an
	// immediate designation and an attempt absent from the plan are dropped.
	if got := s.latched.pending(items); len(got) != 1 || got[0] != "wall" || s.latched.len() != 1 {
		t.Fatal(got, s.latched.len())
	}
	for i := 1; i < clockLatchedHoldMax; i++ {
		if got := s.latched.pending(items); len(got) != 1 {
			t.Fatal(i, got)
		}
	}
	if got := s.latched.pending(items); len(got) != 0 || s.latched.len() != 0 {
		t.Fatal("hold must be bounded", got, s.latched.len())
	}
	s.latched.released()
	// A newer attempt of the same action, or a settled stage, releases it.
	s.latched.remember([]WakeOutcome{{Action: "wall", Attempt: 2, Terminal: true}})
	if got := s.latched.pending([]clockWorkItem{{Action: "wall", Kind: domain.BuildingAction, Stage: domain.Dispatched, Attempt: 3}}); len(got) != 0 || s.latched.len() != 0 {
		t.Fatal(got)
	}
	s.latched.remember([]WakeOutcome{{Action: "wall", Attempt: 2, Terminal: true}})
	if got := s.latched.pending([]clockWorkItem{{Action: "wall", Kind: domain.BuildingAction, Stage: domain.Completed, Attempt: 2}}); len(got) != 0 {
		t.Fatal(got)
	}
	// A fresh watched attempt in flight beside it releases it too, without
	// spending a hold: the window admitted now has that one to latch.
	s.latched.remember([]WakeOutcome{{Action: "wall", Attempt: 2, Terminal: true}})
	fresh := append(items, clockWorkItem{Action: "next", Kind: domain.BuildingAction, Stage: domain.Dispatched, Attempt: 1})
	if got := s.latched.pending(fresh); len(got) != 0 || s.latched.len() != 1 {
		t.Fatal(got, s.latched.len())
	}
	s.latched.released()
	if got := s.latched.pending(items); len(got) != 1 || got[0] != "wall" {
		t.Fatal("the hold resumes once the fresh attempt is gone", got)
	}
	// Through a step: the fixture plan's first action dispatched under
	// attempt 10 and a wake latching it defers the admission, with no
	// native write; a plain step then admits.
	ctx := context.Background()
	state, err := s.player.journal.LoadPlan(ctx, s.session.State().Snapshot.Plan)
	if err != nil {
		t.Fatal(err)
	}
	action, snapshot := state.Progress[0].Action(), s.session.State().Snapshot
	building, _ := action.Building()
	if _, err = s.player.journal.ReserveAndPrepare(ctx, snapshot.Plan, action.ID(), store.Admission{Snapshot: snapshot, Tick: 1, Costs: []store.MaterialCost{}, Footprint: []domain.Cell{building.Cell()}}); err != nil {
		t.Fatal(err)
	}
	p, err := s.player.journal.Dispatch(ctx, snapshot.Plan, action.ID(), snapshot, 1)
	if err != nil {
		t.Fatal(err)
	}
	s.config.Worker = true
	got, err := s.StepWithReason(ctx, StepReason{Cause: StepWake, Events: []WakeOutcome{{Action: p.View().Action, Attempt: p.View().Attempt, Terminal: true}}})
	if err != nil || !got.Deferred || got.Attempt != nil || f.writes != 0 {
		t.Fatal(got, err, f.writes)
	}
	s.latched = newClockLatched()
	if got, err = s.Step(ctx); err != nil || got.Deferred || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied {
		t.Fatal(got, err)
	}
}

// Queued watched-kind work with nothing of its kind dispatched defers the
// admission for the Worker's dispatch, at most clockLatchedHoldMax steps
// per action over its life; a dispatched peer or a settled stage ends it,
// and immediate designations never hold. Without a Worker the review
// proceeds at once (the scheduler fixture admits its pending plan).
func TestClockSchedulerDefersAdmissionForUndispatchedWork(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	queued := []clockWorkItem{
		{Action: "wall", Kind: domain.BuildingAction, Stage: domain.Pending},
		{Action: "door", Kind: domain.BuildingAction, Stage: domain.Prepared},
		{Action: "allow", Kind: domain.SupplyAllowAction, Stage: domain.Pending},
	}
	for i := 0; i < clockLatchedHoldMax; i++ {
		if got := s.latched.undispatched(queued); len(got) != 2 || got[0] != "door" || got[1] != "wall" {
			t.Fatal(i, got)
		}
	}
	if got := s.latched.undispatched(queued); len(got) != 0 {
		t.Fatal("hold must be bounded", got)
	}
	// Leaving the queued stages forgets the count; a dispatched peer of
	// the watched kind means the window has something to watch. The run
	// of deferrals is bounded across every action too: it ends with a
	// step that was not deferred (released), not by itself.
	if got := s.latched.undispatched([]clockWorkItem{{Action: "wall", Kind: domain.BuildingAction, Stage: domain.Dispatched, Attempt: 1}}); len(got) != 0 {
		t.Fatal(got)
	}
	if got := s.latched.undispatched(queued); len(got) != 0 {
		t.Fatal("the run of deferrals is spent", got)
	}
	s.latched.released()
	if got := s.latched.undispatched(queued); len(got) != 2 {
		t.Fatal(got)
	}
	s.latched.released()
	if got := s.latched.undispatched(append(queued, clockWorkItem{Action: "bed", Kind: domain.BuildingAction, Stage: domain.AwaitingObservation, Attempt: 1})); len(got) != 0 {
		t.Fatal(got)
	}
	if got := s.latched.undispatched([]clockWorkItem{{Action: "allow", Kind: domain.SupplyAllowAction, Stage: domain.Pending}}); len(got) != 0 {
		t.Fatal(got)
	}
	// Through a step with a Worker attached: the fixture plan's pending
	// building action defers the admission its bound, then admits.
	ctx := context.Background()
	s.config.Worker = true
	for i := 0; i < clockLatchedHoldMax; i++ {
		if got, err := s.Step(ctx); err != nil || !got.Deferred || got.Attempt != nil || f.writes != 0 {
			t.Fatal(i, got, err, f.writes)
		}
	}
	if got, err := s.Step(ctx); err != nil || got.Deferred || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied {
		t.Fatal(got, err)
	}
}

// With no held read, the poll loop keeps the RunningPollInterval cadence
// while the scheduler reports a window running and the PollInterval
// cadence otherwise.
func TestClockWorkerPollsFasterUnderARunningWindow(t *testing.T) {
	t.Parallel()
	w := clockLoopFixture(t)
	w.config.PollInterval = 60 * time.Millisecond
	w.config.RunningPollInterval = 5 * time.Millisecond
	w.config.PollWait = 0
	var mu sync.Mutex
	var running bool
	var polls []time.Time
	w.held = func() bool { mu.Lock(); defer mu.Unlock(); return running }
	w.poll = func(ctx context.Context, wait time.Duration) (ClockPollResult, error) {
		mu.Lock()
		defer mu.Unlock()
		polls = append(polls, time.Now())
		if wait != 0 {
			t.Error("read must not be held", wait)
		}
		if len(polls) == 3 {
			running = true
		}
		return ClockPollResult{}, nil
	}
	w.start()
	time.Sleep(400 * time.Millisecond)
	mu.Lock()
	defer mu.Unlock()
	// Two idle intervals (~120ms) then the running cadence: well over the
	// handful of polls the idle cadence alone would allow in 400ms.
	if len(polls) < 12 {
		t.Fatal(len(polls), polls)
	}
	if gap := polls[2].Sub(polls[1]); gap < 40*time.Millisecond {
		t.Fatal("idle cadence", gap)
	}
}

// Work of a watched kind dispatched after a window was armed cannot stop
// that window: the native watch list is fixed at Start. A running step that
// finds such an attempt in the plan pauses its own epoch, so the next step
// settles it and admits a window that watches the attempt, instead of
// letting the window run out its whole budget with the routine review
// frozen at its admission tick (#207).
func TestClockSchedulerPausesARunningWindowToWatchWorkDispatchedSinceItStarted(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	ctx := context.Background()
	got, err := s.Step(ctx)
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || got.Watched != 0 {
		t.Fatal(got, err)
	}
	if got, err = s.Step(ctx); err != nil || !got.Running || got.Rearmed || f.pauses != 0 {
		t.Fatal(got, err, f.pauses)
	}
	// The Worker dispatches the plan's building action mid-window.
	snapshot := s.session.State().Snapshot
	state, err := s.player.journal.LoadPlan(ctx, snapshot.Plan)
	if err != nil {
		t.Fatal(err)
	}
	action := state.Progress[0].Action()
	building, _ := action.Building()
	if _, err = s.player.journal.ReserveAndPrepare(ctx, snapshot.Plan, action.ID(), store.Admission{Snapshot: snapshot, Tick: 1, Costs: []store.MaterialCost{}, Footprint: []domain.Cell{building.Cell()}}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.player.journal.Dispatch(ctx, snapshot.Plan, action.ID(), snapshot, 1); err != nil {
		t.Fatal(err)
	}
	got, err = s.Step(ctx)
	if err != nil || !got.Cleaned || !got.Rearmed || got.Running || s.WindowRunning() || f.pauses != 1 {
		t.Fatal(got, err, s.WindowRunning(), f.pauses)
	}
	epochs, err := s.player.journal.LoadClockEpochs(ctx, 16)
	if err != nil {
		t.Fatal(err)
	}
	for _, owned := range epochs {
		if !clockCoordinatorTerminal(owned.Stage) {
			t.Fatal("epoch still owed", owned)
		}
	}
	// The settled step reviews and reaches admission (the fixture's paused
	// status facts refuse the window itself) without another pause.
	if got, err = s.StepWithReason(ctx, StepReason{Cause: StepSettled}); !errors.Is(err, executor.ErrHeld) || got.Cleaned || got.Rearmed || f.pauses != 1 {
		t.Fatal(got, err, f.pauses)
	}
}
