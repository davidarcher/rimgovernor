package buildingruntime

import (
	"context"
	"errors"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/acquisition"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/bill"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/buildingtemperature"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/capture"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/equip"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/haul"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/melee"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/mineacquisition"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/movement"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/ranged"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/rescue"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/supply"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/tend"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/work"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/zone"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type SessionConfig struct {
	Bills               *bill.BillCapabilities
	Zones               *zone.ZoneCapabilities
	Work                *work.WorkCapabilities
	Acquisition         *acquisition.AcquisitionCapabilities
	Supplies            *supply.SupplyCapabilities
	RoutineMethods      bool
	Control             ControlConfig
	Executor            executor.Limits
	Rules               []policy.ResourceRule
	Draft               *draft.DraftCapabilities
	Clock               *ClockCapabilities
	Melee               *melee.MeleeCapabilities
	Haul                *haul.HaulCapabilities
	Ranged              *ranged.RangedCapabilities
	Movement            *movement.MovementCapabilities
	Tend                *tend.TendCapabilities
	Rescue              *rescue.RescueCapabilities
	Capture             *capture.CaptureCapabilities
	Equip               *equip.EquipCapabilities
	GearReplace         *GearReplaceCapabilities
	Repair              *RepairCapabilities
	Clean               *CleanCapabilities
	RecoveryService     *RecoveryServiceCapabilities
	BedAssign           *BedAssignCapabilities
	Surgery             *SurgeryCapabilities
	ResearchSelect      *ResearchSelectCapabilities
	CaravanDeparture    *CaravanDepartureCapabilities
	Husbandry           *HusbandryCapabilities
	HomeCoverage        *HomeCoverageCapabilities
	PrisonerInteraction *PrisonerInteractionCapabilities
	QuestAccept         *QuestAcceptCapabilities
	SettlementGift      *SettlementGiftCapabilities
	QuestFulfill        *QuestFulfillCapabilities
	MineAcquisition     *mineacquisition.MineAcquisitionCapabilities
	ProductionPolicy    *ProductionPolicyCapabilities
	BuildingTemperature *buildingtemperature.Capabilities
}

// Session binds the single profile owner to one journal and executor. Its caller
// owns bridge/database handles and may close them only after Close succeeds.
// Only an explicit trusted player path may call Acquire or create submitted plans.
type Session struct {
	routineMethods bool
	rules          []policy.ResourceRule
	control        *Control
	executor       *executor.Executor
	journal        *store.Store
	drafts         *draftSweep
	clock          *ClockCoordinator
	clockWorkers   *clockWorkerSlot
}

type sessionSink struct {
	mu           sync.Mutex
	executor     *executor.Executor
	control      *Control
	drafts       *draftSweep
	clock        *ClockCoordinator
	clockWorkers *clockWorkerSlot
}

func (s *sessionSink) UpdateAuthority(value executor.Authority) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.executor == nil {
		if value.Enabled {
			return ErrControl
		}
		return nil
	}
	err := s.executor.UpdateAuthority(value)
	if s.clock != nil {
		err = errors.Join(err, s.clock.UpdateAuthority(value))
	}
	return err
}
func (s *sessionSink) stop(ctx context.Context) error {
	workerStopped, err := s.clockWorkers.stop(ctx)
	if err != nil {
		return err
	}
	s.mu.Lock()
	e := s.executor
	drafts := s.drafts
	clock := s.clock
	s.mu.Unlock()
	if e == nil {
		return nil
	}
	err = e.Stop(ctx)
	if clock != nil {
		err = errors.Join(err, clock.Stop(ctx))
	}
	if err != nil {
		return err
	}
	if clock != nil && !workerStopped {
		err = clock.Cleanup(ctx)
	}
	if drafts != nil {
		err = errors.Join(err, drafts.run(ctx))
	}
	return err
}

type sessionHolds struct{ journal *store.Store }

type sessionBuildingLeases struct {
	control *Control
	journal *store.Store
	routine bool
	timeout time.Duration
}

func (s sessionBuildingLeases) Lease(target domain.GenerationSnapshot) (string, error) {
	root := s.control.State()
	if root.Snapshot == target {
		return s.control.Lease(target)
	}
	if !s.routine || !root.Enabled || !root.ObservationKnown {
		return "", ErrControl
	}
	ctx, cancel := context.WithTimeout(context.Background(), s.timeout)
	defer cancel()
	if err := s.journal.AuthorizeRoutinePlan(ctx, root.Snapshot, target); err != nil {
		return "", err
	}
	return s.control.Lease(root.Snapshot)
}

