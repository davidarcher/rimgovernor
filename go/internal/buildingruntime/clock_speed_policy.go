package buildingruntime

import (
	"time"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// SpeedPolicyConfig bounds the controller-side speed policy (#635): per
// admitted window the scheduler raises the requested speed one step after
// RaiseAfter windows whose readmit and observe latencies both held under
// their bounds, and drops a step at once when either is exceeded. The
// levels are Normal..Ultrafast, then Ultrafast with test acceleration
// when AllowTestAcceleration. The configured Start is the floor, so the
// policy starts from the row native's regulator (#583) already agrees with.
type SpeedPolicyConfig struct {
	// ReadmitBound bounds the wall time from a native stop to the next
	// window's admission; a late-handled reactive stop exceeds it.
	ReadmitBound time.Duration
	// ObserveBound bounds the step's own read-and-decide time.
	ObserveBound          time.Duration
	RaiseAfter            int
	Max                   k.Speed
	AllowTestAcceleration bool
}

// DefaultSpeedPolicy derives the bounds from the Worker's StepInterval.
func DefaultSpeedPolicy(step time.Duration) SpeedPolicyConfig {
	return SpeedPolicyConfig{ReadmitBound: 4 * step, ObserveBound: 2 * step, RaiseAfter: 3, Max: k.Speed_SPEED_ULTRAFAST}
}

// speedLatency is one admission's measurements; zero is unmeasured.
type speedLatency struct {
	Readmit, Observe time.Duration
}

// speedPolicy is the policy's state; zero level means "not yet seeded".
type speedPolicy struct {
	config SpeedPolicyConfig
	level  int
	calm   int
}

func speedLevel(speed k.Speed, accelerated bool) int {
	if accelerated && speed == k.Speed_SPEED_ULTRAFAST {
		return int(k.Speed_SPEED_ULTRAFAST) + 1
	}
	return int(speed)
}

func speedOfLevel(level int) (k.Speed, bool) {
	if level > int(k.Speed_SPEED_ULTRAFAST) {
		return k.Speed_SPEED_ULTRAFAST, true
	}
	return k.Speed(level), false
}

// next is the level for a window admitted after sample, and the policy
// state to keep once that window is actually commanded. floor is the
// configured start's level.
func (p speedPolicy) next(floor int, sample speedLatency) (int, speedPolicy) {
	top := speedLevel(p.config.Max, p.config.AllowTestAcceleration)
	if top < floor {
		top = floor
	}
	if p.level < floor || p.level > top {
		p.level = floor
		p.calm = 0
	}
	over := (p.config.ReadmitBound > 0 && sample.Readmit > p.config.ReadmitBound) || (p.config.ObserveBound > 0 && sample.Observe > p.config.ObserveBound)
	switch {
	case over:
		p.calm = 0
		if p.level > floor {
			p.level--
		}
	default:
		p.calm++
		if p.calm >= max(p.config.RaiseAfter, 1) && p.level < top {
			p.level++
			p.calm = 0
		}
	}
	return p.level, p
}
