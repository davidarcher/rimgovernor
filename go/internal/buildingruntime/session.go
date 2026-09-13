package buildingruntime

import (
	"context"
	"errors"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type SessionConfig struct {
	Bills          *BillCapabilities
	Zones          *ZoneCapabilities
	Work           *WorkCapabilities
	Acquisition    *AcquisitionCapabilities
	Supplies       *SupplyCapabilities
	RoutineMethods bool
	Control        ControlConfig
	Executor       executor.Limits
	Rules          []policy.ResourceRule
	Draft          *DraftCapabilities
	Clock          *ClockCapabilities
	Melee          *MeleeCapabilities
	Haul           *HaulCapabilities
	Ranged         *RangedCapabilities
	Tend           *TendCapabilities
	Rescue         *RescueCapabilities
	Equip          *EquipCapabilities
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

func NewSession(ctx context.Context, config SessionConfig, journal *store.Store, native Native, authority NativeAuthority, writer BuildingWriter, clock executor.Clock) (*Session, error) {
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
	var draft *DraftBoundary
	if config.Draft != nil {
		draft, err = NewDraftBoundary(config.Draft.Native, config.Draft.Writer, config.Draft.Cleanup, lazyRoutineLeases{sink, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return nil, err
		}
		if config.Control.Worlds == nil {
			config.Control.Worlds = draft
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
	boundary, err := NewBoundary(native, writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, sessionHolds{journal}, clock, string(namespace), config.Rules)
	if err != nil {
		return cleanup(err)
	}
	var melee *MeleeBoundary
	if config.Melee != nil {
		melee, err = NewMeleeBoundary(config.Melee.Native, config.Melee.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
	}
	var ranged *RangedAttackBoundary
	if config.Ranged != nil {
		if config.Ranged.Native == nil || config.Ranged.Writer == nil {
			return cleanup(ErrControl)
		}
		ranged, err = NewRangedBoundary(config.Ranged.Native, config.Ranged.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
	}
	var worker *executor.Executor
	if config.Supplies != nil && (config.Supplies.Native == nil || config.Supplies.Writer == nil) {
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
	var routine []executor.RoutineScope
	if config.RoutineMethods {
		routine = append(routine, journal)
	}
	switch {
	case melee != nil && ranged != nil:
		worker, err = executor.NewWithMeleeAndRanged(journal, boundary, draft, melee, ranged, clock, config.Executor, routine...)
	case melee != nil:
		worker, err = executor.NewWithMelee(journal, boundary, draft, melee, clock, config.Executor, routine...)
	case ranged != nil:
		worker, err = executor.NewWithRanged(journal, boundary, draft, ranged, clock, config.Executor, routine...)
	case draft != nil:
		worker, err = executor.NewWithDraft(journal, boundary, draft, clock, config.Executor, routine...)
	default:
		worker, err = executor.New(journal, boundary, clock, config.Executor, routine...)
	}
	if err != nil {
		return cleanup(err)
	}
	// Each optional capability is wired directly onto worker with its own
	// typed boundary value, rather than composed into a single value for
	// executor.New to discover by type assertion — see executor.EnableAcquisition
	// for why the composed-value approach was unsafe.
	if config.Supplies != nil {
		if err := worker.EnableSupply(&supplyBoundary{Boundary: boundary, supply: *config.Supplies}); err != nil {
			return cleanup(err)
		}
	}
	if config.Work != nil {
		if err := worker.EnableWork(&workBoundary{Boundary: boundary, work: *config.Work}); err != nil {
			return cleanup(err)
		}
	}
	if config.Acquisition != nil {
		if err := worker.EnableAcquisition(&acquisitionBoundary{Boundary: boundary, acquisition: *config.Acquisition}); err != nil {
			return cleanup(err)
		}
	}
	if config.Zones != nil {
		if err := worker.EnableZone(&zoneBoundary{Boundary: boundary, zone: *config.Zones, journal: journal}); err != nil {
			return cleanup(err)
		}
	}
	if config.Bills != nil {
		if err := worker.EnableBill(&billBoundary{Boundary: boundary, bill: *config.Bills}); err != nil {
			return cleanup(err)
		}
	}
	if config.Haul != nil {
		haulBoundary, err := NewHaulBoundary(config.Haul.Native, config.Haul.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableHaul(haulBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.Tend != nil {
		tendBoundary, err := NewTendBoundary(config.Tend.Native, config.Tend.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableTend(tendBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.Rescue != nil {
		rescueBoundary, err := NewRescueBoundary(config.Rescue.Native, config.Rescue.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableRescue(rescueBoundary); err != nil {
			return cleanup(err)
		}
	}
	if config.Equip != nil {
		equipBoundary, err := NewEquipBoundary(config.Equip.Native, config.Equip.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableEquip(equipBoundary); err != nil {
			return cleanup(err)
		}
	}
	var drafts *draftSweep
	if draft != nil {
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
