package bridge

import (
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// ClockCommand is exactly one immutable clock intent. Requested lease durations
// are command arguments, never restored permission; live lease tokens are absent.
type ClockCommand struct {
	Start *ClockStart
	Renew *ClockRenew
	Speed *ClockSpeed
}

type ClockStart struct {
	Speed             k.Speed
	Policy            *k.WatchPolicy
	LeaseMS, MaxTicks uint32
	// TestAcceleration asks for the native tick boost; it requires
	// SPEED_ULTRAFAST and a game launched with test acceleration (headless
	// acceptance profiles only), which native refuses otherwise.
	TestAcceleration bool
	// BlindTickBudget arms the native blind-tick regulator (issue #583):
	// past this many ticks since the controller's last read or oldest
	// unacknowledged journal row, native throttles the epoch toward Normal
	// and ramps back once the controller catches up, journaling both as
	// SpeedChanged. Zero leaves the epoch unregulated. MaxTicksPerSecond is
	// a continuous ceiling under the speed's own rate; zero is none.
	BlindTickBudget   uint32
	MaxTicksPerSecond uint32
}

type ClockRenew struct {
	Original *k.Epoch
	LeaseMS  uint32
}

type ClockSpeed struct {
	Original *k.Epoch
	Speed    k.Speed
	// MaxTicksPerSecond, when set, is the ceiling the change carries; nil
	// keeps the epoch's.
	MaxTicksPerSecond *uint32
}

// ClockExpectation retains original admission facts for receipt recovery.
// It cannot authorize a call: the current runtime must supply a fresh live lease.
type ClockExpectation struct {
	Identity         *c.Identity
	Attempt          *c.AttemptKey
	NativeGeneration uint64
	Command          ClockCommand
}
