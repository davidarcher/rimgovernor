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

// Cleanup joins commands and their journal writes, including after Stop. It never
// acquires authority or a lease; only the immutable original epoch can be paused.
func (q *ClockCoordinator) Cleanup(ctx context.Context) error {
	return q.CleanupObserved(ctx, nil)
}

// CleanupObserved is Cleanup given a clock status the caller has just read
// and validated (a scheduler step's bundle). An epoch obligation under that
// status's identity is observed from it instead of re-reading the identity
// and the status: two round trips that, behind the game host's one-at-a-time
// tool execution, used to sit between a window's stop and the review that
// admits the next one (issue #162). A nil status reads as before.
func (q *ClockCoordinator) CleanupObserved(ctx context.Context, observed *k.Status) error {
	select {
	case q.gate <- struct{}{}:
	case <-ctx.Done():
		return ctx.Err()
	}
	defer func() { <-q.gate }()
	if err := ctx.Err(); err != nil {
		return err
	}
	attempts, err := q.journal.LoadClockAttempts(ctx, 4096)
	if err != nil {
		return err
	}
	var failures []error
	for _, attempt := range attempts {
		if attempt.SupersededAt != nil || attempt.Intent.Command.Start == nil || (attempt.Phase != store.ClockDispatched && attempt.Phase != store.ClockUncertain) {
			continue
		}
		if err := ctx.Err(); err != nil {
			return errors.Join(append(failures, err)...)
		}
		call, cancel := context.WithTimeout(ctx, q.config.CallTimeout)
		err = q.cleanupStart(call, attempt)
		cancel()
		if err != nil {
			failures = append(failures, err)
		}
	}
	epochs, err := q.journal.LoadClockEpochs(ctx, 4096)
	if err != nil {
		return errors.Join(append(failures, err)...)
	}
	for _, epoch := range epochs {
		if clockCoordinatorTerminal(epoch.Stage) {
			continue
		}
		if err := ctx.Err(); err != nil {
			return errors.Join(append(failures, err)...)
		}
		call, cancel := context.WithTimeout(ctx, q.config.CallTimeout)
		err = q.cleanupEpoch(call, epoch, observed)
		cancel()
		if err != nil {
			failures = append(failures, err)
		}
	}
	return errors.Join(failures...)
}

