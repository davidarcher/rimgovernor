package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// speedNative is a clock native whose game tick runs on the wall clock: a
// started window advances one tick every tickEvery until its deadline, then
// stops on its budget (status Stopped, a Stopped event on the journal, the
// long poll released). Every read observes the tick of the moment it is
// served, so the scheduler and worker see the same sequence of stops at any
// tick multiplier, only faster or slower in wall time (issue #112).
type speedNative struct {
	mu        sync.Mutex
	status    *k.Status
	emergency policy.EmergencyFacts
	tickEvery time.Duration
	epochs    int64
	startedAt time.Time
	startTick int64
	deadline  int64
	events    []*k.Event
	changed   chan struct{}
	receipt   *k.ControlReceipt
	// stops is the wall time of each budget stop, in order.
	stops []time.Time
}

func newSpeedNative(snapshot domain.GenerationSnapshot, tickEvery time.Duration) *speedNative {
	status := &k.Status{Context: &c.ObservationContext{Identity: boundary.Identity(snapshot), Tick: proto.Int64(12), NativeGeneration: proto.Uint64(7)}, State: &k.Status_NeverStarted{NeverStarted: &k.NeverStarted{}}, ActualPaused: proto.Bool(true), ObservedSpeed: k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum(), NativeTickBoundary: proto.Bool(true), DurableEvents: proto.Bool(true), NewestCursor: proto.Int64(0), EvidenceCompleteness: &c.PageInfo{Complete: proto.Bool(true)}}
	return &speedNative{status: status, emergency: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}, tickEvery: tickEvery, changed: make(chan struct{})}
}

// advance moves the running window's tick to now and stops it on its
// budget. Callers hold mu.
func (n *speedNative) advance() {
	running := n.status.GetRunning()
	if running == nil {
		return
	}
	tick := n.startTick + int64(time.Since(n.startedAt)/n.tickEvery)
	if tick > n.deadline {
		tick = n.deadline
	}
	n.status.Context.Tick = proto.Int64(tick)
	running.Epoch.LastTick = proto.Int64(tick)
	if tick < n.deadline {
		return
	}
	now := time.Now()
	epoch := running.Epoch
	n.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: k.StopReason_STOP_REASON_TICK_BUDGET.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(false), StoppedAtUnixMs: proto.Int64(now.UnixMilli())}}
	n.status.ActualPaused = proto.Bool(true)
	n.status.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum()
	cursor := int64(len(n.events) + 1)
	n.events = append(n.events, &k.Event{Cursor: proto.Int64(cursor), Owner: proto.Clone(epoch.Owner).(*k.EpochOwner), Context: proto.Clone(n.status.Context).(*c.ObservationContext), ObservedAtUnixMs: proto.Int64(now.UnixMilli()),
		Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_TICK_BUDGET.Enum(), Evidence: &k.StopEvent_Budget{Budget: &k.BudgetReached{StartTick: proto.Int64(n.startTick), TickDeadline: proto.Int64(n.deadline), ActualTick: proto.Int64(tick)}}}}})
	n.status.NewestCursor = proto.Int64(cursor)
	n.stops = append(n.stops, now)
	close(n.changed)
	n.changed = make(chan struct{})
}

// due is how long until the running window reaches its deadline; false
// when no window runs. Callers hold mu.
func (n *speedNative) due() (time.Duration, bool) {
	if n.status.GetRunning() == nil {
		return 0, false
	}
	return time.Until(n.startedAt.Add(time.Duration(n.deadline-n.startTick) * n.tickEvery)), true
}

func (n *speedNative) context() *c.ObservationContext {
	return proto.Clone(n.status.Context).(*c.ObservationContext)
}

func (n *speedNative) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: n.context()}}}, bridge.Result{}, ctx.Err()
}

func (n *speedNative) Tick(ctx context.Context) (*l.TickReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	return &l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: n.context()}}}, bridge.Result{}, ctx.Err()
}

