package store

import "errors"

var ErrRetired = errors.New("clock request retired")

// ClockSequenceState binds allocation and retirement to this journal namespace.
type ClockSequenceState struct {
	Namespace                     ControllerSessionID
	LastAllocated, RetiredThrough uint64
}

type ClockRetirement struct {
	State                                            ClockSequenceState
	RemovedAttempts, RemovedEpochs, RetainedAttempts int
}
