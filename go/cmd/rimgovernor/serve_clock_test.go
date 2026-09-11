package main

import (
	"context"
	"errors"
	"io"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

func TestClockServeRequiresPlayerControl(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "game", "--state", filepath.Join(dir, "state.db"), "--clock-control"}
	if _, err := parseServe(append(append([]string{}, base...), "--read-only"), io.Discard); err == nil {
		t.Fatal("read-only clock control accepted")
	}
	c, err := parseServe(append(base, "--player-control", "--profile", dir), io.Discard)
	if err != nil || !c.clockControl || !c.playerControl {
		t.Fatalf("clock player configuration: %+v %v", c, err)
	}
}

type clockServiceFake struct {
	buildingruntime.ClockNative
	buildingruntime.ClockWriter
	buildingruntime.ClockWindowNative
	reads  *buildingReadFake
	polled chan struct{}
}

func (f *clockServiceFake) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	return f.reads.Identity(ctx)
}

func (f *clockServiceFake) ReadClockStatus(context.Context, *c.Identity) (*k.StatusReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("clock status unavailable")
}

func (f *clockServiceFake) ReadClockEvents(context.Context, *k.EventsRequest) (*k.EventsReply, bridge.Result, error) {
	select {
	case f.polled <- struct{}{}:
	default:
	}
	return nil, bridge.Result{}, errors.New("event source unavailable")
}

func TestClockServiceDisabledStartupPollsAndJoins(t *testing.T) {
	dir := t.TempDir()
	reads := &buildingReadFake{serviceFake: serviceFake{entered: make(chan struct{}, 2)}}
	clock := &clockServiceFake{reads: reads, polled: make(chan struct{}, 1)}
	caps := unusedBuildingCapabilities{}
	config := serveConfig{playerControl: true, clockControl: true, profile: dir, state: filepath.Join(dir, "state.db"), listen: "127.0.0.1:0", refresh: time.Second, bridge: bridge.ProcessConfig{Timeout: time.Second}}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	addresses := make(buildingAddressWriter, 1)
	done := make(chan error, 1)
	go func() {
		done <- serveBuildingWithBridge(ctx, config, addresses, func(context.Context, bridge.ProcessConfig) (buildingServiceBridge, error) {
			return buildingServiceBridge{reads: reads, native: caps, authority: caps, writes: caps, draft: unusedDrafts(), clock: &buildingruntime.ClockCapabilities{Native: clock, Writer: clock}, clockReads: clock}, nil
		})
	}()
	select {
	case <-clock.polled:
	case err := <-done:
		t.Fatalf("startup: %v", err)
	case <-time.After(5 * time.Second):
		t.Fatal("clock polling did not start")
	}
	cancel()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("clock service did not join")
	}
	if !reads.closed.Load() || reads.closeWhileReading.Load() {
		t.Fatal("read connection closed before readers joined")
	}
}
