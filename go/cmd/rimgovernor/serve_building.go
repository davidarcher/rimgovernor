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
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/bill"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/capture"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/equip"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/haul"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/melee"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/mineacquisition"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/ranged"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/rescue"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/supply"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/tend"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/work"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/zone"
	"github.com/davidarcher/RimGovernor/go/internal/controller"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// defaultCaravanDeparturePolicy is the fixed admission threshold for the
// unconditional player-command CaravanDeparture vertical: leave at least one
// colonist and five days of food home, and keep a doctor among the stayers.
// Not yet operator-configurable; no CLI flag exists for it.
var defaultCaravanDeparturePolicy = policy.CaravanDeparturePolicy{MinimumHomeColonists: 1, MinimumHomeFoodDays: 5, KeepHomeDoctor: true}

type buildingServiceBridge struct {
	bills               *bill.BillCapabilities
	reads               serviceBridge
	native              boundary.Native
	authority           buildingruntime.NativeAuthority
	writes              boundary.BuildingWriter
	acquisition         *acquisition.AcquisitionCapabilities
	mineAcquisition     *mineacquisition.MineAcquisitionCapabilities
	zones               *zone.ZoneCapabilities
	work                *work.WorkCapabilities
	supplies            *supply.SupplyCapabilities
	draft               *draft.DraftCapabilities
	clock               *buildingruntime.ClockCapabilities
	clockReads          serviceClockReads
	melee               *melee.MeleeCapabilities
	ranged              *ranged.RangedCapabilities
	tend                *tend.TendCapabilities
	rescue              *rescue.RescueCapabilities
	capture             *capture.CaptureCapabilities
	equip               *equip.EquipCapabilities
	haul                *haul.HaulCapabilities
	repair              *buildingruntime.RepairCapabilities
	clean               *buildingruntime.CleanCapabilities
	gearReplace         *buildingruntime.GearReplaceCapabilities
	recoveryService     *buildingruntime.RecoveryServiceCapabilities
	husbandry           *buildingruntime.HusbandryCapabilities
	prisonerInteraction *buildingruntime.PrisonerInteractionCapabilities
	research            *buildingruntime.ResearchSelectCapabilities
	questAccept         *buildingruntime.QuestAcceptCapabilities
	settlementGift      *buildingruntime.SettlementGiftCapabilities
	questFulfill        *buildingruntime.QuestFulfillCapabilities
	caravanDeparture    *buildingruntime.CaravanDepartureCapabilities
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
	bills, err := bridge.NewBillControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	zones, err := bridge.NewZoneControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	acquisitionWriter, err := bridge.NewAcquisitionControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	workWriter, err := bridge.NewWorkControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	supplies, err := bridge.NewSupplyControl(client)
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
	recoveryService, err := bridge.NewRecoveryServiceWriter(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	husbandryWriter, err := bridge.NewHusbandryWriter(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	prisonerInteractionWriter, err := bridge.NewPrisonerInteractionWriter(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	researchSelect, err := bridge.NewResearchSelectControl(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	questAccept, err := bridge.NewQuestAcceptWriter(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	settlementGift, err := bridge.NewSettlementGiftWriter(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	questFulfill, err := bridge.NewQuestFulfillWriter(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	caravanDeparture, err := bridge.NewCaravanDepartureWriter(client)
	if err != nil {
		return buildingServiceBridge{}, errors.Join(err, client.Close())
	}
	return buildingServiceBridge{reads: client, native: client, authority: ownedAuthority{client, authority}, writes: writes,
		bills:       &bill.BillCapabilities{Native: client, Writer: bills},
		zones:       &zone.ZoneCapabilities{Native: client, Writer: zones},
		acquisition:     &acquisition.AcquisitionCapabilities{Native: client, Writer: acquisitionWriter},
		mineAcquisition: &mineacquisition.MineAcquisitionCapabilities{Native: client, Writer: acquisitionWriter},
		work:        &work.WorkCapabilities{Native: client, Writer: workWriter},
		supplies:    &supply.SupplyCapabilities{Native: client, Writer: supplies},
		clock:       &buildingruntime.ClockCapabilities{Native: client, Writer: clock}, clockReads: client,
		draft:               &draft.DraftCapabilities{Native: client, Writer: drafts, Cleanup: cleanup},
		melee:               &melee.MeleeCapabilities{Native: client, Writer: attack},
		ranged:              &ranged.RangedCapabilities{Native: client, Writer: attack},
		tend:                &tend.TendCapabilities{Native: client, Writer: pawnOrder},
		rescue:              &rescue.RescueCapabilities{Native: client, Writer: pawnOrder},
		capture:             &capture.CaptureCapabilities{Native: client, Writer: pawnOrder},
		equip:               &equip.EquipCapabilities{Native: client, Writer: pawnOrder},
		haul:                &haul.HaulCapabilities{Native: client, Writer: pawnOrder},
		repair:              &buildingruntime.RepairCapabilities{Native: client, Writer: pawnOrder},
		clean:               &buildingruntime.CleanCapabilities{Native: client, Writer: pawnOrder},
		gearReplace:         &buildingruntime.GearReplaceCapabilities{Native: client, Writer: gearReplace},
		recoveryService:     &buildingruntime.RecoveryServiceCapabilities{Native: client, Writer: recoveryService},
		husbandry:           &buildingruntime.HusbandryCapabilities{Native: client, Writer: husbandryWriter},
		prisonerInteraction: &buildingruntime.PrisonerInteractionCapabilities{Native: client, Writer: prisonerInteractionWriter},
		research:            &buildingruntime.ResearchSelectCapabilities{Native: client, Writer: researchSelect},
		questAccept:         &buildingruntime.QuestAcceptCapabilities{Native: client, Writer: questAccept},
		settlementGift:      &buildingruntime.SettlementGiftCapabilities{Native: client, Writer: settlementGift},
		questFulfill:        &buildingruntime.QuestFulfillCapabilities{Native: client, Writer: questFulfill},
		caravanDeparture:    &buildingruntime.CaravanDepartureCapabilities{Native: client, Writer: caravanDeparture, Policy: defaultCaravanDeparturePolicy}}, nil
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
	if client.questAccept == nil || client.questAccept.Native == nil || client.questAccept.Writer == nil || client.settlementGift == nil || client.settlementGift.Native == nil || client.settlementGift.Writer == nil {
		return errors.New("player service requires complete quest accept and settlement gift capabilities")
	}
	if client.caravanDeparture == nil || client.caravanDeparture.Native == nil || client.caravanDeparture.Writer == nil {
		return errors.New("player service requires complete caravan departure capabilities")
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
	var supplyCapabilities *supply.SupplyCapabilities
	if config.routineSupplyPlans {
		if client.supplies == nil {
			return errors.New("supply plans require typed supply capabilities")
		}
		supplyCapabilities = client.supplies
	}
	var billCapabilities *bill.BillCapabilities
	if config.routineBillPlans {
		if client.bills == nil {
			return errors.New("bill plans require typed capabilities")
		}
		billCapabilities = client.bills
	}
	var zoneCapabilities *zone.ZoneCapabilities
	if config.routineFieldPlans || config.routineFoodStoragePlans {
		if client.zones == nil {
			return errors.New("field and food storage plans require typed capabilities")
		}
		zoneCapabilities = client.zones
	}
	var acquisitionCapabilities *acquisition.AcquisitionCapabilities
	if config.routineAcquisitionPlans {
		if client.acquisition == nil {
			return errors.New("acquisition plans require typed capabilities")
		}
		acquisitionCapabilities = client.acquisition
	}
	var mineAcquisitionCapabilities *mineacquisition.MineAcquisitionCapabilities
	if config.routineResourcePlans {
		if client.mineAcquisition == nil {
			return errors.New("resource plans require typed mine acquisition capabilities")
		}
		mineAcquisitionCapabilities = client.mineAcquisition
	}
	var workCapabilities *work.WorkCapabilities
	if config.routineWorkPlans {
		if client.work == nil {
			return errors.New("work plans require typed settings capabilities")
		}
		workCapabilities = client.work
	}
	var meleeCapabilities *melee.MeleeCapabilities
	var rangedCapabilities *ranged.RangedCapabilities
	if config.routineDefensePlans {
		if client.melee == nil || client.ranged == nil {
			return errors.New("defense plans require typed melee and ranged capabilities")
		}
		meleeCapabilities, rangedCapabilities = client.melee, client.ranged
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
	if config.routineSecureSuppliesPlans {
		if client.haul == nil {
			return errors.New("secure supplies plans require typed haul capabilities")
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
	var gearReplaceCapabilities *buildingruntime.GearReplaceCapabilities
	if config.routineGearPlans {
		if client.gearReplace == nil {
			return errors.New("gear plans require typed capabilities")
		}
		gearReplaceCapabilities = client.gearReplace
	}
	var recoveryServiceCapabilities *buildingruntime.RecoveryServiceCapabilities
	if config.routineRecoveryPlans {
		if client.recoveryService == nil {
			return errors.New("recovery plans require typed capabilities")
		}
		recoveryServiceCapabilities = client.recoveryService
	}
	var husbandryCapabilities *buildingruntime.HusbandryCapabilities
	if config.routineHusbandryPlans {
		if client.husbandry == nil {
			return errors.New("husbandry plans require typed capabilities")
		}
		husbandryCapabilities = client.husbandry
	}
	var prisonerInteractionCapabilities *buildingruntime.PrisonerInteractionCapabilities
	if config.routinePrisonerInteractionPlans {
		if client.prisonerInteraction == nil {
			return errors.New("prisoner interaction plans require typed capabilities")
		}
		prisonerInteractionCapabilities = client.prisonerInteraction
	}
	var researchSelectCapabilities *buildingruntime.ResearchSelectCapabilities
	if config.routineResearchTarget != "" {
		if client.research == nil {
			return errors.New("research plans require typed capabilities")
		}
		researchSelectCapabilities = client.research
	}
	session, err := buildingruntime.NewSession(lifetime, buildingruntime.SessionConfig{RoutineMethods: config.routineMethods,
		Rules:               config.resourceRules,
		Control:             buildingruntime.ControlConfig{ProfileDirectory: config.profile, LeaseDuration: 30 * time.Second, CallTimeout: callTimeout, Worlds: buildingWorldSource{client.reads}},
		Executor:            executor.Limits{MaxAge: 5 * time.Second, RunTimeout: 8 * time.Second, JournalTimeout: 3 * time.Second},
		Acquisition:         acquisitionCapabilities,
		MineAcquisition:     mineAcquisitionCapabilities,
		Zones:               zoneCapabilities,
		Bills:               billCapabilities,
		Work:                workCapabilities,
		Supplies:            supplyCapabilities,
		Draft:               client.draft,
		Clock:               clockCapabilities,
		Melee:               meleeCapabilities,
		Ranged:              rangedCapabilities,
		Tend:                tendCapabilities,
		Rescue:              rescueCapabilities,
		Capture:             captureCapabilities,
		Equip:               equipCapabilities,
		Haul:                haulCapabilities,
		Repair:              repairCapabilities,
		Clean:               cleanCapabilities,
		GearReplace:         gearReplaceCapabilities,
		RecoveryService:     recoveryServiceCapabilities,
		Husbandry:           husbandryCapabilities,
		PrisonerInteraction: prisonerInteractionCapabilities,
		ResearchSelect:      researchSelectCapabilities,
		QuestAccept:         client.questAccept,
		SettlementGift:      client.settlementGift,
		QuestFulfill:        client.questFulfill,
		CaravanDeparture:    client.caravanDeparture,
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
		if err = startServiceClock(lifetime, player, session, client.clockReads, database, config.profile, callTimeout, config.routineReviews, config.routineSleepingPlans, config.routineCookingPlans, config.routineShelterPlans, config.routineComfortPlans, config.routineExpansionPlans, config.routinePowerPlans, config.routineTemperaturePlans, config.routineProjectLimit, config.routineSupplyPlans, config.routineWorkPlans, config.routineAcquisitionPlans, config.routineDefensePlans, config.routineTendPlans, config.routineRescuePlans, config.routineEquipPlans, config.routineSecureSuppliesPlans, config.routineRepairPlans, config.routineCleanPlans, config.routineGearPlans, config.routineMedicalPlans, config.routineAnimalContainmentPlans, config.routineRecoveryPlans, config.routineHusbandryPlans, config.routineHomeCoveragePlans, config.caravanJourneyTracking, config.routineResearchTarget, config.routineResourceTargets.Map(), config.routineFieldPlans, config.routineBillPlans, config.routineFoodStoragePlans, config.routinePrisonerInteractionPlans, config.routinePopulationCustodyPlans, config.routineStoneShellPlans); err != nil {
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
