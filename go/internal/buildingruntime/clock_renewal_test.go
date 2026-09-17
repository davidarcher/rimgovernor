package buildingruntime

import (
	"context"
	"errors"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func TestClockRenewalSafetyReviewMustCatchUp(t *testing.T) {
	t.Parallel()
	s, n, w, _ := renewalFixture(t)
	n.status.NewestCursor = proto.Int64(1)
	if _, err := s.RenewEpoch(context.Background()); err == nil || w.renews != 0 || !s.session.State().Enabled {
		t.Fatal(err, w.renews)
	}
}

func TestClockRenewalDefersCompletedBudgetToScheduler(t *testing.T) {
	t.Parallel()
	for _, unsafe := range []bool{false, true} {
		t.Run(map[bool]string{false: "budget", true: "interruption"}[unsafe], func(t *testing.T) {
			s, n, w, start := renewalFixture(t)
			epoch := proto.Clone(start.Reply.GetReceipt().GetApplied().GetStatus().GetRunning().Epoch).(*k.Epoch)
			epoch.LastTick = proto.Int64(epoch.GetTickDeadline())
			epoch.LeaseRemainingMs = proto.Uint32(0)
			reason := k.StopReason_STOP_REASON_TICK_BUDGET
			if unsafe {
				reason = k.StopReason_STOP_REASON_EXTERNAL_PAUSE
			}
			n.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: reason.Enum(), StoppedAtUnixMs: proto.Int64(100), ActualPaused: proto.Bool(true), PauseRequested: proto.Bool(true), PauseVerified: proto.Bool(true)}}
			n.status.ActualPaused = proto.Bool(true)
			n.status.Context.Tick = proto.Int64(epoch.GetTickDeadline())
			n.status.NewestCursor = proto.Int64(1) // The poller has not captured the stop yet.
			r, err := s.RenewEpoch(context.Background())
			if w.renews != 0 || r.Renewed || (err != nil) != unsafe || s.session.State().Enabled == unsafe {
				t.Fatal(r, err, w.renews, s.session.State())
			}
		})
	}
}

type renewalBoundaryNative struct {
	*schedulerNative
	statusReads int
	beforeRead  func(int) error
}

func (n *renewalBoundaryNative) ReadClockStatus(ctx context.Context, id *c.Identity) (*k.StatusReply, bridge.Result, error) {
	n.statusReads++
	if err := n.beforeRead(n.statusReads); err != nil {
		return nil, bridge.Result{}, err
	}
	return n.schedulerNative.ReadClockStatus(ctx, id)
}

func TestClockRenewalBudgetFinishesDuringPreflight(t *testing.T) {
	t.Parallel()
	for _, reason := range []string{"budget", "interruption", "unknown_boundary", "read_error", "manual", "wrong_epoch"} {
		t.Run(reason, func(t *testing.T) {
			s, n, w, start := renewalFixture(t)
			epoch := proto.Clone(start.Reply.GetReceipt().GetApplied().GetStatus().GetRunning().Epoch).(*k.Epoch)
			native := &renewalBoundaryNative{schedulerNative: n}
			native.beforeRead = func(read int) error {
				if read == 2 {
					epoch.LastTick = proto.Int64(epoch.GetTickDeadline())
					epoch.LeaseRemainingMs = proto.Uint32(0)
					stop := k.StopReason_STOP_REASON_TICK_BUDGET
					if reason == "interruption" {
						stop = k.StopReason_STOP_REASON_EXTERNAL_PAUSE
					}
					n.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: stop.Enum(), StoppedAtUnixMs: proto.Int64(100), ActualPaused: proto.Bool(true), PauseRequested: proto.Bool(true), PauseVerified: proto.Bool(true)}}
					n.status.ActualPaused = proto.Bool(true)
					n.status.Context.Tick = proto.Int64(epoch.GetTickDeadline())
					if reason == "unknown_boundary" {
						n.status.NativeTickBoundary = proto.Bool(false)
					}
					if reason == "wrong_epoch" {
						epoch.Owner.Epoch = proto.Int64(epoch.Owner.GetEpoch() + 1)
					}
				}
				if read == 3 {
					if reason == "read_error" {
						return errors.New("native read unavailable")
					}
					if reason == "manual" {
						return s.session.Disable()
					}
				}
				return nil
			}
			s.native, s.session.clock.native = native, native
			result, err := s.RenewEpoch(context.Background())
			safe := reason == "budget"
			if (err == nil) != safe || s.session.State().Enabled != safe || w.renews != 0 || result.Renewed || result.Attempt == nil || result.Attempt.Phase != store.ClockPrepared {
				t.Fatal(result, err, s.session.State(), w.renews)
			}
			if safe {
				retained, err := s.player.journal.LookupClockAttempt(context.Background(), result.Attempt.Intent.RequestID)
				if err != nil || retained.Phase != store.ClockPrepared {
					t.Fatal(retained, err)
				}
				cleaned, err := s.Step(context.Background())
				if err != nil || !cleaned.Cleaned || !s.session.State().Enabled || w.renews != 0 {
					t.Fatal(cleaned, err)
				}
			}
		})
	}
}

