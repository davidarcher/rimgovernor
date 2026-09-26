package bridge

import (
	"errors"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// ValidateClockExpectation checks immutable admission evidence, not live permission.
func ValidateClockExpectation(e ClockExpectation) error {
	if err := errors.Join(clockWire(e.Identity), clockWire(e.Attempt), ValidateIdentity(e.Identity), buildingAttempt(e.Attempt)); err != nil {
		return err
	}
	if e.NativeGeneration == 0 {
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
		if start.Speed < 1 || start.Speed > 4 || start.MaxTicks < 1 || start.MaxTicks > 1800000 {
			return contract("clock start speed or tick budget")
		}
		if start.TestAcceleration && start.Speed != k.Speed_SPEED_ULTRAFAST {
			return contract("clock test acceleration requires ultrafast")
		}
		if start.BlindTickBudget > 1800000 || start.MaxTicksPerSecond > 60000 {
			return contract("clock blind tick budget or tick rate ceiling")
		}
		if err := clockStartPacing(*start); err != nil {
			return err
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
		if speed.Speed < 1 || speed.Speed > 4 {
			return contract("clock ordinary speed required")
		}
		if speed.Original.GetTestAcceleration() && speed.Speed != k.Speed_SPEED_ULTRAFAST {
			return contract("clock accelerated epoch cannot change speed")
		}
		if speed.MaxTicksPerSecond != nil && (*speed.MaxTicksPerSecond < 1 || *speed.MaxTicksPerSecond > 60000) {
			return contract("clock tick rate ceiling")
		}
	}
	if err := clockEpoch(original); err != nil {
		return err
	}
	if original.Origin.GetNativeGeneration() != e.NativeGeneration || !sameIdentity(original.Origin.Identity, e.Identity) {
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
	if err := clockReceipt(r, e.Identity, e.Attempt, e.NativeGeneration); err != nil {
		return err
	}
	if r.GetUncertain() != nil {
		return nil
	}
	actual := clockStatusEpoch(r.GetApplied().GetStatus())
	if start := e.Command.Start; start != nil {
		if actual == nil || !proto.Equal(actual.Origin, r.AdmittedContext) || actual.GetTickDeadline()-actual.GetStartTick() != int64(start.MaxTicks) || actual.GetRequestedSpeed() != start.Speed || actual.GetTestAcceleration() != start.TestAcceleration || !proto.Equal(actual.Policy, start.Policy) || actual.GetLeaseRemainingMs() > start.LeaseMS || actual.GetBlindTickBudget() != start.BlindTickBudget || actual.GetMaxTicksPerSecond() != start.MaxTicksPerSecond || (actual.GetPacing() == k.Pacing_PACING_PLAYER_ACCELERATED) != start.PlayerAccelerated {
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
	if err := clockSameEpoch(actual, e.Command.Speed.Original, e.Command.Speed.Speed); err != nil {
		return err
	}
	if ceiling := e.Command.Speed.MaxTicksPerSecond; ceiling != nil && actual.GetMaxTicksPerSecond() != *ceiling {
		return contract("clock tick rate ceiling mismatch")
	}
	return nil
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

func clockStartPacing(start ClockStart) error {
	pacing, budget := start.WirePacing()
	return clockPacing(&k.StartRequest{Speed: start.Speed.Enum(), TestAcceleration: &start.TestAcceleration, Pacing: pacing, FrameBudgetMs: budget})
}
