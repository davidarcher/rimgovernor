// Package controller owns the Go service lifecycle and current observations.
package controller

import (
	"context"
	"errors"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
)

// ReadState retains the last good observation while refreshes run serially.
// It owns no native mutation capability or player-direction authority.
type ReadState struct {
	mu       sync.Mutex
	refresh  chan struct{}
	source   observation.Source
	clock    observation.Clock
	maxAge   time.Duration
	started  time.Time
	snapshot httpapi.Snapshot
}

func NewReadState(sessionID string, source observation.Source, clock observation.Clock, maxAge time.Duration) (*ReadState, error) {
	if sessionID == "" || source == nil || clock == nil || maxAge <= 0 {
		return nil, errors.New("session, observation source, clock and positive freshness limit required")
	}
	return &ReadState{source: source, clock: clock, maxAge: maxAge, refresh: make(chan struct{}, 1), snapshot: httpapi.Snapshot{SessionID: sessionID, Mode: "manual", Status: "Waiting for game observations", Stale: true}}, nil
}

func (s *ReadState) Refresh(ctx context.Context) error {
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

func (s *ReadState) Snapshot(ctx context.Context) (httpapi.Snapshot, error) {
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
func (s *ReadState) Poll(ctx context.Context, interval time.Duration) {
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
