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
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/acquisition"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/beduse"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/buildingtemperature"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/capture"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/claimbuilding"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/equip"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/growercrop"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/haul"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/melee"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/mineacquisition"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/movebuilding"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/ranged"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/rescue"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/tend"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	factsstore "github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/interpreter"
	"github.com/davidarcher/RimGovernor/go/internal/model"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// moodReliefWorldSource is the narrow slice of *bridge.Client that
// readMoodReliefLongitude needs to find the colony's home tile and read its
// longitude: the world-progression census (for the home map's Tile) and
// ReadWorld itself (for WorldTile.longitude). It exists only so
// readMoodReliefLongitude can be exercised against a test fake without
// pulling in the rest of bridge.Client's surface.
type moodReliefWorldSource interface {
	ReadWorldProgression(ctx context.Context, identity *c.Identity, includeStorage bool) (bridge.WorldProgressionRead, bridge.Result, error)
	ReadWorld(ctx context.Context, identity *c.Identity, tile int32, settlementRadius float64) (bridge.WorldRead, bridge.Result, error)
}

// readMoodReliefLongitude reads the colony's home-tile longitude once, at
// session startup: bridge.WorldRead.Longitude's doc comment calls it
// "effectively a session constant" since the colony's map tile does not
// move, so one read here is enough -- MoodRelief's boundary never issues a
// per-tick WorldRead for it. It never aborts the CLI session: a missing
// identity or world source, a failed identity/world-progression/world read,
// or no map marked Home in the census all degrade to Unknown -- the same
// "log/ignore and fall back" degradation every other optional startup
// capability in this file uses, and never a guessed or defaulted longitude.
func readMoodReliefLongitude(ctx context.Context, identitySource observation.Source, worldSource moodReliefWorldSource) domain.Fact[float64] {
	unknown := domain.Unknown[float64]()
	if identitySource == nil || worldSource == nil {
		return unknown
	}
	reply, _, err := identitySource.Tick(ctx)
	if err != nil {
		return unknown
	}
	identity, err := observation.DecodeTick(reply)
	if err != nil {
		return unknown
	}
	wireIdentity := boundary.Identity(domain.GenerationSnapshot{Colony: identity.Colony, Load: identity.Load, Map: identity.Map})
	progression, _, err := worldSource.ReadWorldProgression(ctx, wireIdentity, false)
	if err != nil {
		return unknown
	}
	homeTile, found := int32(-1), false
	for _, m := range progression.Maps {
		if m.Home {
			homeTile, found = m.Tile, true
			break
		}
	}
	if !found {
		return unknown
	}
	world, _, err := worldSource.ReadWorld(ctx, wireIdentity, homeTile, 0)
	if err != nil {
		return unknown
	}
	return world.Longitude
}

type buildingServiceBridge struct {
	reads               serviceBridge
	native              boundary.Native
	authority           buildingruntime.NativeAuthority
	writes              boundary.BuildingWriter
	acquisition         *acquisition.AcquisitionCapabilities
	mineAcquisition     *mineacquisition.MineAcquisitionCapabilities
	moveBuilding        *movebuilding.Capabilities
	draft               *draft.DraftCapabilities
	clock               *buildingruntime.ClockCapabilities
	clockReads          serviceClockReads
	melee               *melee.MeleeCapabilities
	ranged              *ranged.RangedCapabilities
	movement            *buildingruntime.MovementCapabilities
	tend                *tend.TendCapabilities
	rescue              *rescue.RescueCapabilities
	capture             *capture.CaptureCapabilities
	equip               *equip.EquipCapabilities
	haul                *haul.HaulCapabilities
	repair              *buildingruntime.RepairCapabilities
	clean               *buildingruntime.CleanCapabilities
	moodRelief          *buildingruntime.MoodReliefCapabilities
	moodReliefWorld     moodReliefWorldSource
	gearReplace         *buildingruntime.GearReplaceCapabilities
	trade               *buildingruntime.TradeCapabilities
	buildingTemperature *buildingtemperature.Capabilities
	bedUse              *beduse.Capabilities
	growerCrop          *growercrop.Capabilities
	claimBuilding       *claimbuilding.Capabilities
	openCasket          *buildingruntime.OpenCasketCapabilities
	wallRemoval         *buildingruntime.WallRemovalCapabilities
	presentationMedia   *bridge.PresentationMedia
	lifecycle           lifecycleCapability
}
type buildingServiceOpener func(context.Context, bridge.ProcessConfig) (buildingServiceBridge, error)
type ownedAuthority struct {
	*bridge.Client
	*bridge.AuthorityControl
}