func (n *speedNative) ReadClockStatus(ctx context.Context, id *c.Identity) (*k.StatusReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	return &k.StatusReply{Outcome: &k.StatusReply_Status{Status: proto.Clone(n.status).(*k.Status)}}, bridge.Result{}, ctx.Err()
}

func (n *speedNative) ReadClockAttempt(ctx context.Context, r *k.AttemptRequest) (*k.AttemptReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	if n.receipt == nil || !proto.Equal(n.receipt.Attempt, r.Attempt) {
		return &k.AttemptReply{Outcome: &k.AttemptReply_Unknown{Unknown: &k.AttemptUnknown{}}}, bridge.Result{}, ctx.Err()
	}
	return &k.AttemptReply{Outcome: &k.AttemptReply_Receipt{Receipt: proto.Clone(n.receipt).(*k.ControlReceipt)}}, bridge.Result{}, ctx.Err()
}

func (n *speedNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	return bridge.EmergencyObservation{Context: n.context(), Facts: n.emergency}, bridge.Result{}, ctx.Err()
}

func (n *speedNative) Start(ctx context.Context, r *k.StartRequest) (*k.ControlReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	if n.status.GetRunning() != nil {
		return nil, bridge.Result{}, errors.New("start while running")
	}
	n.epochs++
	tick := n.status.Context.GetTick()
	epoch := &k.Epoch{Owner: &k.EpochOwner{ControllerSessionId: proto.String(r.Authority.Attempt.GetControllerSessionId()), Epoch: proto.Int64(n.epochs)}, Origin: n.context(), RequestedSpeed: r.Speed, Policy: r.Policy, StartTick: proto.Int64(tick), TickDeadline: proto.Int64(tick + int64(r.GetMaxTicks())), LastTick: proto.Int64(tick), LeaseRemainingMs: proto.Uint32(r.GetLeaseMs())}
	n.status.State = &k.Status_Running{Running: &k.Running{Epoch: epoch}}
	n.status.ActualPaused = proto.Bool(false)
	n.status.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_NORMAL.Enum()
	n.startedAt, n.startTick, n.deadline = time.Now(), tick, epoch.GetTickDeadline()
	n.receipt = &k.ControlReceipt{Attempt: proto.Clone(r.Authority.Attempt).(*c.AttemptKey), AdmittedContext: n.context(), Outcome: &k.ControlReceipt_Applied{Applied: &k.AppliedControl{Status: proto.Clone(n.status).(*k.Status)}}}
	return &k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: proto.Clone(n.receipt).(*k.ControlReceipt)}}, bridge.Result{}, ctx.Err()
}

func (n *speedNative) applied(pre *a.WritePrecondition) (*k.ControlReply, bridge.Result, error) {
	receipt := &k.ControlReceipt{Attempt: proto.Clone(pre.Attempt).(*c.AttemptKey), AdmittedContext: n.context(), Outcome: &k.ControlReceipt_Applied{Applied: &k.AppliedControl{Status: proto.Clone(n.status).(*k.Status)}}}
	return &k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: receipt}}, bridge.Result{}, nil
}

func (n *speedNative) Renew(ctx context.Context, r *k.RenewRequest, original *k.Epoch) (*k.ControlReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	return n.applied(r.Authority)
}

func (n *speedNative) ChangeSpeed(ctx context.Context, r *k.SpeedRequest, original *k.Epoch) (*k.ControlReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	if running := n.status.GetRunning(); running != nil {
		running.Epoch.RequestedSpeed = r.Speed
	}
	return n.applied(r.Authority)
}

func (n *speedNative) OwnedPause(ctx context.Context, r *k.OwnedRequest) (*k.StatusReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	if running := n.status.GetRunning(); running != nil {
		n.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: running.Epoch, Reason: k.StopReason_STOP_REASON_REQUESTED_PAUSE.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(true), StoppedAtUnixMs: proto.Int64(time.Now().UnixMilli())}}
		n.status.ActualPaused = proto.Bool(true)
		n.status.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum()
	}
	return &k.StatusReply{Outcome: &k.StatusReply_Status{Status: proto.Clone(n.status).(*k.Status)}}, bridge.Result{}, ctx.Err()
}

