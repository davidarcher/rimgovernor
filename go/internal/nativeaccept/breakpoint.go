package nativeaccept

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"
)

// A breakpoint (issue #280) stops a run at a named point of its run phase
// so the colony can be inspected instead of diagnosed from text: the case
// is cut, a bundle labelled BreakCheckpoint is taken into its ring, and
// the game is left loaded and paused on the kept process (visible with
// -headless=false). The ring's Next names the bundle, so `acceptance
// resume` (or the next plain run of the case) continues from it and
// `acceptance stop` discards it.

// BreakCheckpoint is the label of the bundle a run paused at a breakpoint
// leaves in its ring.
const BreakCheckpoint = "break"

// breakTickPoll is how often a tick breakpoint reads the game tick at a
// natural pause (readTick).
const breakTickPoll = 2 * time.Second

// Breakpoint is where a run stops: after the named stage (Session.Stage),
// at the first natural pause once the game tick reaches Tick, or once the
// run-phase offset (the ring's t+... labels) reaches Minute. Exactly one
// is set.
type Breakpoint struct {
	Stage  string
	Tick   uint64
	Minute time.Duration
}

// ParseBreakpoint reads a -break spec: stage=<name>, tick=<n> or
// minute=<m> (a whole number of minutes or a duration such as 1m30s).
func ParseBreakpoint(spec string) (Breakpoint, error) {
	kind, value, ok := strings.Cut(strings.TrimSpace(spec), "=")
	if !ok || value == "" {
		return Breakpoint{}, fmt.Errorf("-break %q: want stage=<name>, tick=<n> or minute=<m>", spec)
	}
	switch kind {
	case "stage":
		return Breakpoint{Stage: value}, nil
	case "tick":
		n, err := strconv.ParseUint(value, 10, 64)
		if err != nil || n == 0 {
			return Breakpoint{}, fmt.Errorf("-break %q: the tick must be a positive integer", spec)
		}
		return Breakpoint{Tick: n}, nil
	case "minute":
		if n, err := strconv.ParseFloat(value, 64); err == nil {
			if n <= 0 {
				return Breakpoint{}, fmt.Errorf("-break %q: the minute must be positive", spec)
			}
			return Breakpoint{Minute: time.Duration(n * float64(time.Minute))}, nil
		}
		d, err := time.ParseDuration(value)
		if err != nil || d <= 0 {
			return Breakpoint{}, fmt.Errorf("-break %q: the minute must be a positive number of minutes or a duration", spec)
		}
		return Breakpoint{Minute: d}, nil
	}
	return Breakpoint{}, fmt.Errorf("-break %q: unknown kind %q (stage, tick or minute)", spec, kind)
}

// IsZero is true when no breakpoint is set.
func (b Breakpoint) IsZero() bool { return b.Stage == "" && b.Tick == 0 && b.Minute == 0 }

// String is the spec ParseBreakpoint reads.
func (b Breakpoint) String() string {
	switch {
	case b.Stage != "":
		return "stage=" + b.Stage
	case b.Tick > 0:
		return "tick=" + strconv.FormatUint(b.Tick, 10)
	case b.Minute > 0:
		if b.Minute%time.Minute == 0 {
			return "minute=" + strconv.Itoa(int(b.Minute/time.Minute))
		}
		return "minute=" + b.Minute.String()
	}
	return ""
}

// BreakRecord is what a ring remembers about the run that paused at a
// breakpoint (Ring.Break): the spec, why it tripped, the bundle's tick and
// the run's output directory.
type BreakRecord struct {
	Spec   string `json:"spec"`
	Reason string `json:"reason"`
	Tick   uint64 `json:"tick"`
	Output string `json:"output,omitempty"`
	At     string `json:"at"`
}

// BreakError is the cause a tripped breakpoint cancels the run context
// with; Broke reads it back.
type BreakError struct {
	Reason string
}

func (e *BreakError) Error() string { return "breakpoint: " + e.Reason }

