package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/controller"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type buildingServiceBridge struct {
	reads      serviceBridge
	native     buildingruntime.Native
	authority  buildingruntime.NativeAuthority
	writes     buildingruntime.BuildingWriter
	draft      *buildingruntime.DraftCapabilities
	clock      *buildingruntime.ClockCapabilities
	clockReads serviceClockReads
}
type buildingServiceOpener func(context.Context, bridge.ProcessConfig) (buildingServiceBridge, error)
type ownedAuthority struct {
	*bridge.Client
	*bridge.AuthorityControl
}

func openBuildingService(ctx context.Context, config bridge.ProcessConfig) (buildingServiceBridge, error) {
	client, err := bridge.Open(ctx, config)
	if err != nil {
		return buildingServiceBridge{}, err
	}
	authority, err := bridge.NewAuthorityControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	writes, err := bridge.NewBuildingControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	drafts, err := bridge.NewDraftControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	cleanup, err := bridge.NewDraftCleanup(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	clock, err := bridge.NewClockControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	return buildingServiceBridge{reads: client, native: client, authority: ownedAuthority{client, authority}, writes: writes,
		clock: &buildingruntime.ClockCapabilities{Native: client, Writer: clock}, clockReads: client,
		draft: &buildingruntime.DraftCapabilities{Native: client, Writer: drafts, Cleanup: cleanup}}, nil
}

type buildingWorldSource struct{ reads observation.Source }

func (s buildingWorldSource) ReadWorld(ctx context.Context) (store.World, error) {
	reply, _, err := s.reads.Identity(ctx)
	if err != nil {
		return store.World{}, err
	}
	identity, err := observation.DecodeIdentity(reply)
	if err != nil {
		return store.World{}, err
	}
	return store.World{Colony: identity.Colony, Load: identity.Load, Map: identity.Map}, nil
}

type buildingSnapshots struct {
	reads  httpapi.SnapshotProvider
	player interface {
		State() buildingruntime.ControlState
	}
}

func (s buildingSnapshots) Snapshot(ctx context.Context) (httpapi.Snapshot, error) {
	value, err := s.reads.Snapshot(ctx)
	if err != nil {
		return value, err
	}
	control := s.player.State()
	value.Mode = "manual"
	value.Generation = domain.Unknown[domain.GenerationSnapshot]()
	value.ActivePlanID = domain.Unknown[domain.PlanID]()
	identity, known := value.Identity.Value()
	matching := control.ObservationKnown && known && identity.Colony == control.Snapshot.Colony && identity.Load == control.Snapshot.Load && identity.Map == control.Snapshot.Map
	if control.Enabled && matching && value.Connected && !value.Stale {
		value.Mode = "automate"
		value.Status = "Player execution enabled"
	} else if control.Enabled {
		value.Status = "Player control is waiting for current observations"
	} else if value.Connected && !value.Stale {
		value.Status = "Manual; explicit player controls available"
	}
	if matching {
		value.Generation = domain.Known(control.Snapshot)
		value.ActivePlanID = domain.Known(control.Snapshot.Plan)
	}
	return value, nil
}

type buildingCloser interface{ Close(context.Context) error }

// A failed join retains the bridge, database and profile owner. Close itself
// observes uncertain cleanup; these retries never repeat an execution command.
func drainBuilding(owner buildingCloser) error {
	var first error
	for {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		err := owner.Close(ctx)
		cancel()
		if err == nil {
			return first
		}
		if first == nil {
			first = err
		}
		time.Sleep(100 * time.Millisecond)
	}
}

func serveBuildingControl(ctx context.Context, config serveConfig, out io.Writer) error {
	return serveBuildingWithBridge(ctx, config, out, openBuildingService)
}

func serveBuildingWithBridge(ctx context.Context, config serveConfig, out io.Writer, open buildingServiceOpener) (result error) {
	if err := ctx.Err(); err != nil {
		return err
	}
	listener, err := net.Listen("tcp", config.listen)
	if err != nil {
		return err
	}
	defer listener.Close()
	lifetime, cancel := context.WithCancel(ctx)
	defer cancel()
	client, err := open(lifetime, config.bridge)
	if err != nil {
		return err
	}
	if client.reads == nil {
		return errors.New("building service requires a read client")
	}
	defer func() { result = errors.Join(result, client.reads.Close()) }()
	if client.native == nil || client.authority == nil || client.writes == nil || client.draft == nil || client.draft.Native == nil || client.draft.Writer == nil || client.draft.Cleanup == nil {
		return errors.New("player service requires complete building and draft capabilities")
	}
	if _, err = client.reads.ConnectGame(lifetime); err != nil {
		return err
	}
	database, err := store.Open(lifetime, config.state)
	if err != nil {
		return err
	}
	defer func() { result = errors.Join(result, database.Close()) }()
	callTimeout := min(config.bridge.Timeout, 10*time.Second)
	var clockCapabilities *buildingruntime.ClockCapabilities
	if config.clockControl {
		if client.clock == nil || client.clock.Native == nil || client.clock.Writer == nil || client.clockReads == nil {
			return errors.New("clock service requires complete clock capabilities")
		}
		clockCapabilities = client.clock
		callTimeout = min(callTimeout, 5*time.Second)
	}
	session, err := buildingruntime.NewSession(lifetime, buildingruntime.SessionConfig{RoutineMethods: config.routineMethods,
		Rules:    config.resourceRules,
		Control:  buildingruntime.ControlConfig{ProfileDirectory: config.profile, LeaseDuration: 30 * time.Second, CallTimeout: callTimeout, Worlds: buildingWorldSource{client.reads}},
		Executor: executor.Limits{MaxAge: 5 * time.Second, RunTimeout: 8 * time.Second, JournalTimeout: 3 * time.Second},
		Draft:    client.draft,
		Clock:    clockCapabilities,
	}, database, client.native, client.authority, client.writes, wallClock{})
	if err != nil {
		return err
	}
	var owner buildingCloser = session
	var pollDone chan struct{}
	defer func() {
		cancel()
		if pollDone != nil {
			<-pollDone
		}
		result = errors.Join(result, drainBuilding(owner))
	}()
	player, err := buildingruntime.NewPlayer(lifetime, buildingruntime.PlayerConfig{CallTimeout: 30 * time.Second, JournalTimeout: 3 * time.Second}, database, session, buildingWorldSource{client.reads})
	if err != nil {
		return err
	}
	owner = player
	if config.clockControl {
		if err = startServiceClock(lifetime, player, session, client.clockReads, config.profile, callTimeout, config.routineReviews, config.routineSleepingPlans, config.routineCookingPlans); err != nil {
			return err
		}
	}
	worker, err := buildingruntime.NewWorker(lifetime, buildingruntime.WorkerConfig{RoutineMethods: config.routineMethods,
		StepInterval: time.Second, MaxBackoff: 10 * time.Second, StepTimeout: min(config.bridge.Timeout, 8*time.Second),
		RenewInterval: 5 * time.Second, RenewTimeout: 5 * time.Second,
	}, player, session)
	if err != nil {
		return err
	}
	owner = worker
	var entropy [16]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return err
	}
	reads, err := controller.NewReadState(hex.EncodeToString(entropy[:]), client.reads, wallClock{}, 2*config.refresh+config.bridge.Timeout)
	if err != nil {
		return err
	}
	_ = reads.Refresh(lifetime)
	presentation, _ := client.reads.(httpapi.PresentationReader)
	notifications, _ := client.reads.(httpapi.NotificationReader)
	var clockReview httpapi.ClockReview
	if config.clockControl {
		clockReview = serviceClockReview{database, config.profile}
	}
	server, err := httpapi.NewWithPlayer(httpapi.Config{ClockReview: clockReview, Notifications: notifications, Presentation: presentation, AssetsDir: config.assets, ReadTimeout: 35 * time.Second, ShutdownTimeout: 5 * time.Second, MaxResponseBytes: 1 << 20}, buildingSnapshots{reads, player}, database, player, database)
	if err != nil {
		return err
	}
	defer func() { result = errors.Join(result, server.Close()) }()
	pollDone = make(chan struct{})
	go func() { defer close(pollDone); reads.Poll(lifetime, config.refresh) }()
	if _, err = fmt.Fprintf(out, "RimGovernor Go player service: http://%s\n", listener.Addr()); err != nil {
		return err
	}
	return server.Serve(lifetime, listener)
}