func (q *ClockCoordinator) cleanupIdentity(ctx context.Context) (*c.ObservationContext, error) {
	reply, _, err := q.native.Identity(ctx)
	if err != nil {
		return nil, err
	}
	if err = ctx.Err(); err != nil {
		return nil, err
	}
	loaded := reply.GetLoaded()
	if loaded == nil {
		return nil, executor.ErrHeld
	}
	if len(reply.ProtoReflect().GetUnknown()) != 0 || len(loaded.ProtoReflect().GetUnknown()) != 0 {
		return nil, bridge.ErrContract
	}
	current := loaded.Context
	if err = bridge.ValidateContext(current); err != nil {
		return nil, err
	}
	if len(current.ProtoReflect().GetUnknown()) != 0 {
		return nil, bridge.ErrContract
	}
	return proto.Clone(current).(*c.ObservationContext), nil
}
func (q *ClockCoordinator) cleanupStart(ctx context.Context, v store.ClockAttempt) error {
	current, err := q.cleanupIdentity(ctx)
	if err != nil {
		return err
	}
	if !proto.Equal(current.Identity, boundary.Identity(v.Intent.Snapshot)) {
		_, err = q.journal.MarkClockScopeSuperseded(ctx, v.Intent.RequestID, current)
		return err
	}
	result, err := q.reconcileLocked(ctx, v.Intent.RequestID)
	if err != nil {
		return err
	}
	if result.SupersededAt == nil && (result.Phase == store.ClockDispatched || result.Phase == store.ClockUncertain) {
		return executor.ErrHeld
	}
	return nil
}
func (q *ClockCoordinator) cleanupEpoch(ctx context.Context, v store.ClockEpochObligation, observed *k.Status) error {
	var current *c.ObservationContext
	var status *k.Status
	var err error
	if observed != nil && proto.Equal(observed.Context.GetIdentity(), v.Epoch.Origin.Identity) {
		current, status = proto.Clone(observed.Context).(*c.ObservationContext), observed
	} else if current, err = q.cleanupIdentity(ctx); err != nil {
		return err
	}
	if status == nil && proto.Equal(current.Identity, v.Epoch.Origin.Identity) {
		reply, _, readErr := q.native.ReadClockStatus(ctx, current.Identity)
		if readErr != nil {
			return readErr
		}
		status = reply.GetStatus()
		if err = bridge.ValidateClockStatus(status, current.Identity); err != nil {
			return err
		}
		if status.Context.GetTick() < current.GetTick() || current.NativeGeneration != nil &&
			(status.Context.NativeGeneration == nil || status.Context.GetNativeGeneration() < current.GetNativeGeneration()) {
			return bridge.ErrContract
		}
		current = status.Context
	}
	if err = ctx.Err(); err != nil {
		return err
	}
	v, err = q.journal.ObserveClockEpoch(ctx, v.StartRequestID, v.Sequence, current, status)
	if err != nil {
		return err
	}
	if clockCoordinatorTerminal(v.Stage) {
		return nil
	}
	if v.Stage != store.ClockEpochRequired {
		return executor.ErrHeld
	}
	v, err = q.journal.BeginClockPause(ctx, v.StartRequestID, v.Sequence)
	if err != nil {
		return err
	}
	// Once dispatch is durable, cancellation cannot discard pause uncertainty.
	if err = ctx.Err(); err != nil {
		return q.cleanupPauseUncertain(v, err)
	}
	reply, _, callErr := q.writer.OwnedPause(ctx, &k.OwnedRequest{Identity: proto.Clone(v.Epoch.Origin.Identity).(*c.Identity), Owner: proto.Clone(v.Epoch.Owner).(*k.EpochOwner)})
	if callErr != nil || reply.GetStatus() == nil {
		return q.cleanupPauseUncertain(v, errors.Join(callErr, ctx.Err(), executor.ErrHeld))
	}
	status = reply.GetStatus()
	persist, cancel := context.WithTimeout(context.Background(), q.config.JournalTimeout)
	defer cancel()
	settled, err := q.journal.ObserveClockEpoch(persist, v.StartRequestID, v.Sequence, status.Context, status)
	if err != nil {
		return q.cleanupPauseUncertain(v, errors.Join(err, ctx.Err()))
	}
	if !clockCoordinatorTerminal(settled.Stage) {
		return errors.Join(ctx.Err(), executor.ErrHeld)
	}
	return ctx.Err()
}
func (q *ClockCoordinator) cleanupPauseUncertain(v store.ClockEpochObligation, cause error) error {
	persist, cancel := context.WithTimeout(context.Background(), q.config.JournalTimeout)
	defer cancel()
	_, err := q.journal.MarkClockPauseUncertain(persist, v.StartRequestID, v.Sequence)
	return errors.Join(cause, err)
}

// RepauseObserved pauses a game the player runs by hand under a stopped
// epoch this process owned (#601). The status is the stopped one the
// caller just read and validated (a scheduler step's bundle); the owned
// pause names its epoch, so a replacement owner's clock is never touched
// (ClockWriter.OwnedPause). The epoch is already settled, so nothing is
// journaled: the returned status is what the admission that follows reads
// its pause facts from.
func (q *ClockCoordinator) RepauseObserved(ctx context.Context, observed *k.Status) (*k.Status, error) {
	epoch := observed.GetStopped().GetEpoch()
	if epoch == nil || epoch.Owner == nil || epoch.Origin == nil || observed.Context.GetIdentity() == nil {
		return nil, executor.ErrEvidence
	}
	select {
	case q.gate <- struct{}{}:
	case <-ctx.Done():
		return nil, ctx.Err()
	}
	defer func() { <-q.gate }()
	call, cancel := context.WithTimeout(ctx, q.config.CallTimeout)
	defer cancel()
	identity := proto.Clone(observed.Context.GetIdentity()).(*c.Identity)
	reply, _, err := q.writer.OwnedPause(call, &k.OwnedRequest{Identity: identity, Owner: proto.Clone(epoch.Owner).(*k.EpochOwner)})
	if err != nil {
		return nil, err
	}
	status := reply.GetStatus()
	if err = bridge.ValidateClockStatus(status, identity); err != nil {
		return nil, err
	}
	if status.GetStopped() == nil || status.Context.GetTick() < observed.Context.GetTick() {
		return nil, executor.ErrEvidence
	}
	return status, nil
}