// await is the long poll: it returns once an event follows after, the
// wait elapses or ctx ends, sleeping only until the running window's
// deadline so a budget stop is observed the moment it lands.
func (n *speedNative) await(ctx context.Context, after int64, wait time.Duration) {
	until := time.Now().Add(wait)
	for {
		n.mu.Lock()
		n.advance()
		newest := int64(len(n.events))
		changed := n.changed
		due, running := n.due()
		n.mu.Unlock()
		remaining := time.Until(until)
		if newest > after || remaining <= 0 || ctx.Err() != nil {
			return
		}
		if running && due < remaining {
			remaining = max(due, 0)
		}
		timer := time.NewTimer(remaining)
		select {
		case <-ctx.Done():
		case <-changed:
		case <-timer.C:
		}
		timer.Stop()
	}
}

// page serves the events after the request's cursor. Callers hold mu.
func (n *speedNative) page(request *k.EventsRequest) *k.EventsPage {
	after := request.GetAfterCursor()
	newest := int64(len(n.events))
	page := &k.EventsPage{Context: n.context(), NewestCursor: proto.Int64(newest), NextCursor: proto.Int64(after), Gap: proto.Bool(false), LostCount: proto.Uint64(0)}
	if after > newest {
		page.NextCursor = proto.Int64(newest)
	}
	for _, event := range n.events {
		if event.GetCursor() > after {
			page.Events = append(page.Events, proto.Clone(event).(*k.Event))
			page.NextCursor = proto.Int64(event.GetCursor())
		}
	}
	return page
}

func (n *speedNative) ReadClockEvents(ctx context.Context, request *k.EventsRequest) (*k.EventsReply, bridge.Result, error) {
	n.await(ctx, request.GetAfterCursor(), time.Duration(request.GetWaitMs())*time.Millisecond)
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	return &k.EventsReply{Outcome: &k.EventsReply_Page{Page: n.page(request)}}, bridge.Result{}, ctx.Err()
}

// ReadBundle waits like the long poll first, then composes every section
// from one locked snapshot so the tick, status, emergency and events page
// agree, as the native bundle does.
func (n *speedNative) ReadBundle(ctx context.Context, request *o.BundleRequest) (*o.BundleReply, bridge.Result, error) {
	if request.Events != nil {
		n.await(ctx, request.Events.GetAfterCursor(), time.Duration(request.Events.GetWaitMs())*time.Millisecond)
	}
	n.mu.Lock()
	n.advance()
	snapshot := &speedNative{status: proto.Clone(n.status).(*k.Status), emergency: n.emergency, events: n.events}
	n.mu.Unlock()
	parts := bundleParts{
		tick: func(ctx context.Context) (*l.TickReply, bridge.Result, error) {
			return &l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: snapshot.context()}}}, bridge.Result{}, ctx.Err()
		},
		status: func(ctx context.Context, id *c.Identity) (*k.StatusReply, bridge.Result, error) {
			return &k.StatusReply{Outcome: &k.StatusReply_Status{Status: proto.Clone(snapshot.status).(*k.Status)}}, bridge.Result{}, ctx.Err()
		},
		emergency: func(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
			return bridge.EmergencyObservation{Context: snapshot.context(), Facts: snapshot.emergency}, bridge.Result{}, ctx.Err()
		},
		events: func(ctx context.Context, request *k.EventsRequest) (*k.EventsReply, bridge.Result, error) {
			return &k.EventsReply{Outcome: &k.EventsReply_Page{Page: snapshot.page(request)}}, bridge.Result{}, ctx.Err()
		},
	}
	return composeBundle(ctx, request, parts)
}

// speedStops returns the wall time of each budget stop so far.
func (n *speedNative) speedStops() []time.Time {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.advance()
	return append([]time.Time(nil), n.stops...)
}

type wallClock struct{}

func (wallClock) Now() time.Time { return time.Now() }

// speedStep is one recorded scheduler step: when it began, the reason it
// acted on and what it decided.
type speedStep struct {
	began  time.Time
	reason StepReason
	result ClockSchedulerResult
	err    error
}