func TestClockRenewalRecoversThenValidatesCurrentRunningEpoch(t *testing.T) {
	t.Parallel()
	s, _, w, start := renewalFixture(t)
	original := start.Reply.GetReceipt().GetApplied().GetStatus().GetRunning().Epoch
	id := clockTestNextID(t, s.player.journal)
	intent := store.ClockIntent{RequestID: id, Snapshot: start.Intent.Snapshot, Command: bridge.ClockCommand{Renew: &bridge.ClockRenew{Original: original, LeaseMS: s.config.Start.LeaseMS}}}
	w.lost = true
	if _, err := s.session.CommandClock(context.Background(), intent); err == nil {
		t.Fatal("missing uncertainty")
	}
	w.lost = false
	r, err := s.RenewEpoch(context.Background())
	if err != nil || !r.Reconciled || r.Renewed || w.renews != 1 {
		t.Fatal(r, err)
	}
}

type renewalWriter struct {
	*clockCoreFake
	renews   int
	observed atomic.Int32 // renews, readable while the worker's renew loop still runs
	lost     bool
	original *k.Epoch
	onRenew  func() (*k.ControlReply, error)
}

func (f *renewalWriter) Renew(ctx context.Context, r *k.RenewRequest, original *k.Epoch) (*k.ControlReply, bridge.Result, error) {
	f.renews++
	f.observed.Add(1)
	f.original = proto.Clone(original).(*k.Epoch)
	if f.onRenew != nil {
		reply, err := f.onRenew()
		return reply, bridge.Result{}, err
	}
	reply, raw, err := f.clockCoreFake.Renew(ctx, r, original)
	if reply != nil {
		f.receipt = proto.Clone(reply.GetReceipt()).(*k.ControlReceipt)
	}
	if f.lost {
		return nil, raw, errors.New("renew reply lost")
	}
	return reply, raw, err
}

