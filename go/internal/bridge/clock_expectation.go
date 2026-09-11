package bridge

import (
	"errors"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// ValidateClockExpectation checks immutable admission evidence, not live permission.
func ValidateClockExpectation(e ClockExpectation) error {
	if err := errors.Join(clockWire(e.Identity), clockWire(e.Attempt), clockWire(e.Owner), ValidateIdentity(e.Identity), buildingAttempt(e.Attempt), authorityOwner(e.Owner)); err != nil {
		return err
	}
	if e.NativeGeneration == 0 || e.Attempt.GetControllerSessionId() != e.Owner.GetControllerSessionId() {
		return contract("clock expectation admission mismatch")
	}
	arms := 0
	if e.Command.Start != nil {
		arms++
	}
	if e.Command.Renew != nil {
		arms++
	}
	if e.Command.Speed != nil {
		arms++
	}
	if arms != 1 {
		return contract("one clock command required")
	}
	if start := e.Command.Start; start != nil {
		if start.Speed < 1 || start.Speed > 3 || start.MaxTicks < 1 || start.MaxTicks > 1800000 {
			return contract("clock start speed or tick budget")
		}
		return errors.Join(authorityDuration(&start.LeaseMS), clockPolicy(start.Policy, int64(start.MaxTicks)))
	}
	var original *k.Epoch
	if renew := e.Command.Renew; renew != nil {
		original = renew.Original
		if err := authorityDuration(&renew.LeaseMS); err != nil {
			return err
		}
	}
	if speed := e.Command.Speed; speed != nil {
		original = speed.Original
		if speed.Speed < 1 || speed.Speed > 3 {
			return contract("clock ordinary speed required")
		}
	}
	if err := clockEpoch(original); err != nil {
		return err
	}
	if !sameIdentity(original.Origin.Identity, e.Identity) || original.Owner.GetControllerSessionId() != e.Owner.GetControllerSessionId() {
		return contract("clock original epoch mismatch")
	}
	return nil
}

// ValidateClockReceipt correlates recovered or direct evidence to its full
// original command. A valid uncertain receipt remains uncertain; an applied
// receipt may already describe a stopped epoch and never grants live permission.
func ValidateClockReceipt(r *k.ControlReceipt, e ClockExpectation) error {
	if err := ValidateClockExpectation(e); err != nil {
		return err
	}
	if err := clockReceipt(r, e.Identity, e.Attempt, e.Owner, e.NativeGeneration); err != nil {
		return err
	}
	if r.GetUncertain() != nil {
		return nil
	}
	actual := clockStatusEpoch(r.GetApplied().GetStatus())
	if start := e.Command.Start; start != nil {
		if actual == nil || !proto.Equal(actual.Origin, r.AdmittedContext) || actual.GetTickDeadline()-actual.GetStartTick() != int64(start.MaxTicks) || actual.GetRequestedSpeed() != start.Speed || !proto.Equal(actual.Policy, start.Policy) || actual.GetLeaseRemainingMs() > start.LeaseMS {
			return contract("clock start epoch mismatch")
		}
		return nil
	}
	if renew := e.Command.Renew; renew != nil {
		if err := clockSameEpoch(actual, renew.Original, renew.Original.GetRequestedSpeed()); err != nil {
			return err
		}
		if actual.GetLeaseRemainingMs() > renew.LeaseMS {
			return contract("clock renewal lease exceeds request")
		}
		return nil
	}
	return clockSameEpoch(actual, e.Command.Speed.Original, e.Command.Speed.Speed)
}

// ValidateClockStatus validates typed evidence, including explicit unavailable
// and stopped states. Callers separately interpret freshness and pause facts.
func ValidateClockStatus(status *k.Status, identity *c.Identity) error {
	if err := authorityIdentity(identity); err != nil {
		return err
	}
	return clockStatus(status, identity)
}
func ValidateClockEpoch(epoch *k.Epoch) error { return clockEpoch(epoch) }

// ValidateClockControlReply accepts valid outcome evidence without interpreting
// it as permission, completion, or proof that a conflicting attempt had no effect.
func ValidateClockControlReply(reply *k.ControlReply, e ClockExpectation) error {
	if err := ValidateClockExpectation(e); err != nil {
		return err
	}
	if err := clockWire(reply); err != nil {
		return err
	}
	switch v := reply.Outcome.(type) {
	case *k.ControlReply_Receipt:
		if v == nil {
			return contract("missing clock receipt arm")
		}
		return ValidateClockReceipt(v.Receipt, e)
	case *k.ControlReply_Failure:
		if v == nil {
			return contract("missing clock failure arm")
		}
		err := failure(v.Failure, Result{})
		var refusal *NativeFailure
		if errors.As(err, &refusal) {
			return nil
		}
		return err
	case *k.ControlReply_LongEventPending:
		if v != nil && v.LongEventPending != nil && diagnostic(v.LongEventPending.Detail) {
			return nil
		}
		return contract("invalid clock pending result")
	default:
		return contract("clock control outcome missing")
	}
}
