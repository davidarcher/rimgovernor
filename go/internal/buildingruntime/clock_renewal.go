package buildingruntime

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

func (s *ClockScheduler) renewalHold(cause error) error {
	disabled := s.session.Disable()
	ctx, cancel := context.WithTimeout(context.Background(), s.session.control.config.CallTimeout)
	defer cancel()
	return errors.Join(cause, disabled, s.session.CleanupClock(ctx))
}

func clockRenewalID(start string, snapshot domain.GenerationSnapshot, epoch *k.Epoch, sequence uint64) (string, error) {
	key := struct {
		Start    string
		Snapshot domain.GenerationSnapshot
		Session  string
		Epoch    int64
		Sequence uint64
	}{start, snapshot, epoch.Owner.GetControllerSessionId(), epoch.Owner.GetEpoch(), sequence}
	data, err := json.Marshal(key)
	if err != nil {
		return "", err
	}
	hash := sha256.Sum256(data)
	return "clock-renew-" + hex.EncodeToString(hash[:]), nil
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
	attempts, err := s.player.journal.LoadClockAttempts(call, 4096)
	if err != nil {
		return out, s.renewalHold(err)
	}
	// Resolve the original command before considering another renewal identity.
	for _, attempt := range attempts {
		if attempt.Intent.Command.Renew != nil && (attempt.Phase == store.ClockDispatched || attempt.Phase == store.ClockUncertain) {
			recovered, e := s.session.ReconcileClock(call, attempt.Intent.RequestID)
			out.Attempt = &recovered
			out.Reconciled = true
			if e != nil || recovered.Phase != store.ClockApplied {
				return out, s.renewalHold(errors.Join(e, executor.ErrHeld))
			}
			if !state.Enabled || !state.ObservationKnown || state.Snapshot != attempt.Intent.Snapshot {
				return out, s.renewalHold(executor.ErrAuthority)
			}
			break
		}
	}
	if !state.Enabled || !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 || state.Snapshot.Direction == 0 || state.Snapshot.Revision == 0 {
		return out, s.renewalHold(executor.ErrAuthority)
	}
	epochs, err := s.player.journal.LoadClockEpochs(call, 4096)
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
	if _, err = boundaryContext(current, state.Snapshot); err != nil {
		return out, s.renewalHold(err)
	}
	reply, _, err := s.native.ReadClockStatus(call, current.Identity)
	if err != nil {
		return out, s.renewalHold(err)
	}
	status := reply.GetStatus()
	if err = bridge.ValidateClockStatus(status, current.Identity); err != nil {
		return out, s.renewalHold(err)
	}
	if _, err = boundaryContext(status.Context, state.Snapshot); err != nil {
		return out, s.renewalHold(err)
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
	if status.NewestCursor == nil || status.GetNewestCursor() != review.InboxCursor || review.ReviewedCursor != review.InboxCursor || len(review.Holds) != 0 {
		return out, s.renewalHold(executor.ErrHeld)
	}
	ended := s.clock.Now()
	if started.IsZero() || ended.Before(started) || ended.Sub(started) > s.config.MaxAge || call.Err() != nil || s.session.State() != state {
		return out, s.renewalHold(errors.Join(call.Err(), executor.ErrAuthority))
	}
	if out.Reconciled {
		return out, nil
	}
	var prepared *store.ClockAttempt
	var count uint64
	for _, attempt := range attempts {
		renew := attempt.Intent.Command.Renew
		if renew == nil || attempt.Intent.Snapshot != state.Snapshot || !clockCoordinatorSameEpoch(renew.Original, original) {
			continue
		}
		count++
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
		id, e := clockRenewalID(owned.StartRequestID, state.Snapshot, original, count+1)
		if e != nil {
			return out, s.renewalHold(e)
		}
		intent = store.ClockIntent{RequestID: id, Snapshot: state.Snapshot, Command: bridge.ClockCommand{Renew: &bridge.ClockRenew{Original: proto.Clone(original).(*k.Epoch), LeaseMS: s.config.Start.LeaseMS}}}
	}
	result, err := s.session.CommandClock(call, intent)
	out.Attempt = &result
	if err != nil || result.Phase != store.ClockApplied {
		return out, s.renewalHold(errors.Join(err, executor.ErrHeld))
	}
	out.Renewed = true
	return out, nil
}
