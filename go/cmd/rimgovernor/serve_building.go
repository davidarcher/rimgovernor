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
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/haul"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	factsstore "github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type buildingServiceBridge struct {
	reads             serviceBridge
	native            boundary.Native
	authority         buildingruntime.NativeAuthority
	writes            boundary.BuildingWriter
	clock             *buildingruntime.ClockCapabilities
	clockReads        serviceClockReads
	movement          *buildingruntime.MovementCapabilities
	haul              *haul.HaulCapabilities
	trade             *buildingruntime.TradeCapabilities
	presentationMedia *bridge.PresentationMedia
	lifecycle         lifecycleCapability
	attention         httpapi.AttentionAcknowledger
}
type buildingServiceOpener func(context.Context, bridge.ProcessConfig) (buildingServiceBridge, error)
type ownedAuthority struct {
	*bridge.Client
	*bridge.AuthorityControl
}

// lifecycleCapability composes the three separately held lifecycle mutations
// (Save, Load, NewColony) into the single httpapi.LifecycleWriter shape; neither embedded
// capability grants the other's authority.
type lifecycleCapability struct {
	*flushedSave
	*bridge.LifecycleLoad
	*bridge.LifecycleNewColony
}

// attentionAcknowledger adapts bridge.Client.AckAttention to httpapi.AttentionAcknowledger.
type attentionAcknowledger struct{ client *bridge.Client }

func (a attentionAcknowledger) AckAttention(ctx context.Context, attentionID string) error {
	_, err := a.client.AckAttention(ctx, attentionID)
	return err
}

