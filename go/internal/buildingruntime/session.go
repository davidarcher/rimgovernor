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
		draft, err = NewDraftBoundary(config.Draft.Native, config.Draft.Writer, config.Draft.Cleanup, sink, clock, string(namespace))
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
		melee, err = NewMeleeBoundary(config.Melee.Native, config.Melee.Writer, sink, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
	}
	var worker *executor.Executor
	var executionBoundary executor.Boundary = boundary
	if config.Supplies != nil {
		if config.Supplies.Native == nil || config.Supplies.Writer == nil {
			return cleanup(ErrControl)
		}
		executionBoundary = &supplyBoundary{Boundary: boundary, supply: *config.Supplies}
	}
	if config.Work != nil {
		if config.Work.Native == nil || config.Work.Writer == nil {
			return cleanup(ErrControl)
		}
		work := &workBoundary{Boundary: boundary, work: *config.Work}
		if supplies, ok := executionBoundary.(*supplyBoundary); ok {
			executionBoundary = &workSupplyBoundary{supplyBoundary: supplies, workExecutor: work}
		} else {
			executionBoundary = work
		}
	}
	if config.Acquisition != nil {
		if config.Acquisition.Native == nil || config.Acquisition.Writer == nil {
			return cleanup(ErrControl)
		}
		executionBoundary = withAcquisition(executionBoundary, &acquisitionBoundary{Boundary: boundary, acquisition: *config.Acquisition})
	}
	if config.Zones != nil {
		if config.Zones.Native == nil || config.Zones.Writer == nil {
			return cleanup(ErrControl)
		}
		executionBoundary = withZone(executionBoundary, &zoneBoundary{Boundary: boundary, zone: *config.Zones, journal: journal})
	}
	if config.Bills != nil {
		if config.Bills.Native == nil || config.Bills.Writer == nil {
			return cleanup(ErrControl)
		}
		executionBoundary = withBill(executionBoundary, &billBoundary{Boundary: boundary, bill: *config.Bills})
	}
	if config.Haul != nil {
		if config.Haul.Native == nil || config.Haul.Writer == nil {
			return cleanup(ErrControl)
		}
		haulBoundary, err := NewHaulBoundary(config.Haul.Native, config.Haul.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
		executionBoundary = withHaul(executionBoundary, haulBoundary)
	}
	var routine []executor.RoutineScope
	if config.RoutineMethods {
		routine = append(routine, journal)
	}
	if melee != nil {
		worker, err = executor.NewWithMelee(journal, executionBoundary, draft, melee, clock, config.Executor, routine...)
	} else if draft != nil {
		worker, err = executor.NewWithDraft(journal, executionBoundary, draft, clock, config.Executor, routine...)
	} else {
		worker, err = executor.New(journal, executionBoundary, clock, config.Executor, routine...)
	}
	if err != nil {
		return cleanup(err)
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
