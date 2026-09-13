package store

import "github.com/davidarcher/RimGovernor/go/internal/store/clock"

type ClockEpochStage = clock.EpochStage

const (
	ClockEpochRequired   = clock.EpochRequired
	ClockEpochPausing    = clock.EpochPausing
	ClockEpochUncertain  = clock.EpochUncertain
	ClockEpochPaused     = clock.EpochPaused
	ClockEpochRetired    = clock.EpochRetired
	ClockEpochSuperseded = clock.EpochSuperseded
)

type ClockEpochObligation = clock.EpochObligation
