package sustainedfood

import (
	"fmt"
	"time"
)

// tickStall reports a game whose live tick has stopped advancing (or was
// never read) for longer than limit: the game died, or the bridge's
// reattach relaunched it to the main menu with no map loaded.
type tickStall struct {
	limit    time.Duration
	last     uint64
	seen     bool
	advanced time.Time
}

func newTickStall(limit time.Duration, now time.Time) *tickStall {
	return &tickStall{limit: limit, advanced: now}
}

func (s *tickStall) observe(tick uint64, now time.Time) {
	if !s.seen || tick != s.last {
		s.last, s.seen, s.advanced = tick, true, now
	}
}

// reset restarts the clock after a deliberate pause (a checkpoint save).
func (s *tickStall) reset(now time.Time) { s.advanced = now }

func (s *tickStall) check(now time.Time) (error, bool) {
	if s.limit <= 0 || now.Sub(s.advanced) <= s.limit {
		return nil, false
	}
	if !s.seen {
		return fmt.Errorf("game tick unreadable for %s: the game is not running or has no map loaded", s.limit), true
	}
	return fmt.Errorf("game tick stuck at %d for %s: the game died or was relaunched with no map loaded", s.last, s.limit), true
}
