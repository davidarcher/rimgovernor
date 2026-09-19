package buildingruntime

import (
	"time"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// ClockWindowSize is the colony window one step admitted, published as the
// clock_step row's window_ticks. A routine window runs the whole budget
// (ClockSchedulerConfig.Start.MaxTicks, one game day by default) unless a
// native work allowance or the combat bound narrows it (#244): the planners
// review and the Worker dispatches under the running window, so nothing
// routine waits for a stop, and the stop tier (danger, coupled orders,
// player input) ends a window early.
type ClockWindowSize struct {
	Ticks uint32
}

// clockStopSpanLimit discards stop spans that cannot be one review's wait:
// a clock the service left stopped for a long time, or skew between the
// native wall clock and the scheduler's.
const clockStopSpanLimit = 10 * time.Minute

// clockStopSpan is the wall time since status's native stop, zero when
// status carries no usable stop; the admitting step publishes it as the
// stop-to-readmit pause (stop_pause_s, #162).
func clockStopSpan(status *k.Status, now time.Time) time.Duration {
	stopped := status.GetStopped()
	if stopped == nil || stopped.StoppedAtUnixMs == nil {
		return 0
	}
	span := now.Sub(time.UnixMilli(stopped.GetStoppedAtUnixMs()))
	if span <= 0 || span > clockStopSpanLimit {
		return 0
	}
	return span
}
