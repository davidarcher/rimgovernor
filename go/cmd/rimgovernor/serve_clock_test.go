package main

import (
	"context"
	"errors"
	"io"
	"path/filepath"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
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

func TestComfortServeRequiresReviewsAndCanOwnRoutineMethods(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "game", "--state", filepath.Join(dir, "state.db"), "--clock-control", "--player-control", "--profile", dir, "--routine-comfort-plans"}
	if _, err := parseServe(base, io.Discard); err == nil {
		t.Fatal("comfort compiler accepted without reviews")
	}
	c, err := parseServe(append(base, "--routine-reviews", "--routine-methods"), io.Discard)
	if err != nil || !c.routineComfortPlans || !c.routineMethods || c.routineSleepingPlans || c.routineShelterPlans {
		t.Fatalf("comfort configuration: %+v %v", c, err)
	}
}

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

func (f *clockServiceFake) ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error) {
	f.colonyReads.Add(1)
	return nil, bridge.Result{}, errors.New("colony read unavailable")
}

func (f *clockServiceFake) ReadRoutinePawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("pawn read unavailable")
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
	config := serveConfig{playerControl: true, clockControl: true, routineReviews: true, routineProjectLimit: 2, routineSleepingPlans: true, routineCookingPlans: true, routineMethods: true, profile: dir, state: filepath.Join(dir, "state.db"), listen: "127.0.0.1:0", refresh: time.Second, bridge: bridge.ProcessConfig{Timeout: time.Second}}
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
	if clock.colonyReads.Load() != 0 || clock.placementReads.Load() != 0 {
		t.Fatal("disabled startup read routine facts")
	}
}

func TestSleepingPlansRequireRoutineReviews(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "game", "--state", filepath.Join(dir, "state.db"), "--player-control", "--profile", dir, "--clock-control", "--routine-sleeping-plans"}
	if _, err := parseServe(base, io.Discard); err == nil {
		t.Fatal("unreviewed sleeping plans accepted")
	}
	config, err := parseServe(append(base, "--routine-reviews"), io.Discard)
	if err != nil || !config.routineSleepingPlans {
		t.Fatal(config, err)
	}
	if config.routineMethods {
		t.Fatal("compilation enabled execution implicitly")
	}
	config, err = parseServe(append(base, "--routine-reviews", "--routine-methods"), io.Discard)
	if err != nil || !config.routineMethods {
		t.Fatal(config, err)
	}
}

func TestShelterPlansRequireReviewsAndOptInExecution(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "game", "--state", filepath.Join(dir, "state.db"), "--player-control", "--profile", dir, "--clock-control", "--routine-shelter-plans"}
	if _, err := parseServe(base, io.Discard); err == nil {
		t.Fatal("unreviewed shelter accepted")
	}
	config, err := parseServe(append(base, "--routine-reviews"), io.Discard)
	if err != nil || !config.routineShelterPlans || config.routineMethods {
		t.Fatal(config, err)
	}
	config, err = parseServe(append(base, "--routine-reviews", "--routine-methods"), io.Discard)
	if err != nil || !config.routineMethods {
		t.Fatal(config, err)
	}
}

func TestRoutineServeRequiresClockControl(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "game", "--state", filepath.Join(dir, "state.db"), "--player-control", "--profile", dir, "--routine-reviews"}
	if _, err := parseServe(base, io.Discard); err == nil {
		t.Fatal("routine reviews without clock accepted")
	}
	config, err := parseServe(append(base, "--clock-control"), io.Discard)
	if err != nil || !config.routineReviews {
		t.Fatal(config, err)
	}
}

func TestCookingPlansRequireReviewsAndOptInExecution(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "game", "--state", filepath.Join(dir, "state.db"), "--player-control", "--profile", dir, "--clock-control", "--routine-cooking-plans"}
	if _, err := parseServe(base, io.Discard); err == nil {
		t.Fatal("unreviewed cooking accepted")
	}
	config, err := parseServe(append(base, "--routine-reviews"), io.Discard)
	if err != nil || !config.routineCookingPlans || config.routineMethods || config.routineSleepingPlans {
		t.Fatal(config, err)
	}
	config, err = parseServe(append(base, "--routine-reviews", "--routine-methods"), io.Discard)
	if err != nil || !config.routineMethods {
		t.Fatal(config, err)
	}
}