func TestClockRenewalBudgetFinishesAfterDispatch(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"budget", "external_pause", "expired", "wrong_epoch", "unknown_boundary", "manual", "different_refusal", "lost_reply"} {
		t.Run(mode, func(t *testing.T) {
			s, n, w, start := renewalFixture(t)
			epoch := proto.Clone(start.Reply.GetReceipt().GetApplied().GetStatus().GetRunning().Epoch).(*k.Epoch)
			w.onRenew = func() (*k.ControlReply, error) {
				epoch.LastTick = proto.Int64(epoch.GetTickDeadline())
				epoch.LeaseRemainingMs = proto.Uint32(0)
				reason := k.StopReason_STOP_REASON_TICK_BUDGET
				if mode == "external_pause" {
					reason = k.StopReason_STOP_REASON_EXTERNAL_PAUSE
				}
				if mode == "expired" {
					reason = k.StopReason_STOP_REASON_LEASE_EXPIRED
				}
				if mode == "wrong_epoch" {
					epoch.Owner.Epoch = proto.Int64(epoch.Owner.GetEpoch() + 1)
				}
				n.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: reason.Enum(), StoppedAtUnixMs: proto.Int64(100), ActualPaused: proto.Bool(true), PauseRequested: proto.Bool(true), PauseVerified: proto.Bool(true)}}
				n.status.ActualPaused = proto.Bool(true)
				n.status.Context.Tick = proto.Int64(epoch.GetTickDeadline())
				if mode == "unknown_boundary" {
					n.status.NativeTickBoundary = proto.Bool(false)
				}
				if mode == "manual" {
					if err := s.session.Disable(); err != nil {
						t.Fatal(err)
					}
				}
				if mode == "lost_reply" {
					return nil, errors.New("renewal reply lost")
				}
				code := c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED
				if mode == "different_refusal" {
					code = c.FailureCode_FAILURE_CODE_OWNER_CONFLICT
				}
				failure := &c.Failure{Code: code.Enum()}
				return &k.ControlReply{Outcome: &k.ControlReply_Failure{Failure: failure}}, &bridge.NativeFailure{Value: failure}
			}
			result, err := s.RenewEpoch(context.Background())
			safe := mode == "budget"
			if (err == nil) != safe || s.session.State().Enabled != safe || w.renews != 1 || result.Renewed || result.Attempt == nil {
				t.Fatal(mode, result, err, s.session.State(), w.renews)
			}
			if safe {
				if result.Attempt.Phase != store.ClockRefused {
					t.Fatal(result)
				}
				retained, err := s.player.journal.LookupClockAttempt(context.Background(), result.Attempt.Intent.RequestID)
				if err != nil || retained.Phase != store.ClockRefused {
					t.Fatal(retained, err)
				}
				cleaned, err := s.Step(context.Background())
				if err != nil || !cleaned.Cleaned || !s.session.State().Enabled || w.renews != 1 {
					t.Fatal(cleaned, err, s.session.State())
				}
			}
		})
	}
}
func renewalFixture(t *testing.T) (*ClockScheduler, *schedulerNative, *renewalWriter, store.ClockAttempt) {
	t.Helper()
	s, n := schedulerFixture(t)
	start, err := s.Step(context.Background())
	if err != nil || start.Attempt == nil {
		t.Fatal(start, err)
	}
	w := &renewalWriter{clockCoreFake: n.clockCoreFake}
	s.session.clock.writer = w
	return s, n, w, *start.Attempt
}

func TestClockRenewalOriginalDeadlineAndIndependentPlayerGate(t *testing.T) {
	t.Parallel()
	s, n, w, start := renewalFixture(t)
	original := start.Reply.GetReceipt().GetApplied().GetStatus().GetRunning().Epoch
	n.status.Context.Tick = proto.Int64(20)
	n.status.GetRunning().Epoch.LastTick = proto.Int64(20)
	s.player.gate <- struct{}{}
	defer func() { <-s.player.gate }()
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	r, err := s.RenewEpoch(ctx)
	if err != nil || !r.Renewed || w.renews != 1 || !proto.Equal(w.original, original) {
		t.Fatal(r, err, w.renews)
	}
	if r.Attempt.Intent.Command.Renew.Original.GetTickDeadline() != original.GetTickDeadline() || n.writes != 2 {
		t.Fatal("renew changed original window")
	}
}