// lifecycleCapability composes the two separately held lifecycle mutations
// (Save, Load) into the single httpapi.LifecycleWriter shape; neither embedded
// capability grants the other's authority.
type lifecycleCapability struct {
	*bridge.LifecycleSave
	*bridge.LifecycleLoad
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
	acquisitionWriter, err := bridge.NewAcquisitionControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	attack, err := bridge.NewAttackControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	pawnOrder, err := bridge.NewPawnOrderControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	gearReplace, err := bridge.NewGearReplaceWriter(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	moveBuildingWriter, err := bridge.NewMoveBuildingControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	moodReliefWriter, err := bridge.NewMoodReliefWriter(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	actionsWriter, err := bridge.NewActionsWriter(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	buildingTemperatureControl, err := bridge.NewBuildingTemperatureControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	bedUseControl, err := bridge.NewBedUseControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	growerCropControl, err := bridge.NewGrowerCropControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	claimBuildingControl, err := bridge.NewClaimBuildingControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	wallRemovalWriter, err := bridge.NewWallRemovalWriter(client)
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
	return buildingServiceBridge{reads: client, native: client, authority: ownedAuthority{client, authority}, writes: actionsWriter, moodReliefWorld: client,
		acquisition:     &acquisition.AcquisitionCapabilities{Native: client, Writer: acquisitionWriter},
		mineAcquisition: &mineacquisition.MineAcquisitionCapabilities{Native: client, Writer: acquisitionWriter},
		moveBuilding:    &movebuilding.Capabilities{Native: client, Writer: moveBuildingWriter},
		clock:           &buildingruntime.ClockCapabilities{Native: client, Writer: clock}, clockReads: client,
		draft:               &draft.DraftCapabilities{Native: client, Writer: drafts, Cleanup: cleanup},
		melee:               &melee.MeleeCapabilities{Writer: actionsWriter},
		ranged:              &ranged.RangedCapabilities{Native: client, Writer: attack},
		movement:            &buildingruntime.MovementCapabilities{Writer: actionsWriter},
		tend:                &tend.TendCapabilities{Native: client, Writer: pawnOrder},
		rescue:              &rescue.RescueCapabilities{Native: client, Writer: pawnOrder},
		capture:             &capture.CaptureCapabilities{Native: client, Writer: pawnOrder},
		equip:               &equip.EquipCapabilities{Native: client, Writer: pawnOrder},
		haul:                &haul.HaulCapabilities{Native: client, Writer: actionsWriter},
		repair:              &buildingruntime.RepairCapabilities{Native: client, Writer: pawnOrder},
		clean:               &buildingruntime.CleanCapabilities{Native: client, Writer: pawnOrder},
		moodRelief:          &buildingruntime.MoodReliefCapabilities{Native: client, Writer: moodReliefWriter},
		gearReplace:         &buildingruntime.GearReplaceCapabilities{Native: client, Writer: gearReplace},
		trade:               &buildingruntime.TradeCapabilities{Native: client, Writer: actionsWriter},
		buildingTemperature: &buildingtemperature.Capabilities{Native: client, Writer: buildingTemperatureControl},
		bedUse:              &beduse.Capabilities{Native: client, Writer: bedUseControl},
		growerCrop:          &growercrop.Capabilities{Native: client, Writer: growerCropControl},
		claimBuilding:       &claimbuilding.Capabilities{Native: client, Writer: claimBuildingControl},
		openCasket:          &buildingruntime.OpenCasketCapabilities{Native: client, Writer: pawnOrder},
		wallRemoval:         &buildingruntime.WallRemovalCapabilities{Native: client, Writer: wallRemovalWriter},
		presentationMedia:   presentationMedia,
		lifecycle:           lifecycleCapability{lifecycleSave, lifecycleLoad}}, nil
}

type buildingWorldSource struct{ reads observation.Source }

func (s buildingWorldSource) ReadExtentIdentity(ctx context.Context) (observation.Identity, error) {
	reply, _, err := s.reads.Tick(ctx)
	if err != nil {
		return observation.Identity{}, err
	}
	return observation.DecodeTick(reply)
}

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
	return config.routineSecureSuppliesPlans || config.routineHaulPlans || config.routineFoodStorageUpkeepPlans
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
	started, err := client.reads.GamesStart(lifetime)
	if err != nil {
		return err
	}
	if _, err = client.reads.ConnectWithPoll(lifetime, started); err != nil {
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
	}
	var acquisitionCapabilities *acquisition.AcquisitionCapabilities
	if config.routineAcquisitionPlans {
		if client.acquisition == nil {
			return errors.New("acquisition plans require typed capabilities")
		}
		acquisitionCapabilities = client.acquisition
	}
	var mineAcquisitionCapabilities *mineacquisition.MineAcquisitionCapabilities
	if config.routineResourcePlans || config.routineAnimalFeedPlans {
		if client.mineAcquisition == nil {
			return errors.New("resource and animal feed plans require typed mine acquisition capabilities")
		}
		mineAcquisitionCapabilities = client.mineAcquisition
	}
	var meleeCapabilities *melee.MeleeCapabilities
	var rangedCapabilities *ranged.RangedCapabilities
	var movementCapabilities *buildingruntime.MovementCapabilities
	if config.routineDefensePlans {
		if client.melee == nil || client.ranged == nil || client.movement == nil {
			return errors.New("defense plans require typed melee, ranged and movement capabilities")
		}
		meleeCapabilities, rangedCapabilities, movementCapabilities = client.melee, client.ranged, client.movement
	}
	var tendCapabilities *tend.TendCapabilities
	if config.routineTendPlans {
		if client.tend == nil {
			return errors.New("tend plans require typed capabilities")
		}
		tendCapabilities = client.tend
	}
	var rescueCapabilities *rescue.RescueCapabilities
	if config.routineRescuePlans || config.routinePopulationCustodyPlans {
		if client.rescue == nil {
			return errors.New("rescue plans require typed capabilities")
		}
		rescueCapabilities = client.rescue
	}
	var captureCapabilities *capture.CaptureCapabilities
	if config.routinePopulationCustodyPlans {
		if client.capture == nil {
			return errors.New("population custody plans require typed capture capabilities")
		}
		captureCapabilities = client.capture
	}
	var equipCapabilities *equip.EquipCapabilities
	if config.routineEquipPlans {
		if client.equip == nil {
			return errors.New("equip plans require typed capabilities")
		}
		equipCapabilities = client.equip
	}
	var haulCapabilities *haul.HaulCapabilities
	if haulExecutorRequired(config) {
		if client.haul == nil {
			return errors.New("secure supplies/haul plans require typed haul capabilities")
		}
		haulCapabilities = client.haul
	}
	var repairCapabilities *buildingruntime.RepairCapabilities
	if config.routineRepairPlans {
		if client.repair == nil {
			return errors.New("repair plans require typed repair capabilities")
		}
		repairCapabilities = client.repair
	}
	var cleanCapabilities *buildingruntime.CleanCapabilities
	if config.routineCleanPlans {
		if client.clean == nil {
			return errors.New("clean plans require typed clean capabilities")
		}
		cleanCapabilities = client.clean
	}
	var moodReliefCapabilities *buildingruntime.MoodReliefCapabilities
	if config.routineMoodPlans {
		if client.moodRelief == nil {
			return errors.New("mood plans require typed mood relief capabilities")
		}
		moodReliefCapabilities = client.moodRelief
		moodReliefCapabilities.Longitude = readMoodReliefLongitude(lifetime, client.reads, client.moodReliefWorld)
	}
	var gearReplaceCapabilities *buildingruntime.GearReplaceCapabilities
	if config.routineGearPlans {
		if client.gearReplace == nil {
			return errors.New("gear plans require typed capabilities")
		}
		gearReplaceCapabilities = client.gearReplace
	}
	var tradeCapabilities *buildingruntime.TradeCapabilities
	if config.routineTradePlans {
		if client.trade == nil {
			return errors.New("trade plans require typed capabilities")
		}
		tradeCapabilities = client.trade
	}
	// The refrigeration family patches cooler targets through the shared
	// executor.
	var buildingTemperatureCapabilities *buildingtemperature.Capabilities
	if config.routineRefrigerationPlans {
		if client.buildingTemperature == nil {
			return errors.New("refrigeration plans require typed capabilities")
		}
		buildingTemperatureCapabilities = client.buildingTemperature
	}
	// The hospital family patches beds medical through the shared executor.
	var bedUseCapabilities *beduse.Capabilities
	if config.routineHospitalPlans {
		if client.bedUse == nil {
			return errors.New("hospital plans require typed capabilities")
		}
		bedUseCapabilities = client.bedUse
	}
	// The field family re-crops plant growers through the shared executor.
	var growerCropCapabilities *growercrop.Capabilities
	if config.routineFieldPlans {
		if client.growerCrop == nil {
			return errors.New("field plans require typed capabilities")
		}
		growerCropCapabilities = client.growerCrop
	}
	// The shrine family claims empty caskets, and the initial shelter the
	// ruin walls on its ring (#718), through the shared executor.
	var claimBuildingCapabilities *claimbuilding.Capabilities
	if config.routineShrinePlans || config.routineShelterPlans {
		if client.claimBuilding == nil {
			return errors.New("shrine and shelter plans require typed claim capabilities")
		}
		claimBuildingCapabilities = client.claimBuilding
	}
	// ... and re-sites furniture through the game's Reinstall (#808).
	var moveBuildingCapabilities *movebuilding.Capabilities
	if config.routineTidyPlans {
		if client.moveBuilding == nil {
			return errors.New("tidy plans require typed move capabilities")
		}
		moveBuildingCapabilities = client.moveBuilding
	}
	// The shrine family opens filled caskets through the shared executor (#460).
	var openCasketCapabilities *buildingruntime.OpenCasketCapabilities
	if config.routineShrinePlans {
		if client.openCasket == nil {
			return errors.New("shrine plans require typed open casket capabilities")
		}
		openCasketCapabilities = client.openCasket
	}
	// The stone-shell family demolishes the flammable wall each bundle
	// replaces through the shared executor (typed RemoveWall, #293); without
	// the capability the bundle's demolition step never leaves pending.
	var wallRemovalCapabilities *buildingruntime.WallRemovalCapabilities
	if config.routineStoneShellPlans {
		if client.wallRemoval == nil {
			return errors.New("stone shell plans require typed capabilities")
		}
		wallRemovalCapabilities = client.wallRemoval
	}
	session, err := buildingruntime.NewSession(lifetime, buildingruntime.SessionConfig{RoutineMethods: config.routineMethods,
		Control:             buildingruntime.ControlConfig{ProfileDirectory: config.profile, CallTimeout: callTimeout, Worlds: buildingWorldSource{client.reads}},
		Executor:            executor.Limits{MaxAge: 5 * time.Second, RunTimeout: 8 * time.Second, JournalTimeout: 3 * time.Second},
		Acquisition:         acquisitionCapabilities,
		MineAcquisition:     mineAcquisitionCapabilities,
		Draft:               client.draft,
		Clock:               clockCapabilities,
		Melee:               meleeCapabilities,
		Ranged:              rangedCapabilities,
		Movement:            movementCapabilities,
		Tend:                tendCapabilities,
		Rescue:              rescueCapabilities,
		Capture:             captureCapabilities,
		Equip:               equipCapabilities,
		Haul:                haulCapabilities,
		Repair:              repairCapabilities,
		Clean:               cleanCapabilities,
		MoveBuilding:        moveBuildingCapabilities,
		MoodRelief:          moodReliefCapabilities,
		GearReplace:         gearReplaceCapabilities,
		Trade:               tradeCapabilities,
		BuildingTemperature: buildingTemperatureCapabilities,
		BedUse:              bedUseCapabilities,
		GrowerCrop:          growerCropCapabilities,
		ClaimBuilding:       claimBuildingCapabilities,
		OpenCasket:          openCasketCapabilities,
		WallRemoval:         wallRemovalCapabilities,
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
	// One wake signal joins the clock poll loop to the step loop and the
	// worker: committed journal evidence steps both at once.
	wake := buildingruntime.NewWakeSignal()
	// The decoded state store (#354): the scheduler's reviews
	// file their census sections, /api/routines reports them.
	var sections *factsstore.Store
	var advanced, windowRunning = func() {}, func() bool { return false }
	var stepTrace func() telemetry.Trace
	var validity func() (domain.ReadValidity, bool)
	if config.clockControl {
		sections = factsstore.NewStore()
		clockWorker, err := startServiceClock(lifetime, player, session, client.clockReads, database, config, serviceClockTimeouts(callTimeout), wake, sections)
		if err != nil {
			return err
		}
		advanced, windowRunning, stepTrace, validity = clockWorker.Nudge, clockWorker.WindowRunning, clockWorker.Trace, clockWorker.Validity
		player.SetReplan(clockWorker.Nudge)
	}
	breakSource, _ := client.reads.(buildingruntime.BreakResponseSource)
	pawns, _ := client.reads.(buildingruntime.ArrivalPawns)
	arrival := buildingruntime.WorkerConfig{Pawns: pawns}
	if client.movement != nil {
		arrival.Moves = client.movement.Writer
	}
	worker, err := buildingruntime.NewWorker(lifetime, buildingruntime.WorkerConfig{BreakSource: breakSource, Pawns: arrival.Pawns, Moves: arrival.Moves, RoutineMethods: config.routineMethods,
		StepInterval: time.Second, MaxBackoff: 10 * time.Second, StepTimeout: min(config.bridge.Timeout, 8*time.Second),
		RenewInterval: 5 * time.Second, RenewTimeout: 5 * time.Second, Wake: wake, Advanced: advanced, Store: sections, WindowRunning: windowRunning, Trace: stepTrace, Validity: validity,
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
	presentation, _ := client.reads.(httpapi.PresentationReader)
	notifications, _ := client.reads.(httpapi.NotificationReader)
	var clockReview httpapi.ClockReview
	var routines httpapi.RoutineProvider
	if config.clockControl {
		clockReview = serviceClockReview{database, config.profile}
	}
	if config.routineReviews {
		routines = serviceRoutineDiagnostics{journal: database, reviewsEnabled: config.routineReviews, methodsEnabled: config.routineMethods, families: config.activeRoutineFamilies(), sections: sections}
	}
	var worldEvaluation httpapi.WorldEvaluation
	if config.worldEvaluation {
		worldNative, ok := client.native.(buildingruntime.WorldEvaluationNative)
		if !ok {
			return errors.New("world evaluation requires typed world progression and colony fact observations")
		}
		if worldEvaluation, err = buildingruntime.NewWorldEvaluation(player, worldNative, policy.WorldEvaluationPolicy{TravelFoodMarginDays: worldEvaluationFoodMarginDays}); err != nil {
			return err
		}
	}
	// The live colony census route is a plain read every serve exposes when
	// the native client carries the typed reads it composes (#261).
	var colonyStatus httpapi.ColonyStatus
	if colonyNative, ok := client.native.(buildingruntime.ColonyStatusNative); ok {
		if colonyStatus, err = buildingruntime.NewColonyStatus(player, colonyNative, sections); err != nil {
			return err
		}
	}
	var attention httpapi.AttentionAcknowledger
	if raw, ok := client.reads.(*bridge.Client); ok {
		attention = attentionAcknowledger{raw}
	}
	server, err := httpapi.NewWithPlayer(httpapi.Config{ClockReview: clockReview, Routines: routines, WorldEvaluation: worldEvaluation, ColonyStatus: colonyStatus, Notifications: notifications, Presentation: presentation, PresentationMedia: client.presentationMedia, Lifecycle: client.lifecycle, Attention: attention, AssetsDir: config.assets, Pprof: config.pprof, FlightRecorder: config.flightRecorder, ReadTimeout: 35 * time.Second, ShutdownTimeout: 5 * time.Second, MaxResponseBytes: 1 << 20}, buildingSnapshots{reads, player}, database, player, database)
	if err != nil {
		return err
	}
	defer func() { result = errors.Join(result, server.Close()) }()
	if config.chat {
		raw, ok := client.reads.(*bridge.Client)
		if !ok {
			return errors.New("chat requires a live native bridge client")
		}
		modelClient, err := model.NewClient(model.Config{Model: config.chatModel, BaseURL: config.chatBaseURL, Timeout: 20 * time.Second, MaxResponseBytes: 1 << 20})
		if err != nil {
			return err
		}
		defer func() { result = errors.Join(result, modelClient.Close()) }()
		interp, err := interpreter.NewLocal(interpreter.Config{ContextTokens: chatContextTokens, MaxOutputTokens: chatMaxOutputTokens}, modelClient)
		if err != nil {
			return err
		}
		server.EnableChat(interp, session.ColonyFacts().Chat(raw), database)
	}
	pollDone = make(chan struct{})
	go func() { defer close(pollDone); reads.Poll(lifetime, config.refresh) }()
	superviseDone := make(chan struct{})
	defer func() { <-superviseDone }()
	go func() { defer close(superviseDone); superviseBridge(lifetime, client.reads, out) }()
	if native, ok := client.reads.(governorStateNative); ok {
		shadowDone := make(chan struct{})
		defer func() { <-shadowDone }()
		go func() { defer close(shadowDone); shadowGovernorState(lifetime, native, database, config.refresh, out) }()
	}
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
