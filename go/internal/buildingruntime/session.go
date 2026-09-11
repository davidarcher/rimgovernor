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
}

// Session binds the single profile owner to one journal and executor. Its caller
// owns bridge/database handles and may close them only after Close succeeds.
// Only an explicit trusted player path may call Acquire or create submitted plans.
type Session struct {
	control  *Control
	executor *executor.Executor
	journal  *store.Store
}

type sessionSink struct {
	mu       sync.Mutex
	executor *executor.Executor
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
	return s.executor.UpdateAuthority(value)
}
func (s *sessionSink) stop(ctx context.Context) error {
	s.mu.Lock()
	e := s.executor
	s.mu.Unlock()
	if e == nil {
		return nil
	}
	return e.Stop(ctx)
}

type sessionHolds struct{ journal *store.Store }

func (s sessionHolds) Holds(ctx context.Context, current domain.GenerationSnapshot) ([]policy.Reservation, error) {
	return executor.ExternalHolds(ctx, s.journal, current)
}

func NewSession(ctx context.Context, config SessionConfig, journal *store.Store, native Native, authority NativeAuthority, writer BuildingWriter, clock executor.Clock) (*Session, error) {
	if journal == nil || native == nil || authority == nil || writer == nil || clock == nil {
		return nil, errors.New("building session dependencies required")
	}
	sink := &sessionSink{}
	config.Control.StopWrites = sink.stop
	control, err := NewControl(ctx, config.Control, journal, authority, sink)
	if err != nil {
		return nil, err
	}
	cleanup := func(cause error) (*Session, error) {
		shutdown, cancel := context.WithTimeout(context.Background(), config.Control.CallTimeout)
		defer cancel()
		return nil, errors.Join(cause, control.Close(shutdown))
	}
	namespace, err := journal.Identity(ctx)
	if err != nil {
		return cleanup(err)
	}
	boundary, err := NewBoundary(native, writer, control, sessionHolds{journal}, clock, string(namespace), config.Rules)
	if err != nil {
		return cleanup(err)
	}
	worker, err := executor.New(journal, boundary, clock, config.Executor)
	if err != nil {
		return cleanup(err)
	}
	sink.mu.Lock()
	sink.executor = worker
	sink.mu.Unlock()
	if err = ctx.Err(); err != nil {
		return cleanup(err)
	}
	return &Session{control: control, executor: worker, journal: journal}, nil
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
func (s *Session) Manual(ctx context.Context) error  { return s.control.Manual(ctx) }
func (s *Session) Run(ctx context.Context, plan domain.PlanID, action domain.ActionID) (executor.Result, error) {
	return s.executor.Run(ctx, plan, action)
}
func (s *Session) Close(ctx context.Context) error { return s.control.Close(ctx) }
