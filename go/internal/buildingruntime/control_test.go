package buildingruntime

import (
	"context"
	"errors"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/runtimeowner"
	"github.com/davidarcher/RimGovernor/go/internal/store"
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

type controlNative struct {
	mu                        sync.Mutex
	generation                uint64
	owner                     *a.Owner
	acquires, renews, revokes atomic.Int32
	onGrant                   func(context.Context, string, *a.ControlReply) error
	onRevoke                  func()
	onRead                    func(context.Context) error
}

func controlScope() domain.GenerationSnapshot {
	return domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "plan", Revision: 1, Direction: 3}
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
	if n.owner == nil {
		status.State = &a.Status_Inactive{Inactive: &a.InactiveAuthority{Reason: a.RevocationReason_REVOCATION_REASON_NONE.Enum()}}
	} else {
		status.State = &a.Status_Active{Active: &a.ActiveAuthority{Owner: proto.Clone(n.owner).(*a.Owner), RemainingLeaseMs: proto.Uint32(1000)}}
	}
	return &a.StatusReply{Outcome: &a.StatusReply_Status{Status: status}}, bridge.Result{}, nil
}
func (n *controlNative) grant(id *c.Identity, owner *a.Owner, lease string) *a.ControlReply {
	return &a.ControlReply{Outcome: &a.ControlReply_Granted{Granted: &a.Granted{Context: &c.ObservationContext{Identity: proto.Clone(id).(*c.Identity), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(n.generation)}, Authority: &a.ActiveAuthority{Owner: proto.Clone(owner).(*a.Owner), RemainingLeaseMs: proto.Uint32(1000)}, LeaseId: proto.String(lease)}}}
}
func (n *controlNative) Acquire(ctx context.Context, r *a.Acquire) (*a.ControlReply, bridge.Result, error) {
	n.acquires.Add(1)
	n.mu.Lock()
	n.generation++
	n.owner = proto.Clone(r.Owner).(*a.Owner)
	reply := n.grant(r.Identity, r.Owner, "lease")
	n.mu.Unlock()
	if n.onGrant != nil {
		if err := n.onGrant(ctx, "acquire", reply); err != nil {
			return nil, bridge.Result{}, err
		}
	}
	return reply, bridge.Result{}, nil
}
func (n *controlNative) Renew(ctx context.Context, r *a.Renew, owner *a.Owner) (*a.ControlReply, bridge.Result, error) {
	n.renews.Add(1)
	n.mu.Lock()
	reply := n.grant(r.Identity, owner, r.GetLeaseId())
	n.mu.Unlock()
	if n.onGrant != nil {
		if err := n.onGrant(ctx, "renew", reply); err != nil {
			return nil, bridge.Result{}, err
		}
	}
	return reply, bridge.Result{}, nil
}
func (n *controlNative) Revoke(_ context.Context, r *a.Revoke) (*a.ControlReply, bridge.Result, error) {
	n.revokes.Add(1)
	if n.onRevoke != nil {
		n.onRevoke()
	}
	n.mu.Lock()
	defer n.mu.Unlock()
	n.generation++
	n.owner = nil
	return &a.ControlReply{Outcome: &a.ControlReply_Revoked{Revoked: &a.Revoked{Context: &c.ObservationContext{Identity: proto.Clone(r.Identity).(*c.Identity), Tick: proto.Int64(10), NativeGeneration: proto.Uint64(n.generation)}, Authority: &a.InactiveAuthority{Reason: r.Reason}}}}, bridge.Result{}, nil
}
func controlFixture(t *testing.T, stop func(context.Context) error) (*Control, *controlNative, *controlSink, string) {
	t.Helper()
	dir := t.TempDir()
	db, err := store.Open(context.Background(), filepath.Join(dir, "state.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	n := &controlNative{generation: 1}
	sink := &controlSink{changes: make(chan bool, 16)}
	if stop == nil {
		stop = func(context.Context) error { return nil }
	}
	control, err := NewControl(context.Background(), ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second, StopWrites: stop}, db, n, sink)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { control.Close(context.Background()) })
	return control, n, sink, dir
}
func TestControlOwnsProfileAndExplicitLease(t *testing.T) {
	t.Parallel()
	control, n, sink, dir := controlFixture(t, nil)
	if other, err := runtimeowner.Acquire(context.Background(), dir); err == nil {
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
	if lease, err := control.Lease(snapshot); err != nil || lease != "lease" {
		t.Fatal(lease, err)
	}
	wrong := snapshot
	wrong.Direction++
	if _, err := control.Lease(wrong); err == nil {
		t.Fatal("wrong direction got lease")
	}
	if err := control.Renew(context.Background()); err != nil {
		t.Fatal(err)
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
	if err := control.Renew(context.Background()); err == nil || n.renews.Load() != 1 {
		t.Fatal("renew reacquired")
	}
	if err := control.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	other, err := runtimeowner.Acquire(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	other.Close()
}
func TestControlUncertainAcquireRequiresObservationAndNeverAdopts(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	n.onGrant = func(context.Context, string, *a.ControlReply) error {
		return &bridge.AuthorityUncertain{Cause: errors.New("reply lost")}
	}
	if _, err := control.Acquire(context.Background(), controlScope()); err == nil || sink.enabled() {
		t.Fatal("lost response enabled writes")
	}
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
func TestControlRejectsBadGrantsAndConservativeDeadline(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"owner", "generation", "expired"} {
		t.Run(kind, func(t *testing.T) {
			control, n, sink, _ := controlFixture(t, nil)
			n.onGrant = func(_ context.Context, _ string, r *a.ControlReply) error {
				switch kind {
				case "owner":
					r.GetGranted().Authority.Owner.PlayerDirection = proto.Uint64(99)
				case "generation":
					r.GetGranted().Context.NativeGeneration = proto.Uint64(77)
				case "expired":
					r.GetGranted().Authority.RemainingLeaseMs = proto.Uint32(1)
					time.Sleep(3 * time.Millisecond)
				}
				return nil
			}
			if _, err := control.Acquire(context.Background(), controlScope()); err == nil || sink.enabled() {
				t.Fatal("invalid/expired grant enabled writes")
			}
		})
	}
}
func TestControlManualCancelsActiveAndQueuedRenew(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	snapshot, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	entered := make(chan struct{})
	n.onGrant = func(ctx context.Context, kind string, _ *a.ControlReply) error {
		if kind == "renew" {
			close(entered)
			<-ctx.Done()
			return ctx.Err()
		}
		return nil
	}
	done := make(chan error, 2)
	go func() { done <- control.Renew(context.Background()) }()
	<-entered
	queued, cancel := context.WithCancel(context.Background())
	cancel()
	go func() { done <- control.Renew(queued) }()
	if err := control.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 2; i++ {
		select {
		case err := <-done:
			if err == nil {
				t.Fatal("cancelled renew succeeded")
			}
		case <-time.After(time.Second):
			t.Fatal("renew did not stop")
		}
	}
	if n.renews.Load() != 1 || sink.enabled() {
		t.Fatal("queued renewal survived Manual")
	}
	if _, err := control.Lease(snapshot); err == nil {
		t.Fatal("old lease survived")
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
	if other, err := runtimeowner.Acquire(context.Background(), dir); err == nil {
		other.Close()
		t.Fatal("failed drain released lock")
	}
	draining.Store(true)
	if err := control.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	other, err := runtimeowner.Acquire(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	other.Close()
}

func TestControlLeaseExpiryDisablesSinkWithoutNewCalls(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	n.onGrant = func(_ context.Context, _ string, r *a.ControlReply) error {
		r.GetGranted().Authority.RemainingLeaseMs = proto.Uint32(100)
		return nil
	}
	snapshot, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	for {
		select {
		case enabled := <-sink.changes:
			if enabled {
				goto acquired
			}
		default:
			t.Fatal("missing acquisition notification")
		}
	}
acquired:
	select {
	case enabled := <-sink.changes:
		if enabled {
			t.Fatal("expiry enabled authority")
		}
	case <-time.After(time.Second):
		t.Fatal("lease did not expire")
	}
	if _, err := control.Lease(snapshot); err == nil {
		t.Fatal("expired lease supplied")
	}
	if err := control.Renew(context.Background()); err == nil || n.renews.Load() != 0 {
		t.Fatal("expired lease renewed")
	}
}

func TestControlCloseJoinsActiveRenew(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	snapshot, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	entered := make(chan struct{})
	returned := make(chan struct{})
	n.onGrant = func(ctx context.Context, kind string, _ *a.ControlReply) error {
		if kind == "renew" {
			close(entered)
			<-ctx.Done()
			close(returned)
			return ctx.Err()
		}
		return nil
	}
	done := make(chan error, 1)
	go func() { done <- control.Renew(context.Background()) }()
	<-entered
	if err := control.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	select {
	case <-returned:
	default:
		t.Fatal("Close did not join control")
	}
	if err := <-done; err == nil {
		t.Fatal("cancelled renewal succeeded")
	}
	if _, err := control.Lease(snapshot); err == nil || sink.enabled() {
		t.Fatal("closed control supplied authority")
	}
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
	if value.Enabled || value.Snapshot.Native != 4 || value.Snapshot.Direction != 3 || n.acquires.Load() != 1 {
		t.Fatal("read refresh adopted authority", value)
	}
}

func TestControlObserveTargetRestartsWithoutAcquiringOrAdopting(t *testing.T) {
	t.Parallel()
	control, n, sink, _ := controlFixture(t, nil)
	n.mu.Lock()
	n.generation = 7
	n.owner = &a.Owner{ControllerSessionId: proto.String("previous-controller"), PlayerDirection: proto.Uint64(2)}
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
	if n.acquires.Load() != 0 || n.renews.Load() != 0 || n.revokes.Load() != 0 {
		t.Fatal("restart observation mutated authority")
	}
	n.mu.Lock()
	n.owner = nil
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
			native.owner = control.controlOwner(original)
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
