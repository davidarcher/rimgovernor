package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func (s *ClockScheduler) renewalHold(cause error) error {
	disabled := s.session.Disable()
	ctx, cancel := context.WithTimeout(context.Background(), s.session.control.config.CallTimeout)
	defer cancel()
	return errors.Join(cause, disabled, s.session.CleanupClock(ctx))
}

// RenewEpoch uses its own gate so a long player action cannot starve the native
// epoch lease. It never extends the retained tick deadline or acquires authority.
func (s *ClockScheduler) RenewEpoch(ctx context.Context) (ClockRenewResult, error) {
	var out ClockRenewResult
	call, cancel := context.WithTimeout(ctx, s.session.control.config.CallTimeout)
	defer cancel()
	select {
	case s.renewGate <- struct{}{}:
	case <-call.Done():
		return out, call.Err()
	}
	defer func() { <-s.renewGate }()
	if err := call.Err(); err != nil {
		return out, err
	}
	state := s.session.State()
	epochs, err := s.player.journal.LoadClockEpochs(call, 4096)
	if err != nil {
		return out, s.renewalHold(err)
	}
	attempts, err := s.player.journal.LoadClockAttempts(call, 4096)
	if err != nil {
		return out, s.renewalHold(err)
	}
	var owned *store.ClockEpochObligation
	for _, epoch := range epochs {
		if !clockCoordinatorTerminal(epoch.Stage) {
			if owned != nil {
				return out, s.renewalHold(executor.ErrHeld)
			}
			v := epoch
			owned = &v
		}
	}
	if owned == nil {
		return out, nil
	}
	if !state.Enabled || !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 || state.Snapshot.Revision == 0 {
		return out, s.renewalHold(executor.ErrAuthority)
	}
	if owned.Stage != store.ClockEpochRequired {
		return out, s.renewalHold(executor.ErrHeld)
	}
	start, err := s.player.journal.LookupClockAttempt(call, owned.StartRequestID)
	if err != nil || start.Intent.Snapshot != state.Snapshot || start.Phase != store.ClockApplied || start.Intent.Command.Start == nil {
		return out, s.renewalHold(errors.Join(err, executor.ErrAuthority))
	}
	original := owned.Epoch
	started := s.clock.Now()
	identity, _, err := s.native.Identity(call)
	if err != nil {
		return out, s.renewalHold(err)
	}
	current := identity.GetLoaded().GetContext()
	if _, err = boundary.Context(current, state.Snapshot); err != nil {
		return out, s.renewalHold(err)
	}
	// Only the currently retained nonterminal epoch can block renewal recovery.
	// Terminal epoch uncertainty remains historical evidence, not live work.
	for _, attempt := range attempts {
		renew := attempt.Intent.Command.Renew
		if renew == nil || !clockCoordinatorSameEpoch(renew.Original, original) || attempt.Intent.Snapshot != start.Intent.Snapshot || (attempt.Phase != store.ClockDispatched && attempt.Phase != store.ClockUncertain) {
			continue
		}
		recovered, e := s.session.ReconcileClock(call, attempt.Intent.RequestID)
		out.Attempt, out.Reconciled = &recovered, true
		if e != nil || recovered.Phase != store.ClockApplied {
			return out, s.renewalHold(errors.Join(e, executor.ErrHeld))
		}
		break
	}
	reply, _, err := s.native.ReadClockStatus(call, current.Identity)
	if err != nil {
		return out, s.renewalHold(err)
	}
	status := reply.GetStatus()
	if err = bridge.ValidateClockStatus(status, current.Identity); err != nil {
		return out, s.renewalHold(err)
	}
	if _, err = boundary.Context(status.Context, state.Snapshot); err != nil {
		return out, s.renewalHold(err)
	}
	// A completed finite window needs event review and scheduler cleanup, not a
	// renewal. Those paths retain the stop evidence before admitting another window.
	if clockWindowFinished(status, original) && status.Context.GetTick() >= current.GetTick() {
		return out, nil
	}
	actual := status.GetRunning().GetEpoch()
	if actual == nil || !clockCoordinatorSameEpoch(original, actual) || actual.GetRequestedSpeed() != original.GetRequestedSpeed() || actual.GetLastTick() < original.GetLastTick() || status.Context.GetTick() < current.GetTick() {
		return out, s.renewalHold(executor.ErrHeld)
	}
	if owned.Context != nil && (status.Context.GetTick() < owned.Context.GetTick() || owned.Context.NativeGeneration != nil && (status.Context.NativeGeneration == nil || status.Context.GetNativeGeneration() < owned.Context.GetNativeGeneration())) {
		return out, s.renewalHold(executor.ErrHeld)
	}
	review, err := s.player.journal.ReadClockReview(call, s.config.Profile)
	if err != nil {
		return out, s.renewalHold(err)
	}
	if status.NewestCursor == nil || status.GetNewestCursor() < review.InboxCursor || review.ReviewedCursor > review.InboxCursor || len(review.Holds) != 0 {
		return out, s.renewalHold(executor.ErrHeld)
	}
	if status.GetNewestCursor() > review.InboxCursor || review.ReviewedCursor < review.InboxCursor {
		// Do not extend the native lease until the independent poller catches up.
		// The poller owns interruption invalidation; fresh benign events need no
		// new player acquisition merely because renewal observed them first.
		return out, executor.ErrHeld
	}
	ended := s.clock.Now()
	if started.IsZero() || ended.Before(started) || ended.Sub(started) > s.config.MaxAge || call.Err() != nil || s.session.State() != state {
		return out, s.renewalHold(errors.Join(call.Err(), executor.ErrAuthority))
	}
	if out.Reconciled {
		return out, nil
	}
	var prepared *store.ClockAttempt
	for _, attempt := range attempts {
		renew := attempt.Intent.Command.Renew
		if renew == nil || attempt.Intent.Snapshot != state.Snapshot || !clockCoordinatorSameEpoch(renew.Original, original) {
			continue
		}
		if attempt.Phase == store.ClockPrepared {
			if prepared != nil || !proto.Equal(renew.Original, original) || renew.LeaseMS != s.config.Start.LeaseMS {
				return out, s.renewalHold(executor.ErrHeld)
			}
			v := attempt
			prepared = &v
		}
	}
	var intent store.ClockIntent
	if prepared != nil {
		intent = prepared.Intent
	} else {
		sequence, e := s.player.journal.ReadClockSequence(call)
		if e != nil {
			return out, s.renewalHold(e)
		}
		id, e := sequence.NextRequestID()
		if e != nil {
			return out, s.renewalHold(e)
		}
		intent = store.ClockIntent{RequestID: id, Snapshot: state.Snapshot, Command: bridge.ClockCommand{Renew: &bridge.ClockRenew{Original: proto.Clone(original).(*k.Epoch), LeaseMS: s.config.Start.LeaseMS}}}
	}
	result, err := s.session.CommandClock(call, intent)
	out.Attempt = &result
	if err != nil || result.Phase != store.ClockApplied {
		// A finite window can finish before preflight or before native renewal.
		// An undispatched request or explicit stopped-epoch refusal needs no lease
		// invalidation when fresh native proof shows that exact budget completed.
		// Uncertain writes and actual interruptions retain the conservative hold.
		undispatched := errors.Is(err, executor.ErrHeld) && result.Phase == store.ClockPrepared
		stoppedRefusal := errors.Is(err, bridge.ErrRefused) && result.Phase == store.ClockRefused && result.Reply.GetFailure().GetCode() == c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED
		if (undispatched || stoppedRefusal) && result.Intent.RequestID == intent.RequestID && call.Err() == nil && s.session.State() == state {
			latest, _, readErr := s.native.ReadClockStatus(call, current.Identity)
			observed := latest.GetStatus()
			if readErr == nil && bridge.ValidateClockStatus(observed, current.Identity) == nil {
				_, scopeErr := boundary.Context(observed.Context, state.Snapshot)
				if scopeErr == nil && clockWindowFinished(observed, original) && observed.Context.GetTick() >= status.Context.GetTick() && call.Err() == nil && s.session.State() == state {
					return out, nil
				}
			}
		}
		return out, s.renewalHold(errors.Join(err, executor.ErrHeld))
	}
	out.Renewed = true
	return out, nil
}

// clockWindowFinished recognizes a window that ended on the controller's own
// terms: the tick budget exactly, or a latched watch anywhere inside it.
func clockWindowFinished(status *k.Status, original *k.Epoch) bool {
	stopped := status.GetStopped()
	actual := stopped.GetEpoch()
	if stopped == nil || !status.GetActualPaused() || !status.GetNativeTickBoundary() || !stopped.GetPauseVerified() {
		return false
	}
	switch stopped.GetReason() {
	case k.StopReason_STOP_REASON_TICK_BUDGET:
		if actual.GetLastTick() != original.GetTickDeadline() {
			return false
		}
	case k.StopReason_STOP_REASON_WATCH_LATCHED:
		if actual.GetLastTick() > original.GetTickDeadline() {
			return false
		}
	default:
		return false
	}
	return clockCoordinatorSameEpoch(original, actual) && actual.GetRequestedSpeed() == original.GetRequestedSpeed() && status.GetContext().GetTick() >= actual.GetLastTick()
}
