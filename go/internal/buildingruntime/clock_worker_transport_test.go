package buildingruntime

import (
	"context"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// The native effect happens before the transport stalls. Reads remain available:
// no shared fixture lock is held while the reply ignores cancellation.
type blockedClockTransport struct {
	*joinedClockNative
	blockStart                                           bool
	entered, cancelled, release, renewed                 chan struct{}
	enteredOnce, cancelledOnce, releaseOnce, renewedOnce sync.Once
}

func (f *blockedClockTransport) stall(ctx context.Context) {
	f.enteredOnce.Do(func() { close(f.entered) })
	select {
	case <-ctx.Done():
		f.cancelledOnce.Do(func() { close(f.cancelled) })
		<-f.release
	case <-f.release:
	}
}
func (f *blockedClockTransport) Start(ctx context.Context, r *k.StartRequest) (*k.ControlReply, bridge.Result, error) {
	reply, raw, err := f.joinedClockNative.Start(ctx, r)
	if f.blockStart {
		f.stall(ctx)
	}
	return reply, raw, err
}
func (f *blockedClockTransport) Renew(ctx context.Context, r *k.RenewRequest, epoch *k.Epoch) (*k.ControlReply, bridge.Result, error) {
	reply, raw, err := f.joinedClockNative.Renew(ctx, r, epoch)
	f.renewedOnce.Do(func() { close(f.renewed) })
	if !f.blockStart {
		f.stall(ctx)
	}
	return reply, raw, err
}
func clockTransportWait(t *testing.T, ch <-chan struct{}, label string) {
	t.Helper()
	select {
	case <-ch:
	case <-time.After(8 * time.Second):
		t.Fatal("timed out: " + label)
	}
}

// clockTransportWaitDisabled polls for the session's own local invalidation
// rather than inferring it from the blocked write unblocking. Under
// interruption, the write's context can be cancelled by the worker's own
// per-call deadline racing ahead of the independent poll loop that actually
// discovers and processes the interruption; unlike Manual/Stop (which
// invalidate synchronously before the blocked write can unblock, under the
// same lock), there is no ordering guarantee between the two here.
func clockTransportWaitDisabled(t *testing.T, s *ClockScheduler, label string) {
	t.Helper()
	deadline := time.Now().Add(8 * time.Second)
	for time.Now().Before(deadline) {
		if !s.session.State().Enabled {
			return
		}
		time.Sleep(time.Millisecond)
	}
	t.Fatal("timed out: " + label)
}
func clockTransportFixture(t *testing.T, blockStart bool) (*ClockScheduler, *blockedClockTransport, *ClockWorker) {
	t.Helper()
	s, source := schedulerFixture(t)
	native := &blockedClockTransport{joinedClockNative: &joinedClockNative{source: source, started: make(chan struct{}), paused: make(chan struct{}), captured: make(chan struct{})}, blockStart: blockStart, entered: make(chan struct{}), cancelled: make(chan struct{}), release: make(chan struct{}), renewed: make(chan struct{})}
	s.native = native
	s.session.clock.native = native
	s.session.clock.writer = native
	s.session.control.config.Worlds = clockWorldSource{native}
	worker, err := NewClockWorker(context.Background(), s, native, ClockWorkerConfig{PollInterval: 10 * time.Millisecond, RenewInterval: 50 * time.Millisecond, StepInterval: 10 * time.Millisecond, MaxBackoff: 100 * time.Millisecond, CallTimeout: 200 * time.Millisecond, PageLimit: 128})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		native.releaseOnce.Do(func() { close(native.release) })
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()
		if err := s.session.Close(ctx); err != nil {
			t.Error("cleanup", err)
		}
	})
	return s, native, worker
}
func TestClockWorkerTransportBlockedWriteRetainsOwner(t *testing.T) {
	// Not t.Parallel(): this fixture's assertions depend on the worker's real
	// RenewInterval/CallTimeout timers firing within clockTransportWait's bound.
	// Running alongside the package's other parallel tests under CPU contention
	// starved those timers and produced an intermittent "timed out: dispatched
	// native renew" failure.
	for _, kind := range []string{"start", "renew"} {
		for _, stop := range []string{"manual", "interruption", "stop"} {
			t.Run(kind+"/"+stop, func(t *testing.T) {
				s, native, worker := clockTransportFixture(t, kind == "start")
				clockTransportWait(t, native.entered, "dispatched native "+kind)
				attempts, err := s.player.journal.LoadClockAttempts(context.Background(), 4096)
				if err != nil {
					t.Fatal(err)
				}
				var blocked store.ClockAttempt
				for _, v := range attempts {
					if (kind == "start" && v.Intent.Command.Start != nil) || (kind == "renew" && v.Intent.Command.Renew != nil) {
						blocked = v
					}
				}
				if blocked.Phase != store.ClockDispatched {
					t.Fatal("not durably dispatched", blocked.Phase)
				}
				stopped := make(chan error, 1)
				switch stop {
				case "interruption":
					native.mu.Lock()
					native.alert = true
					native.source.status.NewestCursor = proto.Int64(1)
					native.mu.Unlock()
				case "manual", "stop":
					go func() {
						ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
						defer cancel()
						if stop == "manual" {
							stopped <- s.session.Manual(ctx)
						} else {
							stopped <- worker.Stop(ctx)
						}
					}()
				}
				clockTransportWait(t, native.cancelled, "local invalidation reaches blocked write")
				if stop != "interruption" {
					if err := <-stopped; err == nil {
						t.Fatal("blocked write unexpectedly joined")
					}
					// Manual/Stop invalidate synchronously, under the same lock
					// State() reads, before the blocked write can unblock.
					if s.session.State().Enabled {
						t.Fatal("authority remained enabled")
					}
				} else {
					// The interruption is discovered by the independent poll loop;
					// the write's own per-call deadline can unblock it first.
					clockTransportWaitDisabled(t, s, "authority disabled after interruption")
				}
				closeCtx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
				err = s.session.Close(closeCtx)
				cancel()
				if err == nil {
					t.Fatal("Close released an in-flight transport")
				}
				if owner, err := AcquireProfile(context.Background(), s.config.Profile); err == nil {
					_ = owner.Close()
					t.Fatal("profile released while write blocked")
				}
				native.releaseOnce.Do(func() { close(native.release) })
				ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
				defer cancel()
				if err = s.session.Close(ctx); err != nil {
					t.Fatal("Close retry", err)
				}
				clockTransportWait(t, worker.done, "joined worker")
				saved, err := s.player.journal.LookupClockAttempt(context.Background(), blocked.Intent.RequestID)
				if err != nil || saved.Phase != store.ClockApplied || saved.Reply.GetReceipt() == nil {
					t.Fatal("late receipt not retained", saved.Phase, err)
				}
				epochs, err := s.player.journal.LoadClockEpochs(context.Background(), 4096)
				if err != nil || len(epochs) != 1 || epochs[0].Stage != store.ClockEpochPaused {
					t.Fatal("owned epoch not paused", epochs, err)
				}
				native.mu.Lock()
				pauses := native.source.pauses
				writes := native.source.writes
				native.mu.Unlock()
				expectedWrites := 1
				if kind == "renew" {
					expectedWrites = 2
				}
				if pauses != 1 || writes != expectedWrites {
					t.Fatal("unexpected native effects", pauses, writes)
				}
				owner, err := AcquireProfile(context.Background(), s.config.Profile)
				if err != nil {
					t.Fatal("joined Close retained profile", err)
				}
				_ = owner.Close()
			})
		}
	}
}
func TestClockWorkerTransportRenewalBypassesPlayerGate(t *testing.T) {
	// Not t.Parallel(): same real-timer contention risk as
	// TestClockWorkerTransportBlockedWriteRetainsOwner above.
	s, native, _ := clockTransportFixture(t, false)
	clockTransportWait(t, native.started, "start")
	select {
	case s.player.gate <- struct{}{}:
	case <-time.After(time.Second):
		t.Fatal("player gate")
	}
	held := true
	defer func() {
		if held {
			<-s.player.gate
		}
	}()
	clockTransportWait(t, native.renewed, "renewal while player work holds gate")
	if !s.session.State().Enabled {
		t.Fatal("renewal lost permission")
	}
	native.releaseOnce.Do(func() { close(native.release) })
	// Close cancels the independently waiting scheduler and joins renewal without
	// requiring the long-running player's gate to become available.
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if err := s.session.Close(ctx); err != nil {
		t.Fatal(err)
	}
	<-s.player.gate
	held = false
}
