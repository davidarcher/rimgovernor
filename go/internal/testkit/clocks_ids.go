package testkit

import (
	"fmt"
	"sync"
	"time"
)

// ManualClock is an explicitly injected replay clock. Advancing it never sleeps
// or advances simulation. Methods are safe for concurrent callers.
type ManualClock struct {
	mu  sync.Mutex
	now time.Time
}

func NewManualClock(start time.Time) *ManualClock {
	return &ManualClock{now: start}
}

func (clock *ManualClock) Now() time.Time {
	clock.mu.Lock()
	defer clock.mu.Unlock()
	return clock.now
}

// Advance permits negative durations to reproduce clock rewinds explicitly.
func (clock *ManualClock) Advance(delta time.Duration) {
	clock.mu.Lock()
	defer clock.mu.Unlock()
	clock.now = clock.now.Add(delta)
}

// ID is a fixture identifier, not a production domain identity.
type ID string

// SequenceIDs supplies only recorded identifiers. It never invents a fallback ID.
// Concurrent calls are serialized, but their scheduling order is not prescribed.
type SequenceIDs struct {
	mu   sync.Mutex
	ids  []ID
	next int
}

func NewSequenceIDs(ids []ID) *SequenceIDs {
	return &SequenceIDs{ids: append([]ID(nil), ids...)}
}

func (source *SequenceIDs) Next() (ID, error) {
	source.mu.Lock()
	defer source.mu.Unlock()
	if source.next == len(source.ids) {
		return "", fmt.Errorf("replay ID sequence exhausted")
	}
	id := source.ids[source.next]
	source.next++
	return id, nil
}
