package buildingruntime

import (
	"context"
	"errors"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type SessionConfig struct {
	Control  ControlConfig
	Executor executor.Limits
	Rules    []policy.ResourceRule
	Draft    *DraftCapabilities
	Clock    *ClockCapabilities
}

// Session binds the single profile owner to one journal and executor. Its caller
// owns bridge/database handles and may close them only after Close succeeds.
// Only an explicit trusted player path may call Acquire or create submitted plans.
type Session struct {
	control  *Control
	executor *executor.Executor
	journal  *store.Store
	drafts   *draftSweep
	clock    *ClockCoordinator
}

type sessionSink struct {
	mu       sync.Mutex
	executor *executor.Executor
	control  *Control
	drafts   *draftSweep
	clock    *ClockCoordinator
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
	s.mu.Lock()
	e := s.executor
	drafts := s.drafts
	clock := s.clock
	s.mu.Unlock()
	if e == nil {
		return nil
	}
	err := e.Stop(ctx)
	if clock != nil {
		err = errors.Join(err, clock.Stop(ctx))
	}
	if err != nil {
		return err
	}
	if clock != nil {
		err = clock.Cleanup(ctx)
	}
	if drafts != nil {
		err = errors.Join(err, drafts.run(ctx))
	}
	return err
}

type sessionHolds struct{ journal *store.Store }

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
	if config.Clock != nil && (config.Clock.Native == nil || config.Clock.Writer == nil) {
		return nil, errors.New("complete clock capabilities required")
	}
	if config.Clock == nil {
		if err := requireNoClockObligations(ctx, journal); err != nil {
			return nil, err
		}
	}
	sink := &sessionSink{}
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
	boundary, err := NewBoundary(native, writer, control, sessionHolds{journal}, clock, string(namespace), config.Rules)
	if err != nil {
		return cleanup(err)
	}
	var worker *executor.Executor
	if draft != nil {
		worker, err = executor.NewWithDraft(journal, boundary, draft, clock, config.Executor)
	} else {
		worker, err = executor.New(journal, boundary, clock, config.Executor)
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
		coordinator, err = NewClockCoordinator(journal, config.Clock.Native, config.Clock.Writer, sink, ClockCoordinatorConfig{CallTimeout: config.Control.CallTimeout, JournalTimeout: config.Executor.JournalTimeout})
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
	return &Session{control: control, executor: worker, journal: journal, drafts: drafts, clock: coordinator}, nil
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
