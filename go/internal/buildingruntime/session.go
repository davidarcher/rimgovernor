package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/acquisition"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/haul"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/melee"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/mineacquisition"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/ranged"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type SessionConfig struct {
	Acquisition     *acquisition.AcquisitionCapabilities
	RoutineMethods  bool
	Control         ControlConfig
	Executor        executor.Limits
	Draft           *draft.DraftCapabilities
	Clock           *ClockCapabilities
	Melee           *melee.MeleeCapabilities
	Haul            *haul.HaulCapabilities
	Ranged          *ranged.RangedCapabilities
	Movement        *MovementCapabilities
	Trade           *TradeCapabilities
	MineAcquisition *mineacquisition.MineAcquisitionCapabilities
}

// Session binds the single profile owner to one journal and executor. Its caller
// owns bridge/database handles and may close them only after Close succeeds.
// Only an explicit trusted player path may call Acquire or create submitted plans.
type Session struct {
	routineMethods bool
	colonyFacts    *ColonyFacts
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
			return fmt.Errorf("%w: UpdateAuthority: value.Enabled", ErrControl)
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
		err = errors.Join(err, drafts.run(ctx, false))
	}
	return err
}

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
	if !root.Enabled || !root.ObservationKnown {
		return "", fmt.Errorf("%w: Lease: !root.Enabled || !root.ObservationKnown", ErrControl)
	}
	ctx, cancel := context.WithTimeout(context.Background(), s.timeout)
	defer cancel()
	if err := (planAuthorizer{s.journal, s.routine}).AuthorizeRoutinePlan(ctx, root.Snapshot, target); err != nil {
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
		return "", fmt.Errorf("%w: Lease: control == nil", ErrControl)
	}
	root := control.State()
	if root.Snapshot == target {
		return control.Lease(target)
	}
	if !root.Enabled || !root.ObservationKnown {
		return "", fmt.Errorf("%w: Lease: !root.Enabled || !root.ObservationKnown", ErrControl)
	}
	ctx, cancel := context.WithTimeout(context.Background(), l.timeout)
	defer cancel()
	if err := (planAuthorizer{l.journal, l.routine}).AuthorizeRoutinePlan(ctx, root.Snapshot, target); err != nil {
		return "", err
	}
	return control.Lease(root.Snapshot)
}