// Broke is the breakpoint that cut ctx, nil when the context ended for
// any other reason (or not at all).
func Broke(ctx context.Context) *BreakError {
	var be *BreakError
	if errors.As(context.Cause(ctx), &be) {
		return be
	}
	return nil
}

// Trip fires the ring's breakpoint once with reason: OnBreak (the runner's
// cancel) runs on the first call only.
func (r *CheckpointRing) Trip(reason string) {
	r.mu.Lock()
	if r.tripped != "" {
		r.mu.Unlock()
		return
	}
	r.tripped = reason
	on := r.OnBreak
	r.mu.Unlock()
	if on != nil {
		on(reason)
	}
}

// Tripped is why the breakpoint fired, "" while it has not.
func (r *CheckpointRing) Tripped() string {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.tripped
}

// checkBreak is the natural-pause hook's half of a tick or minute
// breakpoint (a stage breakpoint trips from Session.Stage): it trips the
// ring when the run-phase offset or the game tick has reached the mark.
// The tick is read at most every breakTickPoll.
func (r *CheckpointRing) checkBreak(ctx context.Context) {
	r.mu.Lock()
	b := r.Break
	if b.IsZero() || r.tripped != "" {
		r.mu.Unlock()
		return
	}
	if b.Minute > 0 {
		offset := r.offset()
		r.mu.Unlock()
		if offset >= b.Minute {
			r.Trip(fmt.Sprintf("run phase reached %s", OffsetLabel(offset)))
		}
		return
	}
	if b.Tick == 0 {
		r.mu.Unlock()
		return
	}
	now := time.Now()
	if now.Before(r.nextBreakTick) || r.breakReading {
		r.mu.Unlock()
		return
	}
	// The read below is itself a native call, and so a natural pause:
	// breakReading keeps it from re-entering here.
	r.nextBreakTick, r.breakReading = now.Add(breakTickPoll), true
	r.mu.Unlock()
	tick, ok := r.readTick(ctx)
	r.mu.Lock()
	r.breakReading = false
	r.mu.Unlock()
	if ok && tick >= b.Tick {
		r.Trip(fmt.Sprintf("game tick %d reached %d", tick, b.Tick))
	}
}

// readTick is the live game tick for a tick breakpoint: the service's
// /api/state while a service holds the game, an identity read on the
// harness while it does (a scenario clock's replies carry no tick), else
// the newest tick a reply carried.
func (r *CheckpointRing) readTick(ctx context.Context) (uint64, bool) {
	if p := r.service(); p != nil {
		return serviceTick(p.API)
	}
	if r.Bridge != nil {
		if h := r.Bridge(); h != nil {
			loaded, err := readLoaded(ctx, h, "break-tick")
			if err != nil {
				return 0, false
			}
			loadedContext, _ := AsMap(loaded["context"])
			if tick := AsNumber(loadedContext["tick"]); tick > 0 {
				return uint64(tick), true
			}
			return 0, false
		}
	}
	return lastObservedTick()
}

// service is the running service holding the game, nil when none.
func (r *CheckpointRing) service() *ServiceProcess {
	if r.Service == nil {
		return nil
	}
	return r.Service()
}

// BreakBundle takes the bundle of a run paused at its breakpoint
// (BreakCheckpoint), pausing the game; unlike a periodic capture it is
// taken whether or not the ring is capped. It returns nil, with the error
// on Errors, when no bundle could be taken.
func (r *CheckpointRing) BreakBundle(ctx context.Context) *Checkpoint {
	r.mu.Lock()
	r.busy = true
	r.mu.Unlock()
	entry, err := r.capture(ctx, BreakCheckpoint, true)
	r.mu.Lock()
	defer r.mu.Unlock()
	r.busy = false
	if err != nil {
		r.errs = append(r.errs, fmt.Sprintf("%s: %v", BreakCheckpoint, err))
		return nil
	}
	return &entry
}

// lastObservedTick is the newest game tick a decoded reply carried since
// the last load, for a bridge-only case's tick breakpoint.
func lastObservedTick() (uint64, bool) {
	tickStats.mu.Lock()
	defer tickStats.mu.Unlock()
	return tickStats.last, tickStats.seen
}
