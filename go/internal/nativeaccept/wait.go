package nativeaccept

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"sync"
	"time"
)

// StallEnv overrides the default stall budget (a Go duration) for the shared
// waits and for harnesses whose -stall flag defaults to StallBudget.
const StallEnv = "RIMGOVERNOR_ACCEPT_STALL"

// DefaultStall is the shared stall budget: long enough for a wall segment or
// a haul under the serve clock at Fast, short enough that a letter pause, a
// lost service or a plan the executor never moves ends the run soon after
// it stops moving. It was 10 minutes until every failed run was found to
// spend those ten minutes watching an unchanged signature (a 14-minute
// defense run held its last signature for 10m1s); a wait that needs the
// game to do more than a few minutes of work is bounded in ticks
// (Wait.Ticks, RunUntil), not by a longer stall. Finalize records each
// run's longest quiet span under wait_stats so the budget can be tuned
// from passing runs.
const DefaultStall = 3 * time.Minute

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
	// Ticks, with Tick set, is the game-time budget (issue #91): the wait
	// fails once the game has advanced more than Ticks past the tick Tick
	// reported at the first probe. A wait for something the game itself must
	// do (a haul, a surgery, a pen) is bounded this way, so the bound means
	// the same at every speed and on every machine; Stall still catches a
	// game that stops ticking (a pausing letter) and Ceiling a run that
	// keeps ticking without finishing.
	Ticks uint64
	// Tick reads the current game tick; Harness.Tick for a harness that
	// holds the session, the routine review's tick for a serve-driven one.
	Tick func(ctx context.Context) (uint64, error)
}

// TicksPerDay is RimWorld's game day, for tick budgets stated in days.
const TicksPerDay = 60000

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
	WaitTicks
)

func (o WaitOutcome) String() string {
	switch o {
	case WaitStalled:
		return "stalled"
	case WaitCeiling:
		return "ceiling"
	case WaitTerminal:
		return "terminal"
	case WaitTicks:
		return "ticks"
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
	// TicksElapsed is how far the game advanced during the wait, when the
	// wait had a Tick reader.
	TicksElapsed uint64
}

func (e *WaitError) Error() string {
	switch e.Outcome {
	case WaitTerminal:
		return fmt.Sprintf("wait ended: %v (after %s, last signature %q)", e.Cause, e.Elapsed.Round(time.Second), e.Signature)
	case WaitTicks:
		return fmt.Sprintf("wait tick budget spent: %d ticks of game time (signature %q unchanged for %s, %s elapsed, %d probes)", e.TicksElapsed, e.Signature, e.Quiet.Round(time.Second), e.Elapsed.Round(time.Second), e.Rounds)
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

// waitStats is what Finalize reports under wait_stats: how many waits the
// run made, the longest a signature stayed unchanged before it moved or the
// wait ended (the evidence for tuning DefaultStall), and how many waits
// stalled.
var waitStats struct {
	mu       sync.Mutex
	waits    int
	stalled  int
	maxQuiet time.Duration
	maxLabel string
}

// recordWait folds one finished wait into waitStats.
func recordWait(quiet time.Duration, signature string, stalled bool) {
	waitStats.mu.Lock()
	defer waitStats.mu.Unlock()
	waitStats.waits++
	if stalled {
		waitStats.stalled++
	}
	if quiet > waitStats.maxQuiet {
		waitStats.maxQuiet, waitStats.maxLabel = quiet, signature
	}
}

// ResetWaitStats empties the process's wait statistics so a runner that
// executes several cases in one process reports each case's own.
func ResetWaitStats() {
	waitStats.mu.Lock()
	defer waitStats.mu.Unlock()
	waitStats.waits, waitStats.stalled, waitStats.maxQuiet, waitStats.maxLabel = 0, 0, 0, ""
}

// WaitStats is the run's wait statistics for the report.
func WaitStats() map[string]any {
	waitStats.mu.Lock()
	defer waitStats.mu.Unlock()
	return map[string]any{
		"waits": waitStats.waits, "stalled": waitStats.stalled,
		"max_quiet_ms": waitStats.maxQuiet.Milliseconds(), "max_quiet_signature": waitStats.maxLabel,
		"stall_budget_ms": StallBudget().Milliseconds(),
	}
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
	var startTick, ticksElapsed uint64
	// The longest the signature stayed unchanged, including the final span
	// when the wait ends; Finalize reports the run's maximum.
	longest := time.Duration(0)
	stalled := false
	defer func() {
		if quiet := time.Since(lastChange); quiet > longest {
			longest = quiet
		}
		recordWait(longest, last, stalled)
	}()
	for {
		now := time.Now()
		if w.Terminal != nil {
			if cause := w.Terminal(); cause != nil {
				return &WaitError{Outcome: WaitTerminal, Signature: last, Quiet: now.Sub(lastChange), Elapsed: now.Sub(start), Rounds: rounds, Cause: cause, TicksElapsed: ticksElapsed}
			}
		}
		if w.Tick != nil {
			tick, err := w.Tick(ctx)
			if err != nil {
				return err
			}
			if rounds == 0 {
				startTick = tick
			}
			if tick > startTick {
				ticksElapsed = tick - startTick
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
			if quiet := now.Sub(lastChange); quiet > longest {
				longest = quiet
			}
			last, lastChange = signature, now
		}
		quiet := now.Sub(lastChange)
		if w.Ticks > 0 && w.Tick != nil && ticksElapsed > w.Ticks {
			return &WaitError{Outcome: WaitTicks, Signature: last, Quiet: quiet, Elapsed: now.Sub(start), Rounds: rounds, TicksElapsed: ticksElapsed}
		}
		if w.Stall > 0 && quiet >= w.Stall {
			stalled = true
			return &WaitError{Outcome: WaitStalled, Signature: last, Quiet: quiet, Elapsed: now.Sub(start), Rounds: rounds, TicksElapsed: ticksElapsed}
		}
		if w.Ceiling > 0 && now.Sub(start) >= w.Ceiling {
			return &WaitError{Outcome: WaitCeiling, Signature: last, Quiet: quiet, Elapsed: now.Sub(start), Rounds: rounds, TicksElapsed: ticksElapsed}
		}
		// Between probes is a natural pause for the checkpoint ring
		// (#249); the time a capture takes is not the wait's quiet time.
		if took := checkpointPause(ctx); took > 0 {
			start, lastChange = start.Add(took), lastChange.Add(took)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(interval):
		}
	}
}
