package main

import (
	"context"
	"errors"
	"path/filepath"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type clockServiceFake struct {
	buildingruntime.ClockNative
	buildingruntime.ClockWriter
	buildingruntime.ClockWindowNative
	reads          *buildingReadFake
	polled         chan struct{}
	colonyReads    atomic.Int32
	placementReads atomic.Int32
}

func (f *clockServiceFake) PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	f.placementReads.Add(1)
	return bridge.BuildingPreview{}, bridge.Result{}, errors.New("preview unavailable")
}

func (f *clockServiceFake) ReadColonyFacts(context.Context, *c.Identity, bool) (*o.ColonyFactsReply, bridge.Result, error) {
	f.colonyReads.Add(1)
	return nil, bridge.Result{}, errors.New("colony read unavailable")
}

func (f *clockServiceFake) FrameTables(context.Context, *c.Identity) (bridge.Tables, error) {
	return bridge.Tables{}, errors.New("frame tables unavailable")
}

func (f *clockServiceFake) ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{}, bridge.Result{}, errors.New("emergency read unavailable")
}

func (f *clockServiceFake) ReadRoutineFrame(context.Context, *c.Identity) (bridge.RoutineFrame, error) {
	f.colonyReads.Add(1)
	return bridge.RoutineFrame{}, errors.New("routine frame unavailable")
}

// AnimalRaceCatalog is the empty race catalog (the observation source requires one).
func (f *clockServiceFake) AnimalRaceCatalog(context.Context, *c.Identity) (*bridge.AnimalRaces, error) {
	return &bridge.AnimalRaces{}, nil
}

func (f *clockServiceFake) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	return f.reads.Identity(ctx)
}

func (f *clockServiceFake) Tick(ctx context.Context) (*l.TickReply, bridge.Result, error) {
	return f.reads.Tick(ctx)
}

func (f *clockServiceFake) ReadClockStatus(context.Context, *c.Identity) (*k.StatusReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("clock status unavailable")
}

func (f *clockServiceFake) ReadClockEvents(context.Context, *k.EventsRequest) (*k.EventsReply, bridge.Result, error) {
	return nil, bridge.Result{}, f.unavailable()
}
func (f *clockServiceFake) ReadStep(context.Context, bridge.StepRequest) (*o.BundleSnapshot, bridge.Result, error) {
	return nil, bridge.Result{}, f.unavailable()
}
func (f *clockServiceFake) unavailable() error {
	select {
	case f.polled <- struct{}{}:
	default:
	}
	return errors.New("event source unavailable")
}

// The serve clock holds its journal read under a running window and leaves
// the read its second of the lease-bound poll budget; a call timeout too
// short for any hold polls unheld, and a worker accepts every sizing.
func TestServiceClockTimeoutsHoldTheRunningPoll(t *testing.T) {
	for _, tc := range []struct {
		call, wait, poll time.Duration
	}{
		{call: 10 * time.Second, wait: serviceClockPollWait, poll: 7 * time.Second},
		{call: 7 * time.Second, wait: serviceClockPollWait, poll: 7 * time.Second},
		{call: 3 * time.Second, wait: 2 * time.Second, poll: 3 * time.Second},
		{call: time.Second, wait: 0, poll: time.Second},
	} {
		got := serviceClockTimeouts(tc.call)
		if got.PollWait != tc.wait || got.Poll != tc.poll || got.RunningPoll != 0 {
			t.Fatalf("serviceClockTimeouts(%v) = %+v, want PollWait %v Poll %v RunningPoll 0", tc.call, got, tc.wait, tc.poll)
		}
		if got.PollWait > 0 && got.PollWait+time.Second > got.Poll {
			t.Fatalf("serviceClockTimeouts(%v): hold %v leaves the read under a second of %v", tc.call, got.PollWait, got.Poll)
		}
		if got.PollWait > bridge.ClockEventsMaxWaitMs*time.Millisecond {
			t.Fatalf("serviceClockTimeouts(%v): hold %v exceeds the native bound", tc.call, got.PollWait)
		}
	}
}

func TestClockServiceDisabledStartupPollsAndJoins(t *testing.T) {
	dir := t.TempDir()
	reads := &buildingReadFake{serviceFake: serviceFake{entered: make(chan struct{}, 2)}}
	clock := &clockServiceFake{reads: reads, polled: make(chan struct{}, 1)}
	caps := unusedBuildingCapabilities{}
	config := serveConfig{playerControl: true, clockControl: true, routineReviews: true, routineSleepingPlans: true, routineCookingPlans: true, routineMethods: true, profile: dir, state: filepath.Join(dir, "state.db"), listen: "127.0.0.1:0", refresh: time.Second, bridge: bridge.ProcessConfig{Timeout: time.Second}}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	addresses := make(buildingAddressWriter, 1)
	done := make(chan error, 1)
	go func() {
		done <- serveBuildingWithBridge(ctx, config, addresses, func(context.Context, bridge.ProcessConfig) (buildingServiceBridge, error) {
			return buildingServiceBridge{reads: reads, native: caps, authority: caps, writes: caps, clock: &buildingruntime.ClockCapabilities{Native: clock, Writer: clock}, clockReads: clock}, nil
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
	if clock.colonyReads.Load() != 0 || clock.placementReads.Load() != 0 {
		t.Fatal("disabled startup read routine facts")
	}
}

func TestClockResourceThresholdsSortedAndBounded(t *testing.T) {
	targets := map[policy.Resource]int64{"Steel": 200, "WoodLog": 300, "Silver": 0}
	got := clockResourceThresholds(targets)
	if len(got) != 2 || got[0].GetDefName() != "Steel" || got[0].GetLevel() != 200 || got[1].GetDefName() != "WoodLog" {
		t.Fatal(got)
	}
	for i := range bridge.ClockResourceThresholdsMax + 5 {
		targets[policy.Resource("Def"+string(rune('a'+i)))] = 1
	}
	if got := clockResourceThresholds(targets); len(got) != bridge.ClockResourceThresholdsMax {
		t.Fatal(len(got))
	}
}
