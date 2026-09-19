package buildingruntime

import (
	"context"
	"errors"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

type controlSink struct {
	mu      sync.Mutex
	value   executor.Authority
	changes chan bool
}

func (s *controlSink) UpdateAuthority(v executor.Authority) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.value = v
	select {
	case s.changes <- v.Enabled:
	default:
	}
	return nil
}
func (s *controlSink) enabled() bool { s.mu.Lock(); defer s.mu.Unlock(); return s.value.Enabled }

// controlNative fakes the native side's Mode+generation authority (see #52):
// there is no session/lease negotiation any more, just an Active/Inactive Mode
// and a generation counter that bumps on every SetMode/Revoke.
type controlNative struct {
	mu         sync.Mutex
	generation uint64
	active     bool
	// renews stays at zero: there is no renewal handshake any more (see #52),
	// but it is kept so sibling test files that assert "renew never happened"
	// still compile and hold.
	acquires, renews, revokes atomic.Int32
	onGrant                   func(context.Context, *a.ControlReply) error
	onRevoke                  func()
	onRead                    func(context.Context) error
}

func controlScope() domain.GenerationSnapshot {
	return domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "plan", Revision: 1}
}
func (n *controlNative) ReadAuthority(ctx context.Context, id *c.Identity) (*a.StatusReply, bridge.Result, error) {
	if n.onRead != nil {
		if err := n.onRead(ctx); err != nil {
			return nil, bridge.Result{}, err
		}
	}
	n.mu.Lock()
	defer n.mu.Unlock()
	status := &a.Status{Context: &c.ObservationContext{Identity: proto.Clone(id).(*c.Identity), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(n.generation)}}
	if n.active {
		status.State = &a.Status_Active{Active: &a.ActiveAuthority{Mode: a.Mode_MODE_AUTO.Enum()}}
	} else {
		status.State = &a.Status_Inactive{Inactive: &a.InactiveAuthority{Reason: a.RevocationReason_REVOCATION_REASON_NONE.Enum()}}
	}
	return &a.StatusReply{Outcome: &a.StatusReply_Status{Status: status}}, bridge.Result{}, nil
}
func (n *controlNative) SetMode(ctx context.Context, r *a.SetMode) (*a.ControlReply, bridge.Result, error) {
	n.acquires.Add(1)
	n.mu.Lock()
	n.generation++
	n.active = r.GetMode() == a.Mode_MODE_AUTO
	reply := &a.ControlReply{Outcome: &a.ControlReply_Granted{Granted: &a.Granted{
		Context:   &c.ObservationContext{Identity: proto.Clone(r.Identity).(*c.Identity), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(n.generation)},
		Authority: &a.ActiveAuthority{Mode: a.Mode_MODE_AUTO.Enum()},
	}}}
	n.mu.Unlock()
	if n.onGrant != nil {
		if err := n.onGrant(ctx, reply); err != nil {
			return nil, bridge.Result{}, err
		}
	}
	return reply, bridge.Result{}, nil
}
func (n *controlNative) Revoke(ctx context.Context, r *a.Revoke) (*a.ControlReply, bridge.Result, error) {
	n.revokes.Add(1)
	if n.onRevoke != nil {
		n.onRevoke()
	}
	n.mu.Lock()
	defer n.mu.Unlock()
	n.generation++
	n.active = false
	return &a.ControlReply{Outcome: &a.ControlReply_Revoked{Revoked: &a.Revoked{
		Context:   &c.ObservationContext{Identity: proto.Clone(r.Identity).(*c.Identity), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(n.generation)},
		Authority: &a.InactiveAuthority{Reason: r.Reason},
	}}}, bridge.Result{}, nil
}
func controlFixture(t *testing.T, stop func(context.Context) error) (*Control, *controlNative, *controlSink, string) {
	t.Helper()
	dir := t.TempDir()
	db, err := store.Open(context.Background(), storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	n := &controlNative{generation: 1}
	sink := &controlSink{changes: make(chan bool, 16)}
	if stop == nil {
		stop = func(context.Context) error { return nil }
	}
	control, err := NewControl(context.Background(), ControlConfig{ProfileDirectory: dir, CallTimeout: 5 * time.Second, StopWrites: stop}, db, n, sink)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { control.Close(context.Background()) })
	return control, n, sink, dir
}
func TestControlOwnsProfileAndExplicitLease(t *testing.T) {
	t.Parallel()
	control, n, sink, dir := controlFixture(t, nil)
	if other, err := AcquireProfile(context.Background(), dir); err == nil {
		other.Close()
		t.Fatal("competing owner admitted")
	}
	requested := controlScope()
	requested.Native = 999
	snapshot, err := control.Acquire(context.Background(), requested)
	if err != nil {
		t.Fatal(err)
	}
	if snapshot.Native != 2 || !sink.enabled() {
		t.Fatal("fresh native generation not installed")
	}
	if lease, err := control.Lease(snapshot); err != nil || lease == "" {
		t.Fatal(lease, err)
	}
	wrong := snapshot
	wrong.Native++
	if _, err := control.Lease(wrong); err == nil {
		t.Fatal("wrong direction got lease")
	}
	n.onRevoke = func() {
		if sink.enabled() {
			t.Error("revoke started before local invalidation")
		}
	}
	if err := control.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, err := control.Lease(snapshot); err == nil {
		t.Fatal("manual retained lease")
	}
	if err := control.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	other, err := AcquireProfile(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	other.Close()
}
func TestControlUncertainAcquireRequiresObservationAndNeverAdopts(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	n.onGrant = func(context.Context, *a.ControlReply) error {
		return &bridge.AuthorityUncertain{Cause: errors.New("reply lost")}
	}
	if _, err := control.Acquire(context.Background(), controlScope()); err == nil || sink.enabled() {
		t.Fatal("lost response enabled writes")
	}
	// The native side already committed the mode change before the reply was
	// lost, so a second attempt observes Active and must not request another
	// grant (no adoption of an unconfirmed change).
	if _, err := control.Acquire(context.Background(), controlScope()); err == nil || n.acquires.Load() != 1 {
		t.Fatal("uncertain acquisition retried/adopted")
	}
	if err := control.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	if n.revokes.Load() != 1 {
		t.Fatal("uncertain acquire not reconciled")
	}
}
func TestControlRejectsBadGrants(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"mode", "generation"} {
		t.Run(kind, func(t *testing.T) {
			control, n, sink, _ := controlFixture(t, nil)
			n.onGrant = func(_ context.Context, r *a.ControlReply) error {
				switch kind {
				case "mode":
					r.GetGranted().Authority.Mode = a.Mode_MODE_MANUAL.Enum()
				case "generation":
					r.GetGranted().Context.NativeGeneration = proto.Uint64(77)
				}
				return nil
			}
			if _, err := control.Acquire(context.Background(), controlScope()); err == nil || sink.enabled() {
				t.Fatal("invalid grant enabled writes")
			}
		})
	}
}
func TestControlCloseRetainsLockUntilWritersDrain(t *testing.T) {
	t.Parallel()
	var draining atomic.Bool
	control, _, sink, dir := controlFixture(t, func(context.Context) error {
		if !draining.Load() {
			return errors.New("writer still active")
		}
		return nil
	})
	if _, err := control.Acquire(context.Background(), controlScope()); err != nil {
		t.Fatal(err)
	}
	if err := control.Close(context.Background()); err == nil || sink.enabled() {
		t.Fatal("failed drain released authority")
	}
	if other, err := AcquireProfile(context.Background(), dir); err == nil {
		other.Close()
		t.Fatal("failed drain released lock")
	}
	draining.Store(true)
	if err := control.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	other, err := AcquireProfile(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	other.Close()
}

func TestControlRefreshRetainsDisabledCurrentGeneration(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	if _, err := control.Acquire(context.Background(), controlScope()); err != nil {
		t.Fatal(err)
	}
	if err := control.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	sink.mu.Lock()
	value := sink.value
	sink.mu.Unlock()
	if value.Enabled || value.Snapshot.Native != 3 {
		t.Fatal("revoke generation lost", value)
	}
	n.mu.Lock()
	n.generation = 4
	n.mu.Unlock()
	if err := control.Refresh(context.Background()); err != nil {
		t.Fatal(err)
	}
	sink.mu.Lock()
	value = sink.value
	sink.mu.Unlock()
	if value.Enabled || value.Snapshot.Native != 4 || n.acquires.Load() != 1 {
		t.Fatal("read refresh adopted authority", value)
	}
}

func TestControlObserveTargetRestartsWithoutAcquiringOrAdopting(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	n.mu.Lock()
	n.generation = 7
	n.active = true
	n.mu.Unlock()
	requested := controlScope()
	requested.Native = 2
	if err := control.ObserveTarget(context.Background(), requested); err != nil {
		t.Fatal(err)
	}
	sink.mu.Lock()
	value := sink.value
	sink.mu.Unlock()
	if value.Enabled || value.Snapshot.Native != 7 || value.Snapshot.Plan != requested.Plan || value.Snapshot.Revision != requested.Revision {
		t.Fatal("restart read invented authority", value)
	}
	if _, err := control.Lease(value.Snapshot); err == nil {
		t.Fatal("restart adopted native lease")
	}
	if n.acquires.Load() != 0 || n.revokes.Load() != 0 {
		t.Fatal("restart observation mutated authority")
	}
	n.mu.Lock()
	n.active = false
	n.mu.Unlock()
	if err := control.ObserveTarget(context.Background(), requested); err != nil {
		t.Fatal("inactive observation", err)
	}
	if n.acquires.Load() != 0 || n.revokes.Load() != 0 {
		t.Fatal("inactive observation wrote")
	}
	if _, err := control.Acquire(context.Background(), requested); err != nil {
		t.Fatal(err)
	}
	if err := control.ObserveTarget(context.Background(), requested); err == nil {
		t.Fatal("observation replaced live lease")
	}
}

func TestControlObserveTargetFailureDoesNotPublishRequestedGeneration(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	n.onRead = func(context.Context) error { return errors.New("unavailable") }
	requested := controlScope()
	requested.Native = 999
	if err := control.ObserveTarget(context.Background(), requested); err == nil {
		t.Fatal("missing read failure")
	}
	sink.mu.Lock()
	value := sink.value
	sink.mu.Unlock()
	if value.Enabled || value.Snapshot.Native != 0 || control.haveTarget {
		t.Fatal("failed read fabricated target", value)
	}
}

func TestControlFailedObservationClearsSeededTargetButRetainsCleanup(t *testing.T) {
	t.Parallel()
	for _, operation := range []string{"refresh", "retarget", "malformed", "timeout"} {
		t.Run(operation, func(t *testing.T) {
			control, native, sink, _ := controlFixture(t, nil)
			requested := controlScope()
			if err := control.ObserveTarget(context.Background(), requested); err != nil {
				t.Fatal(err)
			}
			original := control.snapshot
			native.onRead = func(ctx context.Context) error {
				if operation == "timeout" {
					<-ctx.Done()
					return ctx.Err()
				}
				return errors.New("native read failed")
			}
			if operation == "malformed" {
				native.onRead = nil
				native.generation = 0
			}
			call, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
			defer cancel()
			var err error
			if operation == "retarget" {
				requested.Load = "replacement"
				err = control.ObserveTarget(call, requested)
			} else {
				err = control.Refresh(call)
			}
			if err == nil {
				t.Fatal("failed observation accepted")
			}
			sink.mu.Lock()
			value := sink.value
			sink.mu.Unlock()
			if value != (executor.Authority{}) || !control.haveTarget || control.snapshot != original {
				t.Fatal("stale publication or lost cleanup identity", value, control.snapshot)
			}
			native.onRead = nil
			native.generation = uint64(original.Native)
			native.active = true
			if err = control.Manual(context.Background()); err != nil {
				t.Fatal("cleanup retry failed", err)
			}
			if native.revokes.Load() != 1 || control.snapshot.Load != original.Load {
				t.Fatal("cleanup did not retain original scope")
			}
			sink.mu.Lock()
			value = sink.value
			sink.mu.Unlock()
			if value.Enabled || value.Snapshot.Native != original.Native+1 || value.Snapshot.Load != original.Load {
				t.Fatal("fresh revoke observation missing", value)
			}
		})
	}
}

func TestControlAcquireSupersedesBlockedObserveTarget(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	entered := make(chan struct{})
	var reads atomic.Int32
	n.onRead = func(ctx context.Context) error {
		if reads.Add(1) == 1 {
			close(entered)
			<-ctx.Done()
			return ctx.Err()
		}
		return nil
	}
	done := make(chan error, 1)
	go func() { done <- control.ObserveTarget(context.Background(), controlScope()) }()
	<-entered
	snapshot, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	select {
	case err := <-done:
		if err == nil {
			t.Fatal("superseded read succeeded")
		}
	case <-time.After(time.Second):
		t.Fatal("superseded read did not stop")
	}
	if lease, err := control.Lease(snapshot); err != nil || lease == "" || !sink.enabled() {
		t.Fatal("old read disabled new acquisition", err)
	}
}

// A killed controller leaves native in Auto. The next process, which has
// never targeted the world, reclaims it with one revoke at the observed
// generation and a fresh grant; a process that already targeted the world
// still refuses an unexpected Active (its own uncertain grant).
func TestControlAcquireReclaimsStaleAutoFromDeadProcess(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	n.mu.Lock()
	n.generation = 7
	n.active = true
	n.mu.Unlock()
	granted, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	if n.revokes.Load() != 1 || n.acquires.Load() != 1 || !sink.enabled() || granted.Native != 9 {
		t.Fatal("stale auto not reclaimed", n.revokes.Load(), n.acquires.Load(), granted)
	}
	if _, err := control.Lease(granted); err != nil {
		t.Fatal(err)
	}
	if err := control.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	// Now the process has targeted the world: an Active it did not grant is
	// not reclaimed.
	n.mu.Lock()
	n.active = true
	n.mu.Unlock()
	if _, err := control.Acquire(context.Background(), controlScope()); !errors.Is(err, ErrControl) || n.revokes.Load() != 2 || n.acquires.Load() != 1 {
		t.Fatal("targeted process reclaimed foreign auto", err, n.revokes.Load(), n.acquires.Load())
	}
}

// A Disable from outside the gate (the clock poll on a gap, an interrupting
// event or a pending hold) cancels the epoch an Acquire's status read runs
// under. Nothing has been written yet, so the read starts over under the next
// epoch instead of reporting an uncertain outcome (#206); a caller's own
// cancellation still ends it.
// The clock poll ingests the AuthorityChanged event Manual's own revoke
// raises and calls Disable on it; Disable replaces the control epoch.
// Manual's owned cleanup must still run under a live call (#322).
func TestControlManualSurvivesConcurrentDisable(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	if _, err := control.Acquire(context.Background(), controlScope()); err != nil {
		t.Fatal(err)
	}
	n.onRevoke = func() {
		if err := control.Disable(); err != nil {
			t.Error(err)
		}
	}
	var cleanups atomic.Int32
	var cleanupErr error
	control.config.CleanupWrites = func(ctx context.Context) error {
		cleanups.Add(1)
		// The epoch's cancellation reaches an epoch-bound call through
		// AfterFunc; give it time to land before judging the call live.
		select {
		case <-ctx.Done():
		case <-time.After(200 * time.Millisecond):
		}
		cleanupErr = ctx.Err()
		return cleanupErr
	}
	if err := control.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	if cleanups.Load() != 1 || cleanupErr != nil || sink.enabled() || n.revokes.Load() != 1 {
		t.Fatal("cleanup did not run under a live call", cleanups.Load(), cleanupErr, n.revokes.Load())
	}
	state := control.State()
	if state.Enabled || !state.ObservationKnown || state.Snapshot.Native != 3 {
		t.Fatal("revoked generation not published", state)
	}
	n.onRevoke, control.config.CleanupWrites = nil, nil
}

func TestControlAcquireRestartsObservationAfterConcurrentDisable(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	var reads atomic.Int32
	n.onRead = func(ctx context.Context) error {
		if reads.Add(1) == 1 {
			if err := control.Disable(); err != nil {
				return err
			}
			<-ctx.Done()
			return ctx.Err()
		}
		return nil
	}
	granted, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	if reads.Load() != 2 || n.acquires.Load() != 1 || !sink.enabled() || granted.Native != 2 {
		t.Fatal("observation not restarted once", reads.Load(), n.acquires.Load(), granted)
	}
	if err := control.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	// A disable on every read is a persistent interruption: bounded retries,
	// then the failure.
	reads.Store(0)
	n.onRead = func(ctx context.Context) error {
		reads.Add(1)
		if err := control.Disable(); err != nil {
			return err
		}
		<-ctx.Done()
		return ctx.Err()
	}
	if _, err := control.Acquire(context.Background(), controlScope()); !errors.Is(err, context.Canceled) || reads.Load() != acquireObservationRetries+1 || n.acquires.Load() != 1 {
		t.Fatal("persistent interruption not bounded", err, reads.Load(), n.acquires.Load())
	}
	// The caller's own cancellation is never retried.
	reads.Store(0)
	ctx, cancel := context.WithCancel(context.Background())
	n.onRead = func(ctx context.Context) error { reads.Add(1); cancel(); <-ctx.Done(); return ctx.Err() }
	if _, err := control.Acquire(ctx, controlScope()); !errors.Is(err, context.Canceled) || reads.Load() != 1 {
		t.Fatal("caller cancellation retried", err, reads.Load())
	}
	n.onRead = nil
}

// A grant this process accepted and disabled locally (a clock hold) is
// re-acquired in place: one SetMode at the held generation, no revoke, one
// generation advanced (#259). An observed revocation clears the held grant.
func TestControlAcquireReacquiresOwnHeldGrantInOneGeneration(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	granted, err := control.Acquire(context.Background(), controlScope())
	if err != nil || granted.Native != 2 || !control.HoldsGrant(controlScope()) {
		t.Fatal("first acquire", err, granted)
	}
	if err := control.Disable(); err != nil {
		t.Fatal(err)
	}
	other := controlScope()
	other.Load = "other-load"
	if sink.enabled() || !control.HoldsGrant(controlScope()) || control.HoldsGrant(other) {
		t.Fatal("disable dropped the held grant, or another world matched it")
	}
	// The worker retargets reads under a hold: observing another plan in
	// the same world at the held generation keeps the grant.
	retarget := controlScope()
	retarget.Plan, retarget.Revision = "routine-plan", 3
	if err := control.ObserveTarget(context.Background(), retarget); err != nil || !control.HoldsGrant(controlScope()) {
		t.Fatal("observing another plan dropped the held grant", err)
	}
	again, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	if again.Native != 3 || n.revokes.Load() != 0 || n.acquires.Load() != 2 || !sink.enabled() {
		t.Fatal("held grant not re-acquired in place", again, n.revokes.Load(), n.acquires.Load())
	}
	if err := control.Manual(context.Background()); err != nil || n.revokes.Load() != 1 || control.HoldsGrant(controlScope()) {
		t.Fatal("manual kept the grant", err, n.revokes.Load())
	}
	// Native Active at a generation this process never accepted is not its grant.
	n.mu.Lock()
	n.generation++
	n.active = true
	n.mu.Unlock()
	if _, err := control.Acquire(context.Background(), controlScope()); !errors.Is(err, ErrControl) || n.acquires.Load() != 2 {
		t.Fatal("foreign generation re-acquired", err, n.acquires.Load())
	}
}