// speedMatrixFixture is schedulerFixture on a speedNative with the worker
// loops of NewClockWorker, the step wrapped so the test sees each step's
// reason and result.
func speedMatrixFixture(t *testing.T, native *speedNative, snapshot domain.GenerationSnapshot, config ClockWorkerConfig, record func(speedStep)) (*ClockScheduler, *ClockWorker) {
	t.Helper()
	db, err := store.Open(context.Background(), storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	_, fixture := boundary.NewFixture(t)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{fixture.Placement.Action})
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	sessionConfig := SessionConfig{Control: ControlConfig{ProfileDirectory: t.TempDir(), CallTimeout: 10 * time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second}, Clock: &ClockCapabilities{Native: native, Writer: native}}
	sessionFixture := sessionNative{fixture}
	session, err := NewSession(context.Background(), sessionConfig, db, sessionFixture, &controlNative{generation: 6}, sessionFixture, wallClock{})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = session.Close(context.Background()) })
	player, err := NewPlayer(context.Background(), PlayerConfig{CallTimeout: 10 * time.Second, JournalTimeout: 10 * time.Second}, db, session, playerWorldFunc(func(context.Context) (store.World, error) { return playerWorld(snapshot), nil }))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = player.Close(context.Background()) })
	if _, err = session.Acquire(context.Background(), snapshot); err != nil {
		t.Fatal(err)
	}
	watch := &k.WatchPolicy{Mode: k.WatchMode_WATCH_MODE_COLONY.Enum(), HealthDropFraction: proto.Float32(.1), MinHealthFraction: proto.Float32(.2), HostileWithin: proto.Float32(20), InjuryStopCooldownMs: proto.Uint32(0)}
	start := bridge.ClockStart{Speed: k.Speed_SPEED_NORMAL, Policy: watch, LeaseMS: 30_000, MaxTicks: 100}
	scheduler, err := NewClockScheduler(player, session, native, ClockSchedulerConfig{Profile: sessionConfig.Control.ProfileDirectory, Start: start, MaxAge: time.Second}, wallClock{})
	if err != nil {
		t.Fatal(err)
	}
	// NewClockWorker starts its loops before returning, so the step is
	// wrapped here, on a worker built the same way.
	lifetime, cancel := context.WithCancel(context.Background())
	worker := &ClockWorker{ctx: lifetime, cancel: cancel, config: config, done: make(chan struct{}), ready: make(chan struct{}), stopGate: make(chan struct{}, 1), disable: session.disableClockWorker, cleanup: session.CleanupClock, renew: scheduler.RenewEpoch, wake: NewWakeSignal()}
	worker.step = func(ctx context.Context, reason StepReason) (ClockSchedulerResult, error) {
		began := time.Now()
		result, err := scheduler.StepWithReason(ctx, reason)
		record(speedStep{began: began, reason: reason, result: result, err: err})
		return result, err
	}
	worker.poll = func(ctx context.Context) (ClockPollResult, error) {
		return scheduler.PollEvents(ctx, native, config.PageLimit, config.PollWait)
	}
	worker.stopParent = context.AfterFunc(player.lifetime, cancel)
	if err = session.attachClockWorker(worker); err != nil {
		t.Fatal(err)
	}
	worker.start()
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		if err := worker.Stop(ctx); err != nil {
			t.Error(err)
		}
	})
	return scheduler, worker
}

// speedDecision is one clock decision keyed by game tick: a window's start
// or its deadline.
type speedDecision struct {
	kind string
	tick int64
}

