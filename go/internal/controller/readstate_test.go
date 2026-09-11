package controller

import (
	"context"
	"encoding/json"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type fakeClock struct{ now time.Time }

func (c *fakeClock) Now() time.Time { return c.now }

type fakeSource struct {
	err     error
	block   <-chan struct{}
	entered chan struct{}
}

func (s *fakeSource) Identity(ctx context.Context) (bridge.Result, error) {
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
			return bridge.Result{}, ctx.Err()
		}
	}
	return bridge.Result{Structured: json.RawMessage(`{"success":true,"colonyId":"colony","loadToken":"load","mapId":0,"tick":123,"observationBatchVersion":1,"placementPreviewBatchVersion":2}`)}, s.err
}
func (s *fakeSource) Status(context.Context) (bridge.Result, error) {
	return bridge.Result{Structured: json.RawMessage(`{"success":true,"status":"game_loaded","time":{"paused":true,"forcePaused":false,"timeSpeed":"Paused"},"skipped":[]}`)}, s.err
}

func TestRetainLastGoodReadOnRefreshFailureAndAge(t *testing.T) {
	clock := &fakeClock{time.Now()}
	source := &fakeSource{}
	state, err := NewReadState("session", source, clock, time.Second)
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
	source.err = errors.New("offline")
	if err = state.Refresh(context.Background()); err == nil {
		t.Fatal("missing failure")
	}
	failed, _ := state.Snapshot(context.Background())
	if failed.Connected || !failed.Stale || failed.Tick != fresh.Tick || failed.Identity != fresh.Identity {
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
	state, _ := NewReadState("session", source, &fakeClock{time.Now()}, time.Second)
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
