package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"sync"
	"testing"
	"time"
)

// Every native surface shares this lock, including shutdown identity reads.
// The reusable single-threaded fixture never escapes into concurrent worker calls.
type joinedClockNative struct {
	mu                                sync.Mutex
	source                            *schedulerNative
	calls                             int
	alert                             bool
	started, paused, captured         chan struct{}
	startOnce, pauseOnce, captureOnce sync.Once
}

func (f *joinedClockNative) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls++
	return f.source.Identity(ctx)
}
func (f *joinedClockNative) Tick(ctx context.Context) (*l.TickReply, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls++
	return f.source.Tick(ctx)
}
func (f *joinedClockNative) ReadClockStatus(ctx context.Context, id *c.Identity) (*k.StatusReply, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls++
	return f.source.ReadClockStatus(ctx, id)
}
func (f *joinedClockNative) ReadClockAttempt(ctx context.Context, r *k.AttemptRequest) (*k.AttemptReply, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls++
	return f.source.ReadClockAttempt(ctx, r)
}
func (f *joinedClockNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls++
	return f.source.ReadEmergency(ctx, id)
}

// ReadBundle composes the joined reads; each part counts as its own call.
func (f *joinedClockNative) ReadBundle(ctx context.Context, request *o.BundleRequest) (*o.BundleReply, bridge.Result, error) {
	return composeBundle(ctx, request, bundleParts{tick: f.Tick, status: f.ReadClockStatus, emergency: f.ReadEmergency, events: f.ReadClockEvents})
}
func (f *joinedClockNative) Start(ctx context.Context, r *k.StartRequest) (*k.ControlReply, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls++
	reply, raw, err := f.source.Start(ctx, r)
	if err == nil {
		f.startOnce.Do(func() { close(f.started) })
	}
	return reply, raw, err
}
func (f *joinedClockNative) Renew(ctx context.Context, r *k.RenewRequest, original *k.Epoch) (*k.ControlReply, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls++
	return f.source.Renew(ctx, r, original)
}
func (f *joinedClockNative) ChangeSpeed(ctx context.Context, r *k.SpeedRequest, original *k.Epoch) (*k.ControlReply, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls++
	return f.source.ChangeSpeed(ctx, r, original)
}
func (f *joinedClockNative) OwnedPause(ctx context.Context, r *k.OwnedRequest) (*k.StatusReply, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls++
	reply, raw, err := f.source.OwnedPause(ctx, r)
	if err == nil {
		f.pauseOnce.Do(func() { close(f.paused) })
	}
	return reply, raw, err
}
func (f *joinedClockNative) ReadClockEvents(ctx context.Context, request *k.EventsRequest) (*k.EventsReply, bridge.Result, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.calls++
	newest := int64(0)
	if f.alert {
		newest = 1
	}
	f.source.status.NewestCursor = proto.Int64(newest)
	page := &k.EventsPage{Context: proto.Clone(f.source.status.Context).(*c.ObservationContext), NewestCursor: proto.Int64(newest), NextCursor: proto.Int64(newest), Gap: proto.Bool(false), LostCount: proto.Uint64(0)}
	if f.alert && request.GetAfterCursor() == 0 {
		page.Events = []*k.Event{{Cursor: proto.Int64(1), Owner: proto.Clone(clockCoordinatorEpoch(f.source.status).Owner).(*k.EpochOwner), Context: proto.Clone(f.source.status.Context).(*c.ObservationContext), ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_Alert{Alert: &k.Alert{Key: proto.String("danger"), Label: proto.String("danger"), Priority: proto.String("High")}}}}
	}
	if request.GetAfterCursor() == 1 {
		f.captureOnce.Do(func() { close(f.captured) })
	}
	return &k.EventsReply{Outcome: &k.EventsReply_Page{Page: page}}, bridge.Result{}, ctx.Err()
}
func TestClockWorkerActualSessionInterruptionAndJoinedClose(t *testing.T) {
	t.Parallel()
	s, source := schedulerFixture(t)
	native := &joinedClockNative{source: source, started: make(chan struct{}), paused: make(chan struct{}), captured: make(chan struct{})}
	s.native = native
	s.session.clock.native = native
	s.session.clock.writer = native
	s.session.control.config.Worlds = clockWorldSource{native}
	// The fake only echoes the lease, and a short CallTimeout can expire
	// between AppendClockEvents and ReviewClockEvents on a loaded machine,
	// letting the after=1 poll below observe an unreviewed inbox.
	s.config.Start.LeaseMS = 30_000
	worker, err := NewClockWorker(context.Background(), s, native, ClockWorkerConfig{PollInterval: 10 * time.Millisecond, RenewInterval: 100 * time.Millisecond, StepInterval: 10 * time.Millisecond, MaxBackoff: 100 * time.Millisecond, PollTimeout: 5 * time.Second, RenewTimeout: 5 * time.Second, StepTimeout: 5 * time.Second, PageLimit: 128})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()
		_ = worker.Stop(ctx)
	})
	wait := func(ch <-chan struct{}, what string) {
		t.Helper()
		select {
		case <-ch:
		case <-time.After(3 * time.Second):
			t.Fatal("timed out: " + what)
		}
	}
	wait(native.started, "actual scheduler start")
	select {
	case s.player.gate <- struct{}{}:
	case <-time.After(time.Second):
		t.Fatal("could not hold player gate")
	}
	held := true
	defer func() {
		if held {
			<-s.player.gate
		}
	}()
	native.mu.Lock()
	native.alert = true
	native.source.status.NewestCursor = proto.Int64(1)
	native.mu.Unlock()
	wait(native.paused, "owned pause while player gate blocked")
	wait(native.captured, "poll after durable capture")
	state, err := s.player.journal.ReadClockReview(context.Background(), s.config.Profile)
	if err != nil || state.InboxCursor != 1 || state.ReviewedCursor != 1 || state.AcknowledgedCursor != 0 || len(state.Holds) != 1 || s.session.State().Enabled {
		t.Fatal(state, err)
	}
	native.mu.Lock()
	pauses, writes := native.source.pauses, native.source.writes
	native.mu.Unlock()
	if pauses != 1 || writes < 1 {
		t.Fatal("native counts", pauses, writes)
	}
	// Session Close must cancel/join a scheduler waiting on the still-held gate.
	closeCtx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if err = s.session.Close(closeCtx); err != nil {
		t.Fatal(err)
	}
	select {
	case <-worker.done:
	default:
		t.Fatal("worker not joined")
	}
	<-s.player.gate
	held = false
	owner, err := AcquireProfile(context.Background(), s.config.Profile)
	if err != nil {
		t.Fatal("profile retained after successful joined close", err)
	}
	if err = owner.Close(); err != nil {
		t.Fatal(err)
	}
	if err = s.player.journal.Close(); err != nil {
		t.Fatal(err)
	}
	native.mu.Lock()
	before := native.calls
	native.mu.Unlock()
	if err = worker.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
	native.mu.Lock()
	after := native.calls
	native.mu.Unlock()
	if after != before {
		t.Fatal("cached Stop called native after Session close", before, after)
	}
}