// A step that outlives the lease-bound renew budget must not stall renewal:
// Step holds the Player gate for its whole planner census, RenewEpoch only
// takes renewGate. The worker's step budget is independent of the lease
// (issue #73), so the loop-level split is exercised against the real
// scheduler here with a step blocked inside the Player gate.
func TestClockRenewalContinuesDuringSlowStep(t *testing.T) {
	t.Parallel()
	s, _, w, _ := renewalFixture(t)
	ctx, cancel := context.WithCancel(context.Background())
	stepEntered, stepReleased := make(chan struct{}), make(chan struct{})
	worker := &ClockWorker{ctx: ctx, cancel: cancel, config: ClockWorkerConfig{PollInterval: time.Hour, RenewInterval: 5 * time.Millisecond, StepInterval: time.Hour, MaxBackoff: time.Hour, PollTimeout: 50 * time.Millisecond, RenewTimeout: 50 * time.Millisecond, StepTimeout: 10 * time.Second}, done: make(chan struct{}), ready: make(chan struct{}), stopGate: make(chan struct{}, 1), disable: func() error { return nil }, cleanup: func(context.Context) error { return nil }, renew: s.RenewEpoch}
	worker.poll = func(context.Context) (ClockPollResult, error) { return ClockPollResult{}, nil }
	worker.step = func(ctx context.Context) (ClockSchedulerResult, error) {
		_, _, done, err := s.player.enter(ctx, false)
		if err != nil {
			return ClockSchedulerResult{}, err
		}
		defer done()
		close(stepEntered)
		select {
		case <-stepReleased:
		case <-ctx.Done():
		}
		return ClockSchedulerResult{}, ctx.Err()
	}
	worker.start()
	t.Cleanup(func() {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), time.Second)
		defer stopCancel()
		if err := worker.Stop(stopCtx); err != nil {
			t.Error(err)
		}
	})
	select {
	case <-stepEntered:
	case <-time.After(time.Second):
		t.Fatal("step never entered the player gate")
	}
	deadline := time.After(2 * time.Second)
	for w.observed.Load() < 3 {
		select {
		case <-deadline:
			t.Fatal("renewals stalled behind the slow step", w.observed.Load())
		case <-time.After(5 * time.Millisecond):
		}
	}
	select {
	case <-worker.done:
		t.Fatal("step released before renewals were observed")
	default:
	}
	close(stepReleased)
}

func TestClockRenewalReusesPreparedDespiteFreshTick(t *testing.T) {
	t.Parallel()
	s, n, w, start := renewalFixture(t)
	original := start.Reply.GetReceipt().GetApplied().GetStatus().GetRunning().Epoch
	id := clockTestNextID(t, s.player.journal)
	intent := store.ClockIntent{RequestID: id, Snapshot: start.Intent.Snapshot, Command: bridge.ClockCommand{Renew: &bridge.ClockRenew{Original: original, LeaseMS: s.config.Start.LeaseMS}}}
	prepared, _, err := s.player.journal.PrepareClock(context.Background(), intent)
	if err != nil {
		t.Fatal(err)
	}
	n.status.Context.Tick = proto.Int64(20)
	n.status.GetRunning().Epoch.LastTick = proto.Int64(20)
	r, err := s.RenewEpoch(context.Background())
	if err != nil || !r.Renewed || r.Attempt.Intent.RequestID != id || !proto.Equal(r.Attempt.NativeAttempt, prepared.NativeAttempt) || w.renews != 1 {
		t.Fatal(r, err)
	}
}

func TestClockRenewalTerminalUnknownIsIdleWhileDisabled(t *testing.T) {
	t.Parallel()
	s, _, w, _ := renewalFixture(t)
	w.lost = true
	first, err := s.RenewEpoch(context.Background())
	if err == nil || first.Attempt == nil || first.Attempt.Phase != store.ClockUncertain || w.renews != 1 || s.session.State().Enabled {
		t.Fatal(first, err)
	}
	second, err := s.RenewEpoch(context.Background())
	if err != nil || second.Reconciled || second.Attempt != nil || w.renews != 1 {
		t.Fatal(second, err, w.renews)
	}
	old, err := s.player.journal.LookupClockAttempt(context.Background(), first.Attempt.Intent.RequestID)
	if err != nil || old.Phase != store.ClockUncertain {
		t.Fatal(old, err)
	}
	reads, pauses := w.reads, w.pauses
	w.identityError = errors.New("idle must not read native")
	defer func() { w.identityError = nil }()
	if _, err = s.RenewEpoch(context.Background()); err != nil || w.reads != reads || w.pauses != pauses {
		t.Fatal("idle cleanup repeated", err)
	}
}

