package bridge

import (
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

type ClockEpochState string

const (
	ClockEpochRequired   ClockEpochState = "required"
	ClockEpochUncertain  ClockEpochState = "uncertain"
	ClockEpochPaused     ClockEpochState = "paused"
	ClockEpochRetired    ClockEpochState = "retired"
	ClockEpochSuperseded ClockEpochState = "superseded"
)

// AssessClockEpoch classifies fresh cleanup evidence, never live permission.
// Original ownership is immutable even when speed, lease and observed tick change.
func AssessClockEpoch(original *k.Epoch, current *c.ObservationContext, status *k.Status) (ClockEpochState, error) {
	if err := ValidateClockEpoch(original); err != nil {
		return "", err
	}
	if err := clockWire(current); err != nil {
		return "", err
	}
	if err := ValidateContext(current); err != nil {
		return "", err
	}
	if status != nil {
		if err := ValidateClockStatus(status, current.Identity); err != nil {
			return "", err
		}
		if !proto.Equal(status.Context, current) {
			return "", contract("clock cleanup context mismatch")
		}
	}
	if !sameIdentity(original.Origin.Identity, current.Identity) {
		return ClockEpochSuperseded, nil
	}
	if current.GetTick() < original.GetLastTick() {
		return "", contract("clock cleanup observation regressed")
	}
	if status == nil {
		return "", contract("same-world clock status required")
	}
	if status.GetUnavailable() != nil || status.GetNeverStarted() != nil {
		return ClockEpochUncertain, nil
	}
	actual := clockStatusEpoch(status)
	if actual == nil {
		return "", contract("clock cleanup epoch missing")
	}
	if !proto.Equal(actual.Owner, original.Owner) {
		return ClockEpochSuperseded, nil
	}
	if !proto.Equal(actual.Origin, original.Origin) || !proto.Equal(actual.Policy, original.Policy) || actual.GetStartTick() != original.GetStartTick() || actual.GetTickDeadline() != original.GetTickDeadline() || actual.GetLastTick() < original.GetLastTick() {
		return "", contract("clock cleanup original epoch mismatch")
	}
	if stopped := status.GetStopped(); stopped != nil {
		if status.GetActualPaused() && stopped.GetActualPaused() && stopped.GetPauseVerified() {
			return ClockEpochPaused, nil
		}
		return ClockEpochRetired, nil
	}
	if status.GetRunning() != nil {
		return ClockEpochRequired, nil
	}
	return ClockEpochUncertain, nil
}
