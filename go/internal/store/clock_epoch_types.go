package store

import (
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type ClockEpochStage string

const (
	ClockEpochRequired   ClockEpochStage = "required"
	ClockEpochPausing    ClockEpochStage = "pausing"
	ClockEpochUncertain  ClockEpochStage = "uncertain"
	ClockEpochPaused     ClockEpochStage = "paused"
	ClockEpochRetired    ClockEpochStage = "retired"
	ClockEpochSuperseded ClockEpochStage = "superseded"
)

// ClockEpochObligation derives ownership from an immutable applied Start receipt.
// Sequence is a local pause-dispatch fence, not a native attempt or a live lease.
type ClockEpochObligation struct {
	StartRequestID string
	Epoch          *k.Epoch
	Stage          ClockEpochStage
	Sequence       uint64
	Context        *c.ObservationContext
	Status         *k.Status
}
