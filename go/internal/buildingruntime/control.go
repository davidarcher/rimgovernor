package buildingruntime

import (
	"context"
	"errors"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/runtimeowner"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

var ErrControl = errors.New("writer authority unavailable")

// liveToken is an opaque non-empty sentinel returned by Lease while the bot
// holds Auto-mode authority. There is no negotiated lease ID any more (see
// #52); callers only ever check it for presence/validity, never compare it
// against a value the native side returned.
const liveToken = "auto"

// NativeAuthority combines the separately held read client and trusted authority
// capability. Neither a model nor the read-only HTTP service receives this owner.
type NativeAuthority interface {
	ReadAuthority(context.Context, *c.Identity) (*a.StatusReply, bridge.Result, error)
	SetMode(context.Context, *a.SetMode) (*a.ControlReply, bridge.Result, error)
	Revoke(context.Context, *a.Revoke) (*a.ControlReply, bridge.Result, error)
}
type SessionIdentity interface {
	Identity(context.Context) (store.ControllerSessionID, error)
}

// UpdateAuthority must synchronously invalidate queued/active writes and must
// not call back into Control. StopWrites subsequently joins those writers.
type AuthoritySink interface {
	UpdateAuthority(executor.Authority) error
}
type ControlConfig struct {
	ProfileDirectory string
	CallTimeout      time.Duration
	StopWrites       func(context.Context) error
	// CleanupWrites joins invalidated commands and drains owned effects under
	// the control gate. It must not call back into Control or acquire a lease.
	CleanupWrites func(context.Context) error
	// Worlds reads actual native identity without entering the control gate.
	// Without it, shutdown cannot retire a target by proving world replacement.
	Worlds WorldSource
}

// Control owns the shared profile lock until controls and native writers drain.
// Auto means the bot holds authority outright; there is no negotiated lease to
// renew or expire. A local-player interruption (detected natively) or an
// explicit call to Manual is what ends authority, and either bumps the
// generation the next write must match.
type Control struct {
	mu               sync.Mutex
	gate             chan struct{}
	config           ControlConfig
	native           NativeAuthority
	sink             AuthoritySink
	namespace        store.ControllerSessionID
	owner            *runtimeowner.Owner
	lifetime         context.Context
	epoch            context.Context
	cancelEpoch      context.CancelFunc
	stopLifetime     func() bool
	snapshot         domain.GenerationSnapshot
	haveTarget       bool
	observationKnown bool
	active           bool
	closing, closed  bool
}

func NewControl(ctx context.Context, config ControlConfig, identity SessionIdentity, native NativeAuthority, sink AuthoritySink) (*Control, error) {
	if identity == nil || native == nil || sink == nil || config.StopWrites == nil || config.CallTimeout <= 0 || config.CallTimeout > time.Minute {
		return nil, ErrControl
	}
	owner, err := runtimeowner.Acquire(ctx, config.ProfileDirectory)
	if err != nil {
		return nil, err
	}
	namespace, err := identity.Identity(ctx)
	if err != nil || !controlID(string(namespace)) {
		owner.Close()
		return nil, errors.Join(ErrControl, err)
	}
	epoch, cancel := context.WithCancel(ctx)
	control := &Control{gate: make(chan struct{}, 1), config: config, native: native, sink: sink, namespace: namespace, owner: owner, lifetime: ctx, epoch: epoch, cancelEpoch: cancel}
	if err := sink.UpdateAuthority(executor.Authority{}); err != nil {
		cancel()
		owner.Close()
		return nil, err
	}
	control.stopLifetime = context.AfterFunc(ctx, func() {
		control.mu.Lock()
		defer control.mu.Unlock()
		if !control.closed {
			control.invalidateLocked()
		}
	})
	return control, nil
}

// Acquire must be called only by an authenticated explicit-player entrypoint.
// The wire contract carries identity, generation and the requested Mode; the
// requested snapshot's plan/revision are scoped intent, not authentication.
// Fresh native CAS replaces the requested native generation.
func (control *Control) Acquire(ctx context.Context, requested domain.GenerationSnapshot) (domain.GenerationSnapshot, error) {
	if err := requested.Validate(); err != nil {
		return domain.GenerationSnapshot{}, err
	}
	if requested.Revision == 0 {
		return domain.GenerationSnapshot{}, ErrControl
	}
	control.mu.Lock()
	if control.closing || control.lifetime.Err() != nil {
		control.mu.Unlock()
		return domain.GenerationSnapshot{}, ErrControl
	}
	err := control.invalidateLocked()
	epoch := control.epoch
	control.mu.Unlock()
	if err != nil {
		return domain.GenerationSnapshot{}, err
	}
	call, done, err := control.enter(ctx, epoch)
	if err != nil {
		return domain.GenerationSnapshot{}, err
	}
	defer done()
	if control.config.CleanupWrites != nil {
		if err = control.config.CleanupWrites(call); err != nil {
			return domain.GenerationSnapshot{}, err
		}
	}
	status, _, err := control.native.ReadAuthority(call, controlIdentity(requested))
	if err != nil {
		return domain.GenerationSnapshot{}, control.failedObservation(epoch, err)
	}
	if err = controlStatus(status, requested); err != nil {
		return domain.GenerationSnapshot{}, control.failedObservation(epoch, err)
	}
	if err := call.Err(); err != nil {
		return domain.GenerationSnapshot{}, err
	}
	if status.GetStatus().GetInactive() == nil {
		return domain.GenerationSnapshot{}, ErrControl
	}
	generation := status.GetStatus().Context.GetNativeGeneration()
	if generation == ^uint64(0) {
		return domain.GenerationSnapshot{}, ErrControl
	}
	requested.Native = domain.NativeGeneration(generation)
	control.mu.Lock()
	if epoch.Err() != nil || control.closing {
		control.mu.Unlock()
		return domain.GenerationSnapshot{}, ErrControl
	}
	control.snapshot, control.haveTarget = requested, true
	control.mu.Unlock()
	reply, _, err := control.native.SetMode(call, &a.SetMode{Identity: controlIdentity(requested), ExpectedGeneration: proto.Uint64(generation), Mode: a.Mode_MODE_AUTO.Enum()})
	if err != nil {
		return domain.GenerationSnapshot{}, err
	}
	requested.Native++
	if err = control.acceptGrant(call, epoch, reply, requested); err != nil {
		return domain.GenerationSnapshot{}, err
	}
	return requested, nil
}

// Lease reports whether the bot currently holds Auto-mode authority for the
// given snapshot. It is checked again immediately before native dispatch.
// The returned token is opaque and carries no native identity; it exists only
// so callers can distinguish "authority live" from "authority unavailable"
// with the same presence check they used against the old lease ID.
func (control *Control) Lease(snapshot domain.GenerationSnapshot) (string, error) {
	control.mu.Lock()
	defer control.mu.Unlock()
	if !control.liveLocked() || !control.snapshot.Matches(snapshot) {
		return "", ErrControl
	}
	return liveToken, nil
}

// Manual disables local authority before waiting for any native call. Inspection
// reconciles an uncertain SetMode call, but never adopts a grant from it.
func (control *Control) Manual(ctx context.Context) error {
	control.mu.Lock()
	if control.closing {
		control.mu.Unlock()
		return ErrControl
	}
	err := control.invalidateLocked()
	epoch := control.epoch
	control.mu.Unlock()
	call, done, enterErr := control.enter(ctx, epoch)
	if enterErr != nil {
		return errors.Join(err, enterErr)
	}
	defer done()
	err = errors.Join(err, control.revoke(call, a.RevocationReason_REVOCATION_REASON_MANUAL))
	if control.config.CleanupWrites != nil {
		err = errors.Join(err, control.config.CleanupWrites(call))
	}
	return err
}

// Close is retryable when writer drain or native revoke fails. Authority stays
// disabled and the profile lock remains held. The caller retains bridge/store ownership until
// Close succeeds. No uncertain native response is translated into a retry grant.
func (control *Control) Close(ctx context.Context) error {
	control.mu.Lock()
	if control.closed {
		control.mu.Unlock()
		return nil
	}
	control.closing = true
	err := control.invalidateLocked()
	control.mu.Unlock()
	call, cancel := context.WithTimeout(ctx, control.config.CallTimeout)
	defer cancel()
	select {
	case control.gate <- struct{}{}:
		defer func() { <-control.gate }()
	case <-call.Done():
		return errors.Join(err, call.Err())
	}
	if call.Err() != nil {
		return errors.Join(err, call.Err())
	}
	control.mu.Lock()
	closed := control.closed
	control.mu.Unlock()
	if closed {
		return nil
	}
	if drainErr := control.config.StopWrites(call); drainErr != nil {
		return errors.Join(err, drainErr)
	}
	revokeErr := control.shutdownTarget(call)
	control.mu.Lock()
	defer control.mu.Unlock()
	if err != nil || revokeErr != nil {
		return errors.Join(err, revokeErr)
	}
	closeErr := control.owner.Close()
	if closeErr == nil {
		control.closed = true
		control.stopLifetime()
	}
	return closeErr
}

// shutdownTarget runs under the control gate after writers have drained. A
// positively observed replacement retires only the old revoke target; it never
// adopts or writes authority in the replacement world.
func (control *Control) shutdownTarget(ctx context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	control.mu.Lock()
	snapshot, known := control.snapshot, control.haveTarget
	control.mu.Unlock()
	if known && control.config.Worlds != nil {
		actual, err := control.config.Worlds.ReadWorld(ctx)
		if err != nil {
			return err
		}
		if err = ctx.Err(); err != nil {
			return err
		}
		if err = actual.Validate(); err != nil {
			return err
		}
		if actual != playerWorld(snapshot) {
			return nil
		}
	}
	return control.revoke(ctx, a.RevocationReason_REVOCATION_REASON_SHUTDOWN)
}

func (control *Control) invalidateLocked() error {
	control.cancelEpoch()
	control.epoch, control.cancelEpoch = context.WithCancel(control.lifetime)
	control.active = false
	value := executor.Authority{}
	if control.observationKnown {
		value.Snapshot = control.snapshot
	}
	return control.sink.UpdateAuthority(value)
}

// A cleanup identity is not current observation authority. Keep it for revoke
// and refresh retries while cancelling reconciliation until a fresh read succeeds.
func (control *Control) failedObservation(epoch context.Context, cause error) error {
	control.mu.Lock()
	defer control.mu.Unlock()
	if control.epoch != epoch || control.closing {
		return cause
	}
	return control.failedObservationLocked(cause)
}
func (control *Control) failedObservationLocked(cause error) error {
	control.observationKnown = false
	return errors.Join(cause, control.invalidateLocked())
}

// liveLocked reports whether authority is currently held. There is no lease
// deadline any more; live means "the last observation/grant said Auto and
// nothing has invalidated it since" (see invalidateLocked).
func (control *Control) liveLocked() bool {
	return !control.closing && control.lifetime.Err() == nil && control.active
}
func (control *Control) enter(ctx, epoch context.Context) (context.Context, func(), error) {
	call, cancel := context.WithTimeout(ctx, control.config.CallTimeout)
	stop := context.AfterFunc(epoch, cancel)
	cleanup := func() { stop(); cancel() }
	select {
	case control.gate <- struct{}{}:
	case <-call.Done():
		cleanup()
		return nil, nil, call.Err()
	}
	if err := call.Err(); err != nil {
		<-control.gate
		cleanup()
		return nil, nil, err
	}
	if epoch.Err() != nil {
		<-control.gate
		cleanup()
		return nil, nil, ErrControl
	}
	return call, func() { <-control.gate; cleanup() }, nil
}
func (control *Control) acceptGrant(call, epoch context.Context, reply *a.ControlReply, snapshot domain.GenerationSnapshot) error {
	grant := reply.GetGranted()
	if grant == nil || bridge.ValidateContext(grant.Context) != nil || !proto.Equal(grant.Context.Identity, controlIdentity(snapshot)) || grant.Context.NativeGeneration == nil || grant.Context.GetNativeGeneration() != uint64(snapshot.Native) || grant.Authority == nil || grant.Authority.GetMode() != a.Mode_MODE_AUTO {
		return ErrControl
	}
	control.mu.Lock()
	defer control.mu.Unlock()
	if call.Err() != nil || epoch.Err() != nil || control.closing {
		return ErrControl
	}
	control.snapshot = snapshot
	control.active = true
	control.observationKnown = true
	if err := control.sink.UpdateAuthority(executor.Authority{Snapshot: snapshot, Enabled: true}); err != nil {
		control.invalidateLocked()
		return err
	}
	return nil
}
func (control *Control) revoke(ctx context.Context, reason a.RevocationReason) error {
	control.mu.Lock()
	snapshot, known, epoch := control.snapshot, control.haveTarget, control.epoch
	control.mu.Unlock()
	if !known {
		return nil
	}
	reply, _, err := control.native.ReadAuthority(ctx, controlIdentity(snapshot))
	if err != nil {
		return control.failedObservation(epoch, err)
	}
	if err = controlStatus(reply, snapshot); err != nil {
		return control.failedObservation(epoch, err)
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	status := reply.GetStatus()
	if status.GetInactive() != nil {
		return control.publishDisabled(ctx, snapshot, status.Context.GetNativeGeneration())
	}
	active := status.GetActive()
	if active == nil || active.GetMode() != a.Mode_MODE_AUTO {
		return ErrControl
	}
	generation := status.Context.GetNativeGeneration()
	if generation == ^uint64(0) {
		return ErrControl
	}
	result, _, err := control.native.Revoke(ctx, &a.Revoke{Identity: controlIdentity(snapshot), ExpectedGeneration: proto.Uint64(generation), Reason: reason.Enum()})
	if err != nil {
		return err
	}
	revoked := result.GetRevoked()
	if revoked == nil || bridge.ValidateContext(revoked.Context) != nil || !proto.Equal(revoked.Context.Identity, controlIdentity(snapshot)) || revoked.Context.NativeGeneration == nil || revoked.Context.GetNativeGeneration() != generation+1 || revoked.Authority == nil || revoked.Authority.Reason == nil || revoked.Authority.GetReason() != reason {
		return ErrControl
	}
	return control.publishDisabled(ctx, snapshot, revoked.Context.GetNativeGeneration())
}

func (control *Control) publishDisabled(ctx context.Context, expected domain.GenerationSnapshot, generation uint64) error {
	control.mu.Lock()
	defer control.mu.Unlock()
	if ctx.Err() != nil || !control.snapshot.Matches(expected) {
		return ErrControl
	}
	control.snapshot.Native = domain.NativeGeneration(generation)
	control.active = false
	control.observationKnown = true
	return control.sink.UpdateAuthority(executor.Authority{Snapshot: control.snapshot})
}

// ObserveTarget seeds a read-only restart target after the caller validates the
// durable plan/revision. It cannot replace live authority or adopt a grant.
func (control *Control) ObserveTarget(ctx context.Context, requested domain.GenerationSnapshot) error {
	if err := requested.Validate(); err != nil {
		return err
	}
	if requested.Revision == 0 {
		return ErrControl
	}
	control.mu.Lock()
	if control.closing || control.liveLocked() {
		control.mu.Unlock()
		return ErrControl
	}
	epoch := control.epoch
	control.mu.Unlock()
	call, done, err := control.enter(ctx, epoch)
	if err != nil {
		return err
	}
	defer done()
	control.mu.Lock()
	if control.closing || epoch.Err() != nil || control.epoch != epoch || control.liveLocked() {
		control.mu.Unlock()
		return ErrControl
	}
	control.mu.Unlock()
	reply, _, err := control.native.ReadAuthority(call, controlIdentity(requested))
	if err == nil {
		err = controlStatus(reply, requested)
	}
	control.mu.Lock()
	defer control.mu.Unlock()
	if call.Err() != nil || epoch.Err() != nil || control.epoch != epoch || control.closing {
		cause := errors.Join(ErrControl, call.Err())
		if control.epoch == epoch && !control.closing {
			return control.failedObservationLocked(cause)
		}
		return cause
	}
	if err != nil {
		return control.failedObservationLocked(err)
	}
	requested.Native = domain.NativeGeneration(reply.GetStatus().Context.GetNativeGeneration())
	control.snapshot, control.haveTarget = requested, true
	control.observationKnown = true
	return control.sink.UpdateAuthority(executor.Authority{Snapshot: control.snapshot})
}

// Refresh observes the stored target without acquiring authority. Known
// revocation/generation changes remain available for read reconciliation.
func (control *Control) Refresh(ctx context.Context) error {
	control.mu.Lock()
	if control.closing || !control.haveTarget {
		control.mu.Unlock()
		return ErrControl
	}
	epoch, snapshot := control.epoch, control.snapshot
	control.mu.Unlock()
	call, done, err := control.enter(ctx, epoch)
	if err != nil {
		return err
	}
	defer done()
	reply, _, err := control.native.ReadAuthority(call, controlIdentity(snapshot))
	if err == nil {
		err = controlStatus(reply, snapshot)
	}
	control.mu.Lock()
	defer control.mu.Unlock()
	if call.Err() != nil || epoch.Err() != nil || control.epoch != epoch || control.closing {
		cause := errors.Join(ErrControl, call.Err())
		if control.epoch == epoch && !control.closing {
			return control.failedObservationLocked(cause)
		}
		return cause
	}
	if err != nil {
		return control.failedObservationLocked(err)
	}
	status := reply.GetStatus()
	active := status.GetActive()
	if status.Context.GetNativeGeneration() != uint64(snapshot.Native) || active == nil || active.GetMode() != a.Mode_MODE_AUTO {
		err = control.invalidateLocked()
		control.snapshot.Native = domain.NativeGeneration(status.Context.GetNativeGeneration())
		control.observationKnown = true
		return errors.Join(err, control.sink.UpdateAuthority(executor.Authority{Snapshot: control.snapshot}))
	}
	control.active = true
	return nil
}
func controlStatus(reply *a.StatusReply, snapshot domain.GenerationSnapshot) error {
	status := reply.GetStatus()
	if status == nil || bridge.ValidateContext(status.Context) != nil || !proto.Equal(status.Context.Identity, controlIdentity(snapshot)) || status.Context.NativeGeneration == nil {
		return ErrControl
	}
	if inactive := status.GetInactive(); inactive != nil {
		if inactive.Reason == nil || *inactive.Reason <= 0 || *inactive.Reason > a.RevocationReason_REVOCATION_REASON_UNAVAILABLE {
			return ErrControl
		}
		return nil
	}
	if active := status.GetActive(); active != nil && active.GetMode() == a.Mode_MODE_AUTO {
		return nil
	}
	return ErrControl
}
func controlIdentity(s domain.GenerationSnapshot) *c.Identity {
	return &c.Identity{ColonyId: proto.String(string(s.Colony)), LoadToken: proto.String(string(s.Load)), MapId: proto.Int32(int32(s.Map))}
}
func controlID(value string) bool {
	return utf8.ValidString(value) && strings.TrimSpace(value) != "" && len(value) <= 256 && !strings.ContainsRune(value, 0)
}
