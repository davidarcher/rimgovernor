package bridge

import (
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
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
}

type ClockRenew struct {
	Original *k.Epoch
	LeaseMS  uint32
}

type ClockSpeed struct {
	Original *k.Epoch
	Speed    k.Speed
}

// ClockExpectation retains original admission facts for receipt recovery.
// It cannot authorize a call: the current runtime must supply a fresh live lease.
type ClockExpectation struct {
	Identity         *c.Identity
	Attempt          *c.AttemptKey
	Owner            *a.Owner
	NativeGeneration uint64
	Command          ClockCommand
}
