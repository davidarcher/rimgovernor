package main

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	commonpb "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	lifecyclepb "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	observationspb "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type fakeClock struct{ now time.Time }

func (c *fakeClock) Now() time.Time { return c.now }

type fakeSource struct {
	err     error
	block   <-chan struct{}
	entered chan struct{}
	paused  *bool
}

func (s *fakeSource) Identity(ctx context.Context) (*lifecyclepb.IdentityReply, bridge.Result, error) {
	if s.entered != nil {
		select {
		case s.entered <- struct{}{}:
		default:
		}
	}
	if s.block != nil {
		select {
		case <-s.block:
		case <-ctx.Done():
			return nil, bridge.Result{}, ctx.Err()
		}
	}
	return &lifecyclepb.IdentityReply{Outcome: &lifecyclepb.IdentityReply_Loaded{Loaded: &lifecyclepb.LoadedIdentity{Context: readContext(), Paused: s.paused}}}, bridge.Result{}, s.err
}
func readContext() *commonpb.ObservationContext {
	return &commonpb.ObservationContext{Identity: &commonpb.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}, Tick: proto.Int64(123)}
}
func (s *fakeSource) Status(_ context.Context, identity *commonpb.Identity) (*observationspb.StatusReply, bridge.Result, error) {
	if !proto.Equal(identity, readContext().Identity) {
		return nil, bridge.Result{}, errors.New("status scope mismatch")
	}
	return &observationspb.StatusReply{Outcome: &observationspb.StatusReply_Observed{Observed: &observationspb.StatusSnapshot{Context: readContext()}}}, bridge.Result{}, s.err
}

func TestRetainLastGoodReadOnRefreshFailureAndAge(t *testing.T) {
	clock := &fakeClock{time.Now()}
	source := &fakeSource{paused: proto.Bool(false)}
	state, err := newReadState("session", source, clock, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if err = state.Refresh(context.Background()); err != nil {
		t.Fatal(err)
	}
	fresh, err := state.Snapshot(context.Background())
	if err != nil || fresh.Stale || !fresh.Connected {
		t.Fatal(fresh, err)
	}
	if _, known := fresh.Generation.Value(); known {
		t.Fatal("read invented native authority")
	}
	if paused, known := fresh.Paused.Value(); !known || paused {
		t.Fatal("false pause lost")
	}
	source.err = errors.New("offline")
	if err = state.Refresh(context.Background()); err == nil {
		t.Fatal("missing failure")
	}
	failed, _ := state.Snapshot(context.Background())
	if failed.Connected || !failed.Stale || failed.Tick != fresh.Tick || failed.Identity != fresh.Identity || failed.Paused != fresh.Paused || failed.Mode != "manual" {
		t.Fatal("last good data lost", failed)
	}
	source.err = nil
	if err = state.Refresh(context.Background()); err != nil {
		t.Fatal(err)
	}
	clock.now = clock.now.Add(2 * time.Second)
	aged, _ := state.Snapshot(context.Background())
	if !aged.Stale {
		t.Fatal("old read fresh")
	}
	clock.now = state.started.Add(time.Millisecond)
	state.snapshot.ObservedAt = domain.Known(state.started.Add(2 * time.Millisecond))
	rewound, _ := state.Snapshot(context.Background())
	if !rewound.Stale {
		t.Fatal("future observation fresh")
	}
}

func TestQueuedRefreshCancellationDoesNotWaitForNativeRead(t *testing.T) {
	source := &fakeSource{block: make(chan struct{}), entered: make(chan struct{}, 1)}
	state, _ := newReadState("session", source, &fakeClock{time.Now()}, time.Second)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- state.Refresh(ctx) }()
	<-source.entered
	queued, stop := context.WithCancel(context.Background())
	stop()
	if err := state.Refresh(queued); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	cancel()
	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
}

func TestInitialUnavailableReadHasNoInventedFacts(t *testing.T) {
	state, err := newReadState("session", &fakeSource{err: errors.New("unavailable")}, &fakeClock{time.Now()}, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if err := state.Refresh(context.Background()); err == nil {
		t.Fatal("missing read error")
	}
	got, err := state.Snapshot(context.Background())
	if err != nil || got.Mode != "manual" || got.Connected || !got.Stale {
		t.Fatal(got, err)
	}
	if _, known := got.Identity.Value(); known {
		t.Fatal("invented identity")
	}
	if _, known := got.Tick.Value(); known {
		t.Fatal("invented tick")
	}
	if _, known := got.Paused.Value(); known {
		t.Fatal("invented pause")
	}
	if _, known := got.Generation.Value(); known {
		t.Fatal("invented authority")
	}
}
