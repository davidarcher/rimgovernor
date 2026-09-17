package nativeaccept

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"time"
)

// StallEnv overrides the default stall budget (a Go duration) for the shared
// waits and for harnesses whose -stall flag defaults to StallBudget.
const StallEnv = "RIMGOVERNOR_ACCEPT_STALL"

// DefaultStall is the shared stall budget: long enough for a wall segment or
// a haul at speed 1, short enough that a letter pause, a lost service or a
// plan the executor never moves ends the run well before its ceiling.
const DefaultStall = 10 * time.Minute

// StallBudget returns StallEnv's duration, or DefaultStall when unset or
// unparsable.
func StallBudget() time.Duration {
	if v := os.Getenv(StallEnv); v != "" {
		if d, err := time.ParseDuration(v); err == nil && d > 0 {
			return d
		}
	}
	return DefaultStall
}

// Wait bounds a poll loop two ways (issue #91): a ceiling on the whole wait
// and, tighter, a stall budget on the time since the probe's signature last
// changed. A broken run stops changing long before it reaches the ceiling, so
// the stall budget is what ends it; the ceiling is the safety net for a run
// that keeps changing without ever finishing.
type Wait struct {
	// Ceiling is the whole-wait budget; zero means only the stall budget and
	// the context bound the wait.
	Ceiling time.Duration
	// Stall is the budget for the signature not changing; zero disables
	// stall detection.
	Stall time.Duration
	// Interval is the pause between probes (default 2s).
	Interval time.Duration
	// Terminal, when set, is checked before every probe; a non-nil error ends
	// the wait immediately (a serve subprocess that exited, an interrupted
	// scenario, a plan in a failed state).
	Terminal func() error
}

// Probe reports the wait's current progress signature and whether it is
// done. The signature is whatever the wait counts as progress: a plan
// lineage's stages, a review revision, a colony fact. Include the game tick
// only when the wait tolerates a plan that is not moving while the game
// runs; leave it out when the plan itself is what must move.
type Probe func(ctx context.Context) (signature string, done bool, err error)

// WaitOutcome says which bound ended a WaitProgress call.
type WaitOutcome int

const (
	WaitStalled WaitOutcome = iota + 1
	WaitCeiling
	WaitTerminal
)

func (o WaitOutcome) String() string {
	switch o {
	case WaitStalled:
		return "stalled"
	case WaitCeiling:
		return "ceiling"
	case WaitTerminal:
		return "terminal"
	}
	return fmt.Sprintf("WaitOutcome(%d)", int(o))
}

// WaitError is the failure WaitProgress returns for its own bounds; a probe
// or context error is returned as is.
type WaitError struct {
	Outcome WaitOutcome
	// Signature is the last signature observed; Quiet how long it had been
	// unchanged; Elapsed the whole wait; Rounds the probes made.
	Signature string
	Quiet     time.Duration
	Elapsed   time.Duration
	Rounds    int
	// Cause is Terminal's error for WaitTerminal.
	Cause error
}

func (e *WaitError) Error() string {
	switch e.Outcome {
	case WaitTerminal:
		return fmt.Sprintf("wait ended: %v (after %s, last signature %q)", e.Cause, e.Elapsed.Round(time.Second), e.Signature)
	case WaitCeiling:
		return fmt.Sprintf("wait ceiling %s reached (signature %q unchanged for %s, %d probes)", e.Elapsed.Round(time.Second), e.Signature, e.Quiet.Round(time.Second), e.Rounds)
	}
	return fmt.Sprintf("wait stalled: signature %q unchanged for %s (%s elapsed, %d probes)", e.Signature, e.Quiet.Round(time.Second), e.Elapsed.Round(time.Second), e.Rounds)
}

func (e *WaitError) Unwrap() error { return e.Cause }

// IsStalled reports whether err is a WaitError with the stalled outcome.
func IsStalled(err error) bool {
	var w *WaitError
	return errors.As(err, &w) && w.Outcome == WaitStalled
}

// Signature joins parts into a progress signature for a Probe.
func Signature(parts ...any) string {
	s := make([]string, len(parts))
	for i, p := range parts {
		s[i] = fmt.Sprint(p)
	}
	return strings.Join(s, "|")
}

// WaitProgress polls probe under w's bounds until it reports done.
func WaitProgress(ctx context.Context, w Wait, probe Probe) error {
	interval := w.Interval
	if interval <= 0 {
		interval = 2 * time.Second
	}
	start := time.Now()
	last, lastChange := "", start
	rounds := 0
	for {
		now := time.Now()
		if w.Terminal != nil {
			if cause := w.Terminal(); cause != nil {
				return &WaitError{Outcome: WaitTerminal, Signature: last, Quiet: now.Sub(lastChange), Elapsed: now.Sub(start), Rounds: rounds, Cause: cause}
			}
		}
		signature, done, err := probe(ctx)
		rounds++
		if err != nil {
			return err
		}
		if done {
			return nil
		}
		now = time.Now()
		if rounds == 1 || signature != last {
			last, lastChange = signature, now
		}
		quiet := now.Sub(lastChange)
		if w.Stall > 0 && quiet >= w.Stall {
			return &WaitError{Outcome: WaitStalled, Signature: last, Quiet: quiet, Elapsed: now.Sub(start), Rounds: rounds}
		}
		if w.Ceiling > 0 && now.Sub(start) >= w.Ceiling {
			return &WaitError{Outcome: WaitCeiling, Signature: last, Quiet: quiet, Elapsed: now.Sub(start), Rounds: rounds}
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(interval):
		}
	}
}
