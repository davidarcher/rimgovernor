package buildingruntime

import (
	"math"
	"time"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// ClockWindowSizing sizes each admitted colony window by wall time instead
// of a fixed tick count (issue #126). A fixed budget shrinks in wall time
// as the clock speeds up -- at Ultrafast with the test boost 2500 ticks is
// well under a second of game between reviews that take seconds -- so the
// clock spends most of its wall time paused. The scheduler instead asks
// for the ticks the configured speed runs in a target wall time: the
// larger of Seconds and the pause it has been observing between windows
// (the span from one window's native stop to the next admission, which
// includes the poll delay and the review itself), so a window outlasts the
// review that admits it. Watched outcomes, danger and player input still
// stop a window early, so a wider window costs review latency only while
// nothing happens.
type ClockWindowSizing struct {
	// Seconds is the least wall time a colony window should run; zero
	// disables the sizing and keeps ClockSchedulerConfig.Start.MaxTicks.
	Seconds float64
	// TicksPerSecond is the configured speed's nominal tick rate; zero
	// disables the sizing.
	TicksPerSecond float64
	// MaxTicks caps the sized window; Start.MaxTicks is its floor. Zero
	// disables the sizing.
	MaxTicks uint32
}

func (w ClockWindowSizing) enabled(floor uint32) bool {
	return w.Seconds > 0 && w.TicksPerSecond > 0 && w.MaxTicks > floor
}

// ClockWindowSize is the colony window one step admitted, published as the
// clock_step row's window_ticks, window_target_s and window_tps.
type ClockWindowSize struct {
	Ticks          uint32
	TargetSeconds  float64
	TicksPerSecond float64
}

// clockWindowPauseAlpha weights the newest observed pause between windows
// against the running estimate.
const clockWindowPauseAlpha = 0.5

// clockWindowQuantum rounds a sized window up so a slightly different pause
// estimate does not re-key an otherwise identical admission.
const clockWindowQuantum = 100

// clockWindowPauseLimit discards pause spans that cannot be one review's
// wait: a clock the service left stopped for a long time, or skew between
// the native wall clock and the scheduler's.
const clockWindowPauseLimit = 10 * time.Minute

// clockWindowPause is the scheduler's running estimate of the paused wall
// time between windows. A stop is folded in once, by its native stop time;
// a later step that still sees the same stop (an earlier admission was
// refused) replaces that contribution with the longer span it now sees.
type clockWindowPause struct {
	known, beforeKnown bool
	seconds, before    float64
	stoppedAt          int64
}

// observe folds the span since status's native stop into the estimate and
// returns that span, or zero when status carries no usable stop.
func (p *clockWindowPause) observe(status *k.Status, now time.Time) time.Duration {
	stopped := status.GetStopped()
	if stopped == nil || stopped.StoppedAtUnixMs == nil {
		return 0
	}
	at := stopped.GetStoppedAtUnixMs()
	span := now.Sub(time.UnixMilli(at))
	if span <= 0 || span > clockWindowPauseLimit {
		return 0
	}
	if at == p.stoppedAt && p.known {
		p.seconds, p.known = p.before, p.beforeKnown
	} else {
		p.before, p.beforeKnown, p.stoppedAt = p.seconds, p.known, at
	}
	sample := span.Seconds()
	if !p.known {
		p.known, p.seconds = true, sample
		return span
	}
	p.seconds = clockWindowPauseAlpha*sample + (1-clockWindowPauseAlpha)*p.seconds
	return span
}

// colonyWindow sizes the next colony window from the configured sizing,
// the floor (the fixed budget) and the observed pause.
func (w ClockWindowSizing) colonyWindow(floor uint32, pause clockWindowPause) ClockWindowSize {
	size := ClockWindowSize{Ticks: floor}
	if !w.enabled(floor) {
		return size
	}
	size.TargetSeconds, size.TicksPerSecond = w.Seconds, w.TicksPerSecond
	if pause.known && pause.seconds > size.TargetSeconds {
		size.TargetSeconds = pause.seconds
	}
	ticks := math.Ceil(size.TargetSeconds*size.TicksPerSecond/clockWindowQuantum) * clockWindowQuantum
	switch {
	case ticks >= float64(w.MaxTicks):
		size.Ticks = w.MaxTicks
	case ticks > float64(floor):
		size.Ticks = uint32(ticks)
	}
	return size
}