func NewSession(ctx context.Context, config SessionConfig, journal *store.Store, native boundary.Native, authority NativeAuthority, writer boundary.BuildingWriter, clock executor.Clock) (*Session, error) {
	if journal == nil || native == nil || authority == nil || writer == nil || clock == nil {
		return nil, errors.New("building session dependencies required")
	}
	colonyFacts := &ColonyFacts{}
	if config.Draft != nil && (config.Draft.Native == nil || config.Draft.Writer == nil || config.Draft.Cleanup == nil) {
		return nil, errors.New("complete draft capabilities required")
	}
	if config.Melee != nil && (config.Melee.Writer == nil || config.Draft == nil) {
		return nil, errors.New("complete melee and draft capabilities required")
	}
	if config.Ranged != nil && (config.Ranged.Native == nil || config.Ranged.Writer == nil || config.Draft == nil) {
		return nil, errors.New("complete ranged and draft capabilities required")
	}
	if config.Movement != nil && (config.Movement.Writer == nil || config.Draft == nil) {
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
	config.Control.ResumeWrites = sink.resume
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
	place, err := boundary.NewBoundary(native, writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
	if err != nil {
		return cleanup(err)
	}
	var meleeBoundary *melee.MeleeBoundary
	if config.Melee != nil {
		meleeBoundary, err = melee.NewMeleeBoundary(place, config.Melee.Writer)
		if err != nil {
			return cleanup(err)
		}
	}
	var rangedBoundary *ranged.RangedAttackBoundary
	if config.Ranged != nil {
		if config.Ranged.Native == nil || config.Ranged.Writer == nil {
			return cleanup(fmt.Errorf("%w: NewSession: config.Ranged.Native == nil || config.Ranged.Writer == nil", ErrControl))
		}
		rangedBoundary, err = ranged.NewRangedBoundary(config.Ranged.Native, config.Ranged.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock, string(namespace))
		if err != nil {
			return cleanup(err)
		}
	}
	var moves *movementBoundary
	if config.Movement != nil {
		moves = &movementBoundary{Boundary: place, writer: config.Movement.Writer}
	}
	var worker *executor.Executor
	if config.Acquisition != nil && (config.Acquisition.Native == nil || config.Acquisition.Writer == nil) {
		return cleanup(fmt.Errorf("%w: NewSession: config.Acquisition != nil && (config.Acquisition.Native == nil || config.Acquisition.Writer == nil)", ErrControl))
	}
	if config.Haul != nil && (config.Haul.Native == nil || config.Haul.Writer == nil) {
		return cleanup(fmt.Errorf("%w: NewSession: config.Haul != nil && (config.Haul.Native == nil || config.Haul.Writer == nil)", ErrControl))
	}
	if config.Trade != nil && (config.Trade.Native == nil || config.Trade.Writer == nil) {
		return cleanup(fmt.Errorf("%w: NewSession: config.Trade != nil && (config.Trade.Native == nil || config.Trade.Writer == nil)", ErrControl))
	}

	if config.MineAcquisition != nil && (config.MineAcquisition.Native == nil || config.MineAcquisition.Writer == nil) {
		return cleanup(fmt.Errorf("%w: NewSession: config.MineAcquisition != nil && (config.MineAcquisition.Native == nil || config.MineAcquisition.Writer ==", ErrControl))
	}
	routine := []executor.RoutineScope{planAuthorizer{journal, config.RoutineMethods}}
	switch {
	case meleeBoundary != nil && rangedBoundary != nil && moves != nil:
		worker, err = executor.NewWithMeleeRangedAndMovement(journal, place, draftBoundary, meleeBoundary, rangedBoundary, moves, clock, config.Executor, routine...)
	case meleeBoundary != nil && rangedBoundary != nil:
		worker, err = executor.NewWithMeleeAndRanged(journal, place, draftBoundary, meleeBoundary, rangedBoundary, clock, config.Executor, routine...)
	case meleeBoundary != nil && moves != nil:
		worker, err = executor.NewWithMeleeAndMovement(journal, place, draftBoundary, meleeBoundary, moves, clock, config.Executor, routine...)
	case rangedBoundary != nil && moves != nil:
		worker, err = executor.NewWithRangedAndMovement(journal, place, draftBoundary, rangedBoundary, moves, clock, config.Executor, routine...)
	case meleeBoundary != nil:
		worker, err = executor.NewWithMelee(journal, place, draftBoundary, meleeBoundary, clock, config.Executor, routine...)
	case rangedBoundary != nil:
		worker, err = executor.NewWithRanged(journal, place, draftBoundary, rangedBoundary, clock, config.Executor, routine...)
	case moves != nil:
		worker, err = executor.NewWithMovement(journal, place, draftBoundary, moves, clock, config.Executor, routine...)
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
	if config.Trade != nil {
		if err := worker.EnableTrade(&tradeBoundary{Boundary: place, trade: *config.Trade}); err != nil {
			return cleanup(err)
		}
	}

	if config.Haul != nil {
		haulBoundary, err := haul.NewHaulBoundary(config.Haul.Native, config.Haul.Writer, sessionBuildingLeases{control, journal, config.RoutineMethods, config.Executor.JournalTimeout}, clock)
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableHaul(haulBoundary); err != nil {
			return cleanup(err)
		}
	}
	var drafts *draftSweep
	if draftBoundary != nil {
		drafts = &draftSweep{journal: journal, executor: worker, fights: draftBoundary, gate: make(chan struct{}, 1), timeout: config.Control.CallTimeout}
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
	return &Session{routineMethods: config.RoutineMethods, colonyFacts: colonyFacts, control: control, executor: worker, journal: journal, drafts: drafts, clock: coordinator, clockWorkers: sink.clockWorkers}, nil
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
func (s *Session) State() ControlState { return s.control.State() }
func (s *Session) HoldsGrant(scope domain.GenerationSnapshot) bool {
	return s.control.HoldsGrant(scope)
}
func (s *Session) Disable() error { return s.control.Disable() }

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
func (s *Session) Refresh(ctx context.Context) error   { return s.control.Refresh(ctx) }
func (s *Session) TargetsWorld(world store.World) bool { return s.control.TargetsWorld(world) }
func (s *Session) Manual(ctx context.Context) error {
	return s.control.Manual(ctx)
}
func (s *Session) ManualForResume(ctx context.Context) error {
	return s.control.ManualForResume(ctx)
}

// ReleaseClosedFights releases the draft claims of every combat fight that
// has closed (#910); the worker runs it each step.
func (s *Session) ReleaseClosedFights(ctx context.Context) error {
	if s.drafts == nil {
		return nil
	}
	return s.drafts.releaseClosedFights(ctx)
}
func (s *Session) Run(ctx context.Context, plan domain.PlanID, action domain.ActionID) (executor.Result, error) {
	if s.journal != nil {
		ctx = withPlanIntent(ctx, s.journal, plan)
	}
	return s.executor.Run(ctx, plan, action)
}

// RunBatch dispatches a plan's actions in one native Apply (#1042).
func (s *Session) RunBatch(ctx context.Context, plan domain.PlanID, actions []domain.ActionID) ([]executor.BatchItem, error) {
	if s.journal != nil {
		ctx = withPlanIntent(ctx, s.journal, plan)
	}
	return s.executor.RunBatch(ctx, plan, actions)
}
func (s *Session) Close(ctx context.Context) error { return s.control.Close(ctx) }

func (s *Session) RoutineMethodsEnabled() bool { return s.routineMethods }

// ColonyFacts serves the session's colony facts reads from the routine
// review's mirrored census once a scheduler binds its reviewer.
func (s *Session) ColonyFacts() *ColonyFacts { return s.colonyFacts }