// TestClockSpeedMatrixDecidesPerTickAndWakesWithinStepInterval drives the
// scheduler and worker against speedNative at tick multipliers 1, 3, 6, 15
// and 150 (one tick every 4ms down to every 27µs). The clock decisions
// (each window admitted, each budget stop settled) must be the same
// sequence per game tick at every multiplier: the decisions follow the
// game's ticks, not the wall clock. And every step a budget stop woke must
// begin within one StepInterval of the native stop: the long poll and the
// wake signal deliver the stop at once instead of waiting out the timer
// cadence, which the step's clock_step row reports as its stop latency
// (issue #112).
func TestClockSpeedMatrixDecidesPerTickAndWakesWithinStepInterval(t *testing.T) {
	t.Parallel()
	const windows = 3
	config := ClockWorkerConfig{PollInterval: 20 * time.Millisecond, RenewInterval: 5 * time.Second, StepInterval: 200 * time.Millisecond, MaxBackoff: 2 * time.Second, PollTimeout: 5 * time.Second, RenewTimeout: 5 * time.Second, StepTimeout: 5 * time.Second, PageLimit: 128, PollWait: 500 * time.Millisecond}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 7}
	var expected []speedDecision
	for _, multiplier := range []int{1, 3, 6, 15, 150} {
		t.Run(fmt.Sprintf("x%d", multiplier), func(t *testing.T) {
			native := newSpeedNative(snapshot, 4*time.Millisecond/time.Duration(multiplier))
			var mu sync.Mutex
			var steps []speedStep
			scheduler, worker := speedMatrixFixture(t, native, snapshot, config, func(step speedStep) {
				mu.Lock()
				steps = append(steps, step)
				mu.Unlock()
			})
			// Run until the third window has stopped and the stop has been
			// settled and answered by a wake step.
			deadline := time.Now().Add(20 * time.Second)
			for time.Now().Before(deadline) {
				mu.Lock()
				settled := 0
				for _, step := range steps {
					if step.reason.Stopped {
						settled++
					}
				}
				mu.Unlock()
				if len(native.speedStops()) >= windows && settled >= windows {
					break
				}
				time.Sleep(5 * time.Millisecond)
			}
			ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			if err := worker.Stop(ctx); err != nil {
				t.Fatal(err)
			}
			stops := native.speedStops()
			if len(stops) < windows {
				t.Fatalf("only %d windows stopped", len(stops))
			}
			mu.Lock()
			defer mu.Unlock()
			// The decisions, keyed by game tick: the windows the journal
			// holds, start tick and deadline in admission order.
			epochs, err := scheduler.player.journal.LoadClockEpochs(context.Background(), 4096)
			if err != nil {
				t.Fatal(err)
			}
			var decisions []speedDecision
			for i, epoch := range epochs {
				if i == windows {
					break
				}
				decisions = append(decisions, speedDecision{"window", epoch.Epoch.GetStartTick()}, speedDecision{"deadline", epoch.Epoch.GetTickDeadline()})
			}
			var woken []speedStep
			for _, step := range steps {
				// A step may be held while a poll's review catches up, and
				// the step in flight when the worker stops is cancelled.
				if step.err != nil && !errors.Is(step.err, executor.ErrHeld) && !errors.Is(step.err, context.Canceled) {
					t.Fatalf("step failed: %v (reason %s)", step.err, step.reason)
				}
				if step.reason.Stopped {
					woken = append(woken, step)
				}
			}
			if len(woken) < windows {
				t.Fatalf("%d wake steps carried a stop, want %d: %+v", len(woken), windows, steps)
			}
			for _, step := range woken[:windows] {
				if step.reason.Cause != StepWake || step.reason.StopAt.IsZero() {
					t.Fatalf("stop reason: %s at %v", step.reason, step.reason.StopAt)
				}
				if latency := step.began.Sub(step.reason.StopAt); latency < 0 || latency > config.StepInterval {
					t.Fatalf("x%d: stop-to-step latency %s exceeds the %s step interval", multiplier, latency, config.StepInterval)
				}
			}
			if len(decisions) != 2*windows {
				t.Fatalf("x%d: %d windows admitted: %+v", multiplier, len(epochs), decisions)
			}
			if expected == nil {
				expected = decisions
				return
			}
			for i := range expected {
				if decisions[i] != expected[i] {
					t.Fatalf("x%d: decision %d %+v differs from x1 %+v", multiplier, i, decisions[i], expected[i])
				}
			}
		})
	}
}
