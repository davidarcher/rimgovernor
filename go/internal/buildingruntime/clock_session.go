package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
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

func (s *sessionSink) cleanup(ctx context.Context) error {
	s.mu.Lock()
	drafts, clock := s.drafts, s.clock
	s.mu.Unlock()
	var err error
	if clock != nil {
		err = clock.Cleanup(ctx)
	}
	if drafts != nil {
		err = errors.Join(err, drafts.run(ctx))
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

func (s *Session) ReconcileClock(ctx context.Context, requestID string) (store.ClockAttempt, error) {
	if s.clock == nil {
		return store.ClockAttempt{}, ErrControl
	}
	return s.clock.Reconcile(ctx, requestID)
}

func (s *Session) CleanupClock(ctx context.Context) error {
	if s.clock == nil {
		return nil
	}
	return s.clock.Cleanup(ctx)
}