// lazyRoutineLeases mirrors sessionBuildingLeases, but reads Control lazily
// through sink instead of holding a *Control directly. DraftBoundary must be
// constructed before Control exists (Control's own world source can be the
// draft boundary itself), so at construction time no *Control is available
// yet; by the time Lease is actually called (during dispatch), sink.control
// has been published.
type lazyRoutineLeases struct {
	sink    *sessionSink
	journal *store.Store
	routine bool
	timeout time.Duration
}

func (l lazyRoutineLeases) Lease(target domain.GenerationSnapshot) (string, error) {
	l.sink.mu.Lock()
	control := l.sink.control
	l.sink.mu.Unlock()
	if control == nil {
		return "", ErrControl
	}
	root := control.State()
	if root.Snapshot == target {
		return control.Lease(target)
	}
	if !l.routine || !root.Enabled || !root.ObservationKnown {
		return "", ErrControl
	}
	ctx, cancel := context.WithTimeout(context.Background(), l.timeout)
	defer cancel()
	if err := l.journal.AuthorizeRoutinePlan(ctx, root.Snapshot, target); err != nil {
		return "", err
	}
	return control.Lease(root.Snapshot)
}

func (s sessionHolds) Holds(ctx context.Context, current domain.GenerationSnapshot) ([]policy.Reservation, error) {
	return executor.ExternalHolds(ctx, s.journal, current)
}