func openBuildingService(ctx context.Context, config bridge.ProcessConfig) (buildingServiceBridge, error) {
	client, err := openConfigured(ctx, config)
	if err != nil {
		return buildingServiceBridge{}, err
	}
	authority, err := bridge.NewAuthorityControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	clock, err := bridge.NewClockControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	actionsWriter, err := bridge.NewActionsWriter(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	presentationMedia, err := bridge.NewPresentationMedia(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	lifecycleSave, err := bridge.NewLifecycleSave(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	lifecycleLoad, err := bridge.NewLifecycleLoad(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	lifecycleNewColony, err := bridge.NewLifecycleNewColony(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	return buildingServiceBridge{reads: client, native: client, authority: ownedAuthority{client, authority}, writes: actionsWriter,
		clock: &buildingruntime.ClockCapabilities{Native: client, Writer: clock}, clockReads: client,
		movement:          &buildingruntime.MovementCapabilities{Writer: actionsWriter},
		haul:              &haul.HaulCapabilities{Native: client, Writer: actionsWriter},
		trade:             &buildingruntime.TradeCapabilities{Native: client, Writer: actionsWriter, Requests: client},
		presentationMedia: presentationMedia,
		lifecycle:         lifecycleCapability{&flushedSave{LifecycleSave: lifecycleSave}, lifecycleLoad, lifecycleNewColony},
		attention:         attentionAcknowledger{client}}, nil
}

type buildingWorldSource struct{ reads observation.Source }

func (s buildingWorldSource) ReadWorld(ctx context.Context) (store.World, error) {
	reply, _, err := s.reads.Tick(ctx)
	if err != nil {
		return store.World{}, err
	}
	identity, err := observation.DecodeTick(reply)
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

func haulExecutorRequired(config serveConfig) bool {
	return config.roundsFoodStorageUpkeepPlans
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
	if client.native == nil || client.authority == nil || client.writes == nil {
		return errors.New("player service requires complete building capabilities")
	}
	natives, err := requireServeNatives(client)
	if err != nil {
		return err
	}
	started, err := client.reads.GamesStart(lifetime)
	if err != nil {
		return err
	}
	if _, err = client.reads.ConnectWithPoll(lifetime, started); err != nil {
		return err
	}
	database, err := openState(lifetime, config.state)
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
	}
	var movementCapabilities *buildingruntime.MovementCapabilities
	if config.roundsDefensePlans {
		movementCapabilities = client.movement
	}
	var haulCapabilities *haul.HaulCapabilities
	if haulExecutorRequired(config) {
		if client.haul == nil {
			return errors.New("secure supplies/haul plans require typed haul capabilities")
		}
		haulCapabilities = client.haul
	}
	var tradeCapabilities *buildingruntime.TradeCapabilities
	if config.roundsTradePlans {
		tradeCapabilities = client.trade
	}
	session, err := buildingruntime.NewSession(lifetime, buildingruntime.SessionConfig{RoundsMethods: config.roundsMethods,
		Control:  buildingruntime.ControlConfig{ProfileDirectory: config.profile, CallTimeout: callTimeout, Worlds: buildingWorldSource{client.reads}},
		Executor: executor.Limits{MaxAge: 5 * time.Second, RunTimeout: 8 * time.Second, JournalTimeout: 3 * time.Second},
		Clock:    clockCapabilities,
		Movement: movementCapabilities,
		Haul:     haulCapabilities,
		Trade:    tradeCapabilities,
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
	player, err := buildingruntime.NewPlayer(lifetime, buildingruntime.PlayerConfig{CallTimeout: serviceClockStepTimeout, JournalTimeout: 3 * time.Second}, database, session, buildingWorldSource{client.reads})
	if err != nil {
		return err
	}
	owner = player
	// One wake signal joins the clock poll loop to the step loop and the
	// worker: committed journal evidence steps both at once.
	wake := buildingruntime.NewWakeSignal()
	// The decoded state store (#354): the scheduler's reviews
	// file their census sections, /api/routines reports them.
	var sections *factsstore.Store
	var advanced, windowRunning = func() {}, func() bool { return false }
	var stepTrace func() telemetry.Trace
	var validity func() (domain.ReadValidity, bool)
	// The per-world store rebuild (#1123): the clock worker's gate and the save
	// flusher share it, so it runs before either acts in a new world.
	stateNative := natives.state
	rebuild := &worldRebuild{database: database, out: out, orphans: struct {
		buildingruntime.TradeNative
		boundary.ActionsWriter
	}{client.trade.Native, client.trade.Writer}}
	worldReady := rebuild.workerGate(stateNative)
	var autosave func(context.Context, *c.Identity, int64)
	if client.lifecycle.flushedSave != nil {
		autosave = newGoAutosaver(client.lifecycle.flushedSave.Save).AtStop
	}
	if config.clockControl {
		sections = factsstore.NewStore()
		clockWorker, err := startServiceClock(lifetime, player, session, client.clockReads, database, config, serviceClockTimeouts(callTimeout), wake, sections, worldReady, autosave)
		if err != nil {
			return err
		}
		advanced, windowRunning, stepTrace, validity = clockWorker.Nudge, clockWorker.WindowRunning, clockWorker.Trace, clockWorker.Validity
	}
	worker, err := buildingruntime.NewWorker(lifetime, buildingruntime.WorkerConfig{BreakSource: natives.breaks, Pawns: natives.breaks, Moves: client.movement.Writer, RoundsMethods: config.roundsMethods,
		StepInterval: time.Second, MaxBackoff: 10 * time.Second, StepTimeout: min(config.bridge.Timeout, 8*time.Second),
		RenewInterval: 5 * time.Second, RenewTimeout: 5 * time.Second, Wake: wake, Advanced: advanced, Store: sections, WindowRunning: windowRunning, Trace: stepTrace, Validity: validity, Flush: natives.flush.FlushSnapshot,
	}, player, session)
	if err != nil {
		return err
	}
	owner = worker
	var entropy [16]byte
	if _, err = rand.Read(entropy[:]); err != nil {
		return err
	}
	reads, err := newReadState(hex.EncodeToString(entropy[:]), client.reads, wallClock{}, 2*config.refresh+config.bridge.Timeout)
	if err != nil {
		return err
	}
	_ = reads.Refresh(lifetime)
	// Every Go-made save and every pre_save signal flushes the same way (#2359).
	flusher := &stateFlusher{native: stateNative, world: currentGovernorWorld(reads), database: database, rebuild: rebuild}
	if client.lifecycle.flushedSave != nil {
		client.lifecycle.flushedSave.flush = flusher.Flush
	}
	var clockReview httpapi.ClockReview
	var routines httpapi.RoundsProvider
	if config.clockControl {
		clockReview = serviceClockReview{database, config.profile}
	}
	if config.roundsEnabled {
		routines = serviceRoundsDiagnostics{journal: database, reviewsEnabled: config.roundsEnabled, methodsEnabled: config.roundsMethods, families: config.activeRoundsFamilies(), sections: sections}
	}
	// The live colony census route is a plain read every serve exposes (#261).
	colonyStatus, err := buildingruntime.NewColonyStatus(player, natives.colony, sections)
	if err != nil {
		return err
	}
	server, err := httpapi.NewWithPlayer(httpapi.Config{ClockReview: clockReview, Rounds: routines, ColonyStatus: colonyStatus, Notifications: natives.notifications, Presentation: natives.presentation, PresentationMedia: client.presentationMedia, Lifecycle: client.lifecycle, Attention: client.attention, Pprof: config.pprof, FlightRecorder: config.flightRecorder, Access: accessRecorder(config), ReadTimeout: 35 * time.Second, ShutdownTimeout: 5 * time.Second, MaxResponseBytes: 1 << 20}, buildingSnapshots{reads, player}, database, player, database)
	if err != nil {
		return err
	}
	pollDone = make(chan struct{})
	go func() { defer close(pollDone); reads.Poll(lifetime, config.refresh) }()
	superviseDone := make(chan struct{})
	defer func() { <-superviseDone }()
	go func() { defer close(superviseDone); superviseBridge(lifetime, client.reads, out, cancel) }()
	flushLoopDone := make(chan struct{})
	defer func() { <-flushLoopDone }()
	go func() {
		defer close(flushLoopDone)
		runSaveFlusher(lifetime, natives.signals, flusher.Flush, saveSignalRetry)
	}()
	if config.resume {
		resumer, err := newAutoResumer(buildingSnapshots{reads, player}, player, database, out)
		if err != nil {
			return err
		}
		resumeDone := make(chan struct{})
		defer func() { <-resumeDone }()
		go func() { defer close(resumeDone); resumer.run(lifetime, config.refresh) }()
	}
	if _, err = fmt.Fprintf(out, "RimGovernor Go player service: http://%s\n", listener.Addr()); err != nil {
		return err
	}
	return server.Serve(lifetime, listener)
}

// serveNatives are the typed capabilities the player service cannot run
// without (#1670): each is asserted once at startup so a client missing one
// fails serve with the capability's name instead of a route that quietly
// does not exist.
type serveNatives struct {
	flush         interface{ FlushSnapshot(context.Context) error }
	state         governorStateNative
	signals       saveSignalNative
	breaks        buildingruntime.BreakResponseSource
	presentation  httpapi.PresentationReader
	notifications httpapi.NotificationReader
	colony        buildingruntime.ColonyStatusNative
}

// requireNative asserts that source implements T, naming the missing
// capability when it does not.
func requireNative[T any](source any, name string) (T, error) {
	native, ok := source.(T)
	if !ok {
		return native, fmt.Errorf("serve requires %s: the native client does not provide it", name)
	}
	return native, nil
}

func requireServeNatives(client buildingServiceBridge) (natives serveNatives, err error) {
	if natives.presentation, natives.notifications, err = requirePresentationReaders(client.reads); err != nil {
		return natives, err
	}
	if client.attention == nil {
		return natives, errors.New("serve requires the attention acknowledger: the native client does not provide it")
	}
	if client.movement == nil || client.movement.Writer == nil {
		return natives, errors.New("serve requires typed movement capabilities: the native client does not provide them")
	}
	if client.trade == nil || client.trade.Native == nil || client.trade.Writer == nil {
		return natives, errors.New("serve requires typed trade capabilities: the native client does not provide them")
	}
	if natives.flush, err = requireNative[interface{ FlushSnapshot(context.Context) error }](client.reads, "the snapshot flush (FlushSnapshot)"); err != nil {
		return natives, err
	}
	if natives.state, err = requireNative[governorStateNative](client.reads, "the governor state component (GovernorState, PutGovernorStateBatch)"); err != nil {
		return natives, err
	}
	if natives.breaks, err = requireNative[buildingruntime.BreakResponseSource](client.reads, "the break response reads (ReadEmergency, ReadCombatPawns; also the arrival hold reads)"); err != nil {
		return natives, err
	}
	if natives.signals, err = requireNative[saveSignalNative](client.reads, "the pre_save signal (WaitSaveSignal, FlushDone)"); err != nil {
		return natives, err
	}
	natives.colony, err = requireNative[buildingruntime.ColonyStatusNative](client.native, "the colony census reads (ReadColonyFacts, ReadHomeColonists)")
	return natives, err
}

func requirePresentationReaders(source any) (httpapi.PresentationReader, httpapi.NotificationReader, error) {
	presentation, err := requireNative[httpapi.PresentationReader](source, "the presentation reader (ReadCamera, ReadSelection, ReadColonistRoster, ReadRenderState)")
	if err != nil {
		return nil, nil, err
	}
	notifications, err := requireNative[httpapi.NotificationReader](source, "the notification reader (ReadNotifications)")
	return presentation, notifications, err
}
