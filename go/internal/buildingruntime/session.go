package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/haul"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type SessionConfig struct {
	RoundsMethods bool
	Control       ControlConfig
	Executor      executor.Limits
	Clock         *ClockCapabilities
	Haul          *haul.HaulCapabilities
	Movement      *MovementCapabilities
	Trade         *TradeCapabilities
}

// Session binds the single profile owner to one journal and executor. Its caller
// owns bridge/database handles and may close them only after Close succeeds.
// Only an explicit trusted player path may call Acquire or create submitted plans.
type Session struct {
	roundsMethods bool
	control       *Control
	executor      *executor.Executor
	journal       *store.Store
	clock         *ClockCoordinator
	clockWorkers  *clockWorkerSlot
}

type sessionSink struct {
	mu           sync.Mutex
	executor     *executor.Executor
	control      *Control
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

// Lease is a construction-time relay; native calls never occur under sink.mu.
func (s *sessionSink) Lease(snapshot domain.GenerationSnapshot) (string, error) {
	s.mu.Lock()
	control := s.control
	s.mu.Unlock()
	if control == nil {
		return "", fmt.Errorf("%w: Lease: control == nil", ErrControl)
	}
	return control.Lease(snapshot)
}

func (s *sessionSink) stop(ctx context.Context) error {
	workerStopped, err := s.clockWorkers.stop(ctx)
	if err != nil {
		return err
	}
	s.mu.Lock()
	e := s.executor
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
	if err := (planAuthorizer{s.journal, s.routine}).AuthorizeRoundsPlan(ctx, root.Snapshot, target); err != nil {
		return "", err
	}
	return s.control.Lease(root.Snapshot)
}

func NewSession(ctx context.Context, config SessionConfig, journal *store.Store, native boundary.Native, authority NativeAuthority, writer boundary.BuildingWriter, clock executor.Clock) (*Session, error) {
	if journal == nil || native == nil || authority == nil || writer == nil || clock == nil {
		return nil, errors.New("building session dependencies required")
	}
	if config.Movement != nil && config.Movement.Writer == nil {
		return nil, errors.New("complete movement capabilities required")
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
	place, err := boundary.NewBoundary(native, writer, sessionBuildingLeases{control, journal, config.RoundsMethods, config.Executor.JournalTimeout}, clock, string(namespace))
	if err != nil {
		return cleanup(err)
	}
	var moves *movementBoundary
	if config.Movement != nil {
		moves = &movementBoundary{Boundary: place, writer: config.Movement.Writer}
	}
	var worker *executor.Executor
	if config.Haul != nil && (config.Haul.Native == nil || config.Haul.Writer == nil) {
		return cleanup(fmt.Errorf("%w: NewSession: config.Haul != nil && (config.Haul.Native == nil || config.Haul.Writer == nil)", ErrControl))
	}
	if config.Trade != nil && (config.Trade.Native == nil || config.Trade.Writer == nil) {
		return cleanup(fmt.Errorf("%w: NewSession: config.Trade != nil && (config.Trade.Native == nil || config.Trade.Writer == nil)", ErrControl))
	}
	routine := []executor.RoundsScope{planAuthorizer{journal, config.RoundsMethods}}
	if moves != nil {
		worker, err = executor.NewWithMovement(journal, place, moves, clock, config.Executor, routine...)
	} else {
		worker, err = executor.New(journal, place, clock, config.Executor, routine...)
	}
	if err != nil {
		return cleanup(err)
	}
	// Each optional capability is wired directly onto worker with its own
	// typed boundary value, never discovered by type assertion on a composed
	// boundary.
	if config.Trade != nil {
		if err := worker.EnableTrade(&tradeBoundary{Boundary: place, trade: *config.Trade}); err != nil {
			return cleanup(err)
		}
	}

	if config.Haul != nil {
		haulBoundary, err := haul.NewHaulBoundary(config.Haul.Native, config.Haul.Writer, sessionBuildingLeases{control, journal, config.RoundsMethods, config.Executor.JournalTimeout}, clock)
		if err != nil {
			return cleanup(err)
		}
		if err := worker.EnableHaul(haulBoundary); err != nil {
			return cleanup(err)
		}
	}
	var coordinator *ClockCoordinator
	if config.Clock != nil {
		coordinator, err = NewClockCoordinator(journal, config.Clock.Native, config.Clock.Writer, sink, clock, ClockCoordinatorConfig{CallTimeout: config.Control.CallTimeout, JournalTimeout: config.Executor.JournalTimeout})
		if err != nil {
			return cleanup(err)
		}
	}
	if err = sink.attach(ctx, worker, coordinator); err != nil {
		if coordinator != nil {
			_ = coordinator.Stop(context.Background())
		}
		return cleanup(err)
	}
	return &Session{roundsMethods: config.RoundsMethods, control: control, executor: worker, journal: journal, clock: coordinator, clockWorkers: sink.clockWorkers}, nil
}

// Publish only after the final fallible construction check. Until publication,
// failure cleanup releases the owner without draining unrelated durable work.
func (s *sessionSink) attach(ctx context.Context, worker *executor.Executor, clock *ClockCoordinator) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := ctx.Err(); err != nil {
		return err
	}
	s.executor = worker
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

// RunBatch dispatches a plan's actions in one native Apply (#1042).
func (s *Session) RunBatch(ctx context.Context, plan domain.PlanID, actions []domain.ActionID) ([]executor.BatchItem, error) {
	if s.journal != nil {
		ctx = withPlanIntent(ctx, s.journal, plan)
	}
	return s.executor.RunBatch(ctx, plan, actions)
}
func (s *Session) Close(ctx context.Context) error { return s.control.Close(ctx) }

func (s *Session) RoundsMethodsEnabled() bool { return s.roundsMethods }
