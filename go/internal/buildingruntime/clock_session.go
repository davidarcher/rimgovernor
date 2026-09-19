package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// ClockCapabilities gates the complete owned clock boundary. Constructing a
// session never resumes time or recovers a live lease from durable evidence.
type ClockCapabilities struct {
	Native ClockNative
	Writer ClockWriter
}

type clockWorldSource struct{ native ClockNative }

func (s clockWorldSource) ReadWorld(ctx context.Context) (store.World, error) {
	reply, _, err := s.native.Identity(ctx)
	if err != nil {
		return store.World{}, err
	}
	observed := reply.GetLoaded().GetContext()
	if err = bridge.ValidateContext(observed); err != nil {
		return store.World{}, err
	}
	w := store.World{Colony: domain.ColonyID(observed.Identity.GetColonyId()), Load: domain.LoadID(observed.Identity.GetLoadToken()), Map: domain.MapID(observed.Identity.GetMapId())}
	return w, errors.Join(w.Validate(), ctx.Err())
}

func requireNoClockObligations(ctx context.Context, journal *store.Store) error {
	attempts, err := journal.LoadClockAttempts(ctx, 4096)
	if err != nil {
		return err
	}
	for _, v := range attempts {
		if v.SupersededAt == nil && v.Intent.Command.Start != nil && (v.Phase == store.ClockDispatched || v.Phase == store.ClockUncertain) {
			return errors.New("clock recovery capability required for pending start")
		}
	}
	epochs, err := journal.LoadClockEpochs(ctx, 4096)
	if err != nil {
		return err
	}
	for _, v := range epochs {
		if !clockCoordinatorTerminal(v.Stage) {
			return errors.New("clock cleanup capability required for owned epoch")
		}
	}
	return nil
}

// cleanup joins the owed clock commands and releases every owned draft: an
// explicit Manual, a checkpoint save and shutdown hand the pawns back.
func (s *sessionSink) cleanup(ctx context.Context) error { return s.drain(ctx, false) }

// resume is cleanup before a resume in the same world: the drafts a plan
// still holds stay owned (draftSweep.run).
func (s *sessionSink) resume(ctx context.Context) error { return s.drain(ctx, true) }

func (s *sessionSink) drain(ctx context.Context, retainHeld bool) error {
	s.mu.Lock()
	drafts, clock := s.drafts, s.clock
	s.mu.Unlock()
	var err error
	if clock != nil {
		err = clock.Cleanup(ctx)
	}
	if drafts != nil {
		err = errors.Join(err, drafts.run(ctx, retainHeld))
	}
	return err
}

// CommandClock belongs to explicit trusted player composition. Current shared
// authority and the original complete snapshot remain mandatory at dispatch.
func (s *Session) CommandClock(ctx context.Context, intent store.ClockIntent) (store.ClockAttempt, error) {
	if s.clock == nil {
		return store.ClockAttempt{}, ErrControl
	}
	return s.clock.Command(ctx, intent)
}

func (s *Session) CommandClockWindow(ctx context.Context, request ClockWindowRequest) (store.ClockAttempt, error) {
	if s.clock == nil {
		return store.ClockAttempt{}, ErrControl
	}
	return s.clock.CommandWindow(ctx, request)
}

func (s *Session) ReconcileClock(ctx context.Context, requestID string) (store.ClockAttempt, error) {
	if s.clock == nil {
		return store.ClockAttempt{}, ErrControl
	}
	return s.clock.Reconcile(ctx, requestID)
}

func (s *Session) CleanupClock(ctx context.Context) error {
	return s.CleanupClockObserved(ctx, nil)
}

// CleanupClockObserved is CleanupClock given a clock status the caller just
// read (ClockCoordinator.CleanupObserved).
func (s *Session) CleanupClockObserved(ctx context.Context, observed *k.Status) error {
	if s.clock == nil {
		return nil
	}
	return s.clock.CleanupObserved(ctx, observed)
}