func NewSession(ctx context.Context, config SessionConfig, journal *store.Store, native boundary.Native, authority NativeAuthority, writer boundary.BuildingWriter, clock executor.Clock) (*Session, error) {
	if journal == nil || native == nil || authority == nil || writer == nil || clock == nil {
		return nil, errors.New("building session dependencies required")
	}
	if config.Draft != nil && (config.Draft.Native == nil || config.Draft.Writer == nil || config.Draft.Cleanup == nil) {
		return nil, errors.New("complete draft capabilities required")
	}
	if config.Melee != nil && (config.Melee.Native == nil || config.Melee.Writer == nil || config.Draft == nil) {
		return nil, errors.New("complete melee and draft capabilities required")
	}
	if config.Ranged != nil && (config.Ranged.Native == nil || config.Ranged.Writer == nil || config.Draft == nil) {
		return nil, errors.New("complete ranged and draft capabilities required")
	}
	if config.Movement != nil && (config.Movement.Native == nil || config.Movement.Writer == nil || config.Draft == nil) {
		return nil, errors.New("complete movement and draft capabilities required")
	}
	if config.Clock != nil && (config.Clock.Native == nil || config.Clock.Writer == nil) {
		return nil, errors.New("complete clock capabilities required")
	}
	if config.Clock == nil {
		if err := requireNoClockObligations(ctx, journal); err != nil {
			return nil, err
		}
	}
	sink := &sessionSink{clockWorkers: &clockWorkerSlot{}}
	namespace, err := journal.Identity(ctx)
	if err != nil {
		return nil, err
	}
	var draftBoundary *draft.DraftBoundary
	if config.Draft != nil {
		draftBoundary, err = draft.NewDraftBoundary(config.Draft.Native, config.Draft.Writer, config.Draft.Cleanup, lazyRoutineLeases{sink, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return nil, err
		}
		if config.Control.Worlds == nil {
			config.Control.Worlds = draftBoundary
		}
	}
	config.Control.StopWrites = sink.stop
	config.Control.CleanupWrites = sink.cleanup
	if config.Control.Worlds == nil && config.Clock != nil {
		config.Control.Worlds = clockWorldSource{config.Clock.Native}
	}
	control, err := NewControl(ctx, config.Control, journal, authority, sink)
	if err != nil {
		return nil, err
	}
	cleanup := func(cause error) (*Session, error) {
		shutdown, cancel := context.WithTimeout(context.Background(), config.Control.CallTimeout)
		defer cancel()
		return nil, errors.Join(cause, control.Close(shutdown))
	}
	sink.mu.Lock()
	sink.control = control
	sink.mu.Unlock()
	place, err := boundary.NewBoundary(native, writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, sessionHolds{journal}, clock, string(namespace), config.Rules)
	if err != nil {
		return cleanup(err)
	}
	var meleeBoundary *melee.MeleeBoundary
	if config.Melee != nil {
		meleeBoundary, err = melee.NewMeleeBoundary(config.Melee.Native, config.Melee.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
	}
	var rangedBoundary *ranged.RangedAttackBoundary
	if config.Ranged != nil {
		if config.Ranged.Native == nil || config.Ranged.Writer == nil {
			return cleanup(ErrControl)
		}
		rangedBoundary, err = ranged.NewRangedBoundary(config.Ranged.Native, config.Ranged.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
	}
	var movementBoundary *movement.MovementBoundary
	if config.Movement != nil {
		if config.Movement.Native == nil || config.Movement.Writer == nil {
			return cleanup(ErrControl)
		}
		movementBoundary, err = movement.NewMovementBoundary(config.Movement.Native, config.Movement.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
	}
	var worker *executor.Executor
	if config.Supplies != nil && (config.Supplies.Native == nil || config.Supplies.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.BuildingTemperature != nil && (config.BuildingTemperature.Native == nil || config.BuildingTemperature.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Work != nil && (config.Work.Native == nil || config.Work.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Acquisition != nil && (config.Acquisition.Native == nil || config.Acquisition.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Zones != nil && (config.Zones.Native == nil || config.Zones.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Bills != nil && (config.Bills.Native == nil || config.Bills.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Haul != nil && (config.Haul.Native == nil || config.Haul.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Tend != nil && (config.Tend.Native == nil || config.Tend.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Rescue != nil && (config.Rescue.Native == nil || config.Rescue.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Equip != nil && (config.Equip.Native == nil || config.Equip.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.GearReplace != nil && (config.GearReplace.Native == nil || config.GearReplace.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Repair != nil && (config.Repair.Native == nil || config.Repair.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Clean != nil && (config.Clean.Native == nil || config.Clean.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.RecoveryService != nil && (config.RecoveryService.Native == nil || config.RecoveryService.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.BedAssign != nil && (config.BedAssign.Native == nil || config.BedAssign.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Surgery != nil && (config.Surgery.Native == nil || config.Surgery.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.ResearchSelect != nil && (config.ResearchSelect.Native == nil || config.ResearchSelect.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.CaravanDeparture != nil && (config.CaravanDeparture.Native == nil || config.CaravanDeparture.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.Husbandry != nil && (config.Husbandry.Native == nil || config.Husbandry.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.HomeCoverage != nil && (config.HomeCoverage.Native == nil || config.HomeCoverage.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.PrisonerInteraction != nil && (config.PrisonerInteraction.Native == nil || config.PrisonerInteraction.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.QuestAccept != nil && (config.QuestAccept.Native == nil || config.QuestAccept.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.SettlementGift != nil && (config.SettlementGift.Native == nil || config.SettlementGift.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.QuestFulfill != nil && (config.QuestFulfill.Native == nil || config.QuestFulfill.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.MineAcquisition != nil && (config.MineAcquisition.Native == nil || config.MineAcquisition.Writer == nil) {
		return cleanup(ErrControl)
	}
	if config.ProductionPolicy != nil && (config.ProductionPolicy.Native == nil || config.ProductionPolicy.Writer == nil) {
		return cleanup(ErrControl)
	}
	var routine []executor.RoutineScope
	if config.RoutineMethods {
		routine = append(routine, journal)
	}
	switch {
	case meleeBoundary != nil && rangedBoundary != nil && movementBoundary != nil:
		worker, err = executor.NewWithMeleeRangedAndMovement(journal, place, draftBoundary, meleeBoundary, rangedBoundary, movementBoundary, clock, config.Executor, routine...)
	case meleeBoundary != nil && rangedBoundary != nil:
		worker, err = executor.NewWithMeleeAndRanged(journal, place, draftBoundary, meleeBoundary, rangedBoundary, clock, config.Executor, routine...)
	case meleeBoundary != nil && movementBoundary != nil:
		worker, err = executor.NewWithMeleeAndMovement(journal, place, draftBoundary, meleeBoundary, movementBoundary, clock, config.Executor, routine...)
	case rangedBoundary != nil && movementBoundary != nil:
		worker, err = executor.NewWithRangedAndMovement(journal, place, draftBoundary, rangedBoundary, movementBoundary, clock, config.Executor, routine...)
	case meleeBoundary != nil:
		worker, err = executor.NewWithMelee(journal, place, draftBoundary, meleeBoundary, clock, config.Executor, routine...)
	case rangedBoundary != nil:
		worker, err = executor.NewWithRanged(journal, place, draftBoundary, rangedBoundary, clock, config.Executor, routine...)
	case movementBoundary != nil:
		worker, err = executor.NewWithMovement(journal, place, draftBoundary, movementBoundary, clock, config.Executor, routine...)
	case draftBoundary != nil:
		worker, err = executor.NewWithDraft(journal, place, draftBoundary, clock, config.Executor, routine...)
	default:
		worker, err = executor.New(journal, place, clock, config.Executor, routine...)
	}
	if err != nil {
		return cleanup(err)
	}
	// Each optional capability is wired directly onto worker with its own
	// typed boundary value, rather than composed into a single value for
	// executor.New to discover by type assertion — see executor.EnableAcquisition
	// for why the composed-value approach was unsafe.
	if config.Supplies != nil {
		if err := worker.EnableSupply(supply.NewSupplyBoundary(place, *config.Supplies)); err != nil {
			return cleanup(err)
		}
	}
	if config.Work != nil {
		if err := worker.EnableWork(work.NewWorkBoundary(place, *config.Work)); err != nil {
			return cleanup(err)
		}
	}
	if config.BuildingTemperature != nil {
		if err := worker.EnableBuildingTemperature(buildingtemperature.NewBoundary(place, *config.BuildingTemperature)); err != nil {
			return cleanup(err)
		}
	}
	if config.Acquisition != nil {
		if err := worker.EnableAcquisition(acquisition.NewAcquisitionBoundary(place, *config.Acquisition)); err != nil {
			return cleanup(err)
		}
	}
	if config.MineAcquisition != nil {
		if err := worker.EnableMineAcquisition(mineacquisition.NewMineAcquisitionBoundary(place, *config.MineAcquisition)); err != nil {
			return cleanup(err)
		}
	}
	if config.Zones != nil {
		if err := worker.EnableZone(zone.NewZoneBoundary(place, *config.Zones, journal)); err != nil {
			return cleanup(err)
		}
	}
	if config.Bills != nil {
		if err := worker.EnableBill(bill.NewBillBoundary(place, *config.Bills)); err != nil {
			return cleanup(err)
		}
	}
	if config.ResearchSelect != nil {
		if err := worker.EnableResearchSelect(&researchSelectBoundary{Boundary: place, research: *config.ResearchSelect}); err != nil {
			return cleanup(err)
		}
	}
	if config.ProductionPolicy != nil {
		if err := worker.EnableProductionPolicy(&productionPolicyBoundary{Boundary: place, production: *config.ProductionPolicy}); err != nil {
			return cleanup(err)
		}
	}
	if config.Haul != nil {
		haulBoundary, err := haul.NewHaulBoundary(config.Haul.Native, config.Haul.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableHaul(haulBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.Tend != nil {
		tendBoundary, err := tend.NewTendBoundary(config.Tend.Native, config.Tend.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableTend(tendBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.Rescue != nil {
		rescueBoundary, err := rescue.NewRescueBoundary(config.Rescue.Native, config.Rescue.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableRescue(rescueBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.Capture != nil {
		captureBoundary, err := capture.NewCaptureBoundary(config.Capture.Native, config.Capture.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableCapture(captureBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.Equip != nil {
		equipBoundary, err := equip.NewEquipBoundary(config.Equip.Native, config.Equip.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableEquip(equipBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.GearReplace != nil {
		gearReplaceBoundary, err := NewGearReplaceBoundary(config.GearReplace.Native, config.GearReplace.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableGearReplace(gearReplaceBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.Repair != nil {
		repairBoundary, err := NewRepairBoundary(config.Repair.Native, config.Repair.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableRepair(repairBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.Clean != nil {
		cleanBoundary, err := NewCleanBoundary(config.Clean.Native, config.Clean.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableClean(cleanBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.RecoveryService != nil {
		recoveryServiceBoundary, err := NewRecoveryServiceBoundary(config.RecoveryService.Native, config.RecoveryService.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableRecoveryService(recoveryServiceBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.BedAssign != nil {
		bedAssignBoundary, err := NewBedAssignBoundary(config.BedAssign.Native, config.BedAssign.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableBedAssign(bedAssignBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.Surgery != nil {
		surgeryBoundary, err := NewSurgeryBoundary(config.Surgery.Native, config.Surgery.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableSurgery(surgeryBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.CaravanDeparture != nil {
		caravanDepartureBoundary, err := NewCaravanDepartureBoundary(config.CaravanDeparture.Native, config.CaravanDeparture.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace), config.CaravanDeparture.Policy)
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableCaravanDeparture(caravanDepartureBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.Husbandry != nil {
		husbandryBoundary, err := NewHusbandryBoundary(config.Husbandry.Native, config.Husbandry.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableHusbandry(husbandryBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.HomeCoverage != nil {
		homeCoverageBoundary, err := NewHomeCoverageBoundary(config.HomeCoverage.Native, config.HomeCoverage.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableHomeCoverage(homeCoverageBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.PrisonerInteraction != nil {
		prisonerInteractionBoundary, err := NewPrisonerInteractionBoundary(config.PrisonerInteraction.Native, config.PrisonerInteraction.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnablePrisonerInteraction(prisonerInteractionBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.QuestAccept != nil {
		questAcceptBoundary, err := NewQuestAcceptBoundary(config.QuestAccept.Native, config.QuestAccept.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableQuestAccept(questAcceptBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.SettlementGift != nil {
		settlementGiftBoundary, err := NewSettlementGiftBoundary(config.SettlementGift.Native, config.SettlementGift.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableSettlementGift(settlementGiftBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.QuestFulfill != nil {
		questFulfillBoundary, err := NewQuestFulfillBoundary(config.QuestFulfill.Native, config.QuestFulfill.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableQuestFulfill(questFulfillBoundary); err != nil {
			return cleanup(err)
		}
	}
	var drafts *draftSweep
	if draftBoundary != nil {
		drafts = &draftSweep{journal: journal, executor: worker, gate: make(chan struct{}, 1), timeout: config.Control.CallTimeout}
	}
	var coordinator *ClockCoordinator
	if config.Clock != nil {
		coordinator, err = NewClockCoordinator(journal, config.Clock.Native, config.Clock.Writer, sink, clock, ClockCoordinatorConfig{CallTimeout: config.Control.CallTimeout, JournalTimeout: config.Executor.JournalTimeout})
		if err != nil {
			return cleanup(err)
		}
	}
	if err = sink.attach(ctx, worker, drafts, coordinator); err != nil {
		if coordinator != nil {
			_ = coordinator.Stop(context.Background())
		}
		return cleanup(err)
	}
	return &Session{routineMethods: config.RoutineMethods, rules: append([]policy.ResourceRule(nil), config.Rules...), control: control, executor: worker, journal: journal, drafts: drafts, clock: coordinator, clockWorkers: sink.clockWorkers}, nil
}

// Publish only after the final fallible construction check. Until publication,
// failure cleanup releases the owner without draining unrelated durable work.
func (s *sessionSink) attach(ctx context.Context, worker *executor.Executor, drafts *draftSweep, clock *ClockCoordinator) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := ctx.Err(); err != nil {
		return err
	}
	s.executor = worker
	s.drafts = drafts
	s.clock = clock
	return nil
}

// Acquire binds explicit intent to the exact durable plan revision. A proposal
// without a stored plan cannot obtain a write lease.
func (s *Session) Acquire(ctx context.Context, requested domain.GenerationSnapshot) (domain.GenerationSnapshot, error) {
	state, err := s.journal.LoadPlan(ctx, requested.Plan)
	if err != nil {
		return domain.GenerationSnapshot{}, err
	}
	if state.Spec.Revision() != requested.Revision {
		return domain.GenerationSnapshot{}, executor.ErrAuthority
	}
	return s.control.Acquire(ctx, requested)
}
func (s *Session) Renew(ctx context.Context) error { return s.control.Renew(ctx) }
func (s *Session) State() ControlState             { return s.control.State() }
func (s *Session) Disable() error                  { return s.control.Disable() }

// ObserveTarget attaches only read reconciliation to a durable plan. It cannot
// obtain a lease, even if native status reports an active owner for this namespace.
func (s *Session) ObserveTarget(ctx context.Context, requested domain.GenerationSnapshot) error {
	state, err := s.journal.LoadPlan(ctx, requested.Plan)
	if err != nil {
		return err
	}
	if state.Spec.Revision() != requested.Revision {
		return executor.ErrAuthority
	}
	return s.control.ObserveTarget(ctx, requested)
}
func (s *Session) Refresh(ctx context.Context) error { return s.control.Refresh(ctx) }
func (s *Session) Manual(ctx context.Context) error {
	return s.control.Manual(ctx)
}
func (s *Session) Run(ctx context.Context, plan domain.PlanID, action domain.ActionID) (executor.Result, error) {
	return s.executor.Run(ctx, plan, action)
}
func (s *Session) Close(ctx context.Context) error { return s.control.Close(ctx) }

func (s *Session) RoutineMethodsEnabled() bool { return s.routineMethods }
func (s *Session) ResourceRules() []policy.ResourceRule {
	return append([]policy.ResourceRule(nil), s.rules...)
}
