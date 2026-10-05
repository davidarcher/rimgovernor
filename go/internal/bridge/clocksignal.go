package bridge

import (
	"context"
	"encoding/json"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/gabp"
)

// clockChannel is the GABP event channel the mod announces its clock journal
// on (C# ClockEventPublisher). An event says the journal advanced to a
// cursor; it never carries journal rows. The journal stays the source of
// truth: the controller reads the page after its own cursor with an unheld
// clock_read_events, so ordering, loss and gap evidence are exactly what the
// read already proves. The mod announces its newest cursor on every subscribe,
// so a (re)connect always ends in one tail read.
const clockChannel = "rimgovernor.clock"

// ClockSignal is the controller's view of the clock channel: a version that
// moves whenever the mod announced a journal advance (or a subscription began)
// and a way to wait for the next move. It outlives reconnects.
type ClockSignal struct {
	mu      sync.Mutex
	version uint64
	newest  int64
	changed chan struct{} // closed and replaced on every advance
}

// NewClockSignal is a signal no announcement has moved. Tests drive it with
// Announce; a game backend feeds it from the channel.
func NewClockSignal() *ClockSignal { return &ClockSignal{newest: -1, changed: make(chan struct{})} }

// Version is the count of announcements seen. A reader takes it before its
// journal read and waits for it to move afterwards, so an announcement that
// lands during the read is never lost.
func (s *ClockSignal) Version() uint64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.version
}

// Newest is the newest journal cursor the mod announced, -1 for none yet.
func (s *ClockSignal) Newest() int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.newest
}

// Wait returns once the version differs from since, timeout passes or ctx
// ends; it reports whether the version moved.
func (s *ClockSignal) Wait(ctx context.Context, since uint64, timeout time.Duration) bool {
	s.mu.Lock()
	if s.version != since {
		s.mu.Unlock()
		return true
	}
	changed := s.changed
	s.mu.Unlock()
	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case <-changed:
		return true
	case <-timer.C:
		return false
	case <-ctx.Done():
		return false
	}
}

// offer records one rimgovernor.clock event. It never blocks: the GABP
// reader goroutine calls it.
func (s *ClockSignal) offer(event gabp.Event) {
	if s == nil || event.Channel != clockChannel {
		return
	}
	var body struct {
		Newest *int64 `json:"newest"`
	}
	_ = json.Unmarshal(event.Payload, &body)
	if body.Newest != nil {
		s.Announce(*body.Newest)
		return
	}
	s.touch()
}

// Announce records that the journal advanced to newest.
func (s *ClockSignal) Announce(newest int64) {
	s.mu.Lock()
	s.newest = newest
	s.version++
	close(s.changed)
	s.changed = make(chan struct{})
	s.mu.Unlock()
}

// touch moves the version without an announcement: a fresh subscription calls
// it so the reader takes one tail read of the journal, whatever it missed
// while no subscription stood.
func (s *ClockSignal) touch() {
	if s == nil {
		return
	}
	s.mu.Lock()
	s.version++
	close(s.changed)
	s.changed = make(chan struct{})
	s.mu.Unlock()
}

// ClockSignal is the clock channel's signal, nil for a client with no game
// host behind it.
func (c *Client) ClockSignal() *ClockSignal { return c.clock }