func TestClockRenewalOldTerminalUncertaintyDoesNotBlockNewEpoch(t *testing.T) {
	t.Parallel()
	s, n, w, start := renewalFixture(t)
	w.lost = true
	old, err := s.RenewEpoch(context.Background())
	if err == nil || old.Attempt == nil || old.Attempt.Phase != store.ClockUncertain {
		t.Fatal(old, err)
	}
	w.lost = false
	if err = s.session.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	current, err := s.session.Acquire(context.Background(), start.Intent.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	n.status.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
	n.status.DurableEvents = proto.Bool(true)
	next, err := s.Step(context.Background())
	if err != nil || next.Attempt == nil || next.Attempt.Phase != store.ClockApplied {
		t.Fatal(next, err)
	}
	renewed, err := s.RenewEpoch(context.Background())
	if err != nil || !renewed.Renewed || renewed.Reconciled || renewed.Attempt.Intent.Snapshot != current || w.renews != 2 {
		t.Fatal(renewed, err, w.renews)
	}
	retained, err := s.player.journal.LookupClockAttempt(context.Background(), old.Attempt.Intent.RequestID)
	if err != nil || retained.Phase != store.ClockUncertain {
		t.Fatal("old uncertainty overwritten", retained, err)
	}
}

func TestClockRenewalDisabledOrReplacementNeverRenews(t *testing.T) {
	t.Parallel()
	for _, reason := range []string{"disabled", "owner", "world", "deadline", "speed"} {
		t.Run(reason, func(t *testing.T) {
			s, n, w, _ := renewalFixture(t)
			original := proto.Clone(n.status.GetRunning().Epoch).(*k.Epoch)
			defer func() {
				if reason == "deadline" || reason == "speed" {
					n.status.State = &k.Status_Running{Running: &k.Running{Epoch: original}}
				}
			}()
			switch reason {
			case "disabled":
				if err := s.session.Disable(); err != nil {
					t.Fatal(err)
				}
			case "owner":
				n.status.GetRunning().Epoch.Owner.Epoch = proto.Int64(9)
			case "world":
				n.status.Context.Identity.LoadToken = proto.String("replacement")
			case "deadline":
				n.status.GetRunning().Epoch.TickDeadline = proto.Int64(200)
			case "speed":
				n.status.GetRunning().Epoch.RequestedSpeed = k.Speed_SPEED_FAST.Enum()
			}
			if _, err := s.RenewEpoch(context.Background()); err == nil || w.renews != 0 || s.session.State().Enabled {
				t.Fatal(reason, err, w.renews)
			}
		})
	}
}

func TestClockRenewalConcurrentCallsHaveDistinctDurableIdentities(t *testing.T) {
	t.Parallel()
	s, _, w, _ := renewalFixture(t)
	results := make(chan ClockRenewResult, 2)
	errs := make(chan error, 2)
	var group sync.WaitGroup
	for range 2 {
		group.Add(1)
		go func() { defer group.Done(); r, e := s.RenewEpoch(context.Background()); results <- r; errs <- e }()
	}
	group.Wait()
	close(results)
	close(errs)
	for err := range errs {
		if err != nil {
			t.Fatal(err)
		}
	}
	ids := map[string]bool{}
	for r := range results {
		if !r.Renewed || r.Attempt == nil || ids[r.Attempt.Intent.RequestID] {
			t.Fatal(r)
		}
		ids[r.Attempt.Intent.RequestID] = true
	}
	if w.renews != 2 || len(ids) != 2 {
		t.Fatal(w.renews, ids)
	}
}
