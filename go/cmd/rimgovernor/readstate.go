package main

import (
	"context"
	"errors"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// readState retains the last good observation while refreshes run serially.
// It owns no native mutation capability or player-direction authority.
type readState struct {
	mu       sync.Mutex
	refresh  chan struct{}
	source   observation.Source
	clock    observation.Clock
	maxAge   time.Duration
	started  time.Time
	snapshot httpapi.Snapshot
}

func newReadState(sessionID string, source observation.Source, clock observation.Clock, maxAge time.Duration) (*readState, error) {
	if sessionID == "" || source == nil || clock == nil || maxAge <= 0 {
		return nil, errors.New("session, observation source, clock and positive freshness limit required")
	}
	return &readState{source: source, clock: clock, maxAge: maxAge, refresh: make(chan struct{}, 1), snapshot: httpapi.Snapshot{SessionID: sessionID, Mode: "manual", Status: "Waiting for game observations", Stale: true}}, nil
}

func (s *readState) Refresh(ctx context.Context) error {
	select {
	case s.refresh <- struct{}{}:
		defer func() { <-s.refresh }()
	case <-ctx.Done():
		return ctx.Err()
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	reading, err := observation.Observe(ctx, s.source, s.clock)
	if err == nil {
		err = reading.Snapshot.CheckFresh(s.clock.Now(), s.maxAge, reading.Snapshot.After)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if err != nil {
		s.snapshot.Connected = false
		s.snapshot.Stale = true
		s.snapshot.Status = "Game observations are unavailable"
		return err
	}
	s.started = reading.Snapshot.StartedAt
	s.snapshot.Connected = true
	s.snapshot.Stale = false
	s.snapshot.Status = "Read-only observation"
	s.snapshot.Identity = domain.Known(reading.Snapshot.After)
	s.snapshot.Tick = domain.Known(reading.Snapshot.After.Tick)
	s.snapshot.Paused = reading.Snapshot.Status.Paused
	s.snapshot.ObservedAt = domain.Known(reading.Snapshot.ObservedAt)
	return nil
}

func (s *readState) Snapshot(ctx context.Context) (httpapi.Snapshot, error) {
	if err := ctx.Err(); err != nil {
		return httpapi.Snapshot{}, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	result := s.snapshot
	now := s.clock.Now()
	if s.started.IsZero() || now.Before(s.started) || now.Sub(s.started) > s.maxAge {
		result.Stale = true
	}
	if observed, known := result.ObservedAt.Value(); known && now.Before(observed) {
		result.Stale = true
	}
	return result, nil
}

// Poll runs after the initial refresh. The caller cancels and joins it before
// closing the bridge session, so no worker outlives its native connection.
func (s *readState) Poll(ctx context.Context, interval time.Duration) {
	timer := time.NewTicker(interval)
	defer timer.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-timer.C:
			_ = s.Refresh(ctx)
		}
	}
}

// factTickSource serves the read-state poll's tick from the scheduler's
// fact cache when the identity row it holds was stored within maxAge (the
// bundle seeds it every scheduler step), and reads natively otherwise: no
// clock control, a stalled scheduler, or a row a write or scope change
// dropped (#168). A served reply is what the dedicated read would have
// returned for that row, so DecodeTick validates it unchanged.
type factTickSource struct {
	observation.Source
	facts  *bridge.FactCache
	maxAge time.Duration
	now    func() time.Time
}

func (s factTickSource) Tick(ctx context.Context) (*l.TickReply, bridge.Result, error) {
	if cached, ok := s.facts.Context(); ok && s.now().Sub(cached.StoredAt) <= s.maxAge {
		if err := ctx.Err(); err != nil {
			return nil, bridge.Result{}, err
		}
		return &l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: cached.Context, Paused: proto.Bool(cached.Paused)}}}, bridge.Result{}, nil
	}
	return s.Source.Tick(ctx)
}
