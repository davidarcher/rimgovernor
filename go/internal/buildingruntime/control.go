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

// NativeAuthority combines the separately held read client and trusted authority
// capability. Neither a model nor the read-only HTTP service receives this owner.
type NativeAuthority interface {
	ReadAuthority(context.Context, *c.Identity) (*a.StatusReply, bridge.Result, error)
	Acquire(context.Context, *a.Acquire) (*a.ControlReply, bridge.Result, error)
	Renew(context.Context, *a.Renew, *a.Owner) (*a.ControlReply, bridge.Result, error)
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
	ProfileDirectory           string
	LeaseDuration, CallTimeout time.Duration
	StopWrites                 func(context.Context) error
}

// Control owns the shared profile lock until controls and native writers drain.
// It renews only on explicit calls. A lease is not permission to resume the clock.
type Control struct {
	mu              sync.Mutex
	gate            chan struct{}
	config          ControlConfig
	native          NativeAuthority
	sink            AuthoritySink
	namespace       store.ControllerSessionID
	owner           *runtimeowner.Owner
	lifetime        context.Context
	epoch           context.Context
	cancelEpoch     context.CancelFunc
	stopLifetime    func() bool
	timer           *time.Timer
	snapshot        domain.GenerationSnapshot
	haveTarget      bool
	lease           string
	deadline        time.Time
	closing, closed bool
}

func NewControl(ctx context.Context, config ControlConfig, identity SessionIdentity, native NativeAuthority, sink AuthoritySink) (*Control, error) {
	if identity == nil || native == nil || sink == nil || config.StopWrites == nil || config.LeaseDuration < time.Second || config.LeaseDuration > 30*time.Second || config.LeaseDuration%time.Millisecond != 0 || config.CallTimeout <= 0 || config.CallTimeout > time.Minute {
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
// A requested direction is scoped intent, not authentication. Fresh native CAS
// replaces the requested native generation; an existing owner is never adopted.
func (control *Control) Acquire(ctx context.Context, requested domain.GenerationSnapshot) (domain.GenerationSnapshot, error) {
	if err := requested.Validate(); err != nil {
		return domain.GenerationSnapshot{}, err
	}
	if requested.Direction == 0 || requested.Revision == 0 {
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
	status, _, err := control.native.ReadAuthority(call, controlIdentity(requested))
	if err != nil {
		return domain.GenerationSnapshot{}, err
	}
	if err = controlStatus(status, requested); err != nil {
		return domain.GenerationSnapshot{}, err
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
	started := time.Now()
	reply, _, err := control.native.Acquire(call, &a.Acquire{Identity: controlIdentity(requested), ExpectedGeneration: proto.Uint64(generation), Owner: control.controlOwner(requested), LeaseMs: proto.Uint32(uint32(control.config.LeaseDuration / time.Millisecond))})
	if err != nil {
		return domain.GenerationSnapshot{}, err
	}
	requested.Native++
	if err = control.acceptGrant(call, epoch, reply, requested, "", started); err != nil {
		return domain.GenerationSnapshot{}, err
	}
	return requested, nil
}

func (control *Control) Renew(ctx context.Context) error {
	control.mu.Lock()
	if !control.liveLocked(time.Now()) {
		control.mu.Unlock()
		return ErrControl
	}
	epoch, snapshot, lease, deadline := control.epoch, control.snapshot, control.lease, control.deadline
	control.mu.Unlock()
	call, done, err := control.enter(ctx, epoch)
	if err != nil {
		return err
	}
	defer done()
	call, cancel := context.WithDeadline(call, deadline)
	defer cancel()
	if err := call.Err(); err != nil {
		return err
	}
	started := time.Now()
	reply, _, err := control.native.Renew(call, &a.Renew{Identity: controlIdentity(snapshot), ExpectedGeneration: proto.Uint64(uint64(snapshot.Native)), ControllerSessionId: proto.String(string(control.namespace)), LeaseId: proto.String(lease), LeaseMs: proto.Uint32(uint32(control.config.LeaseDuration / time.Millisecond))}, control.controlOwner(snapshot))
	if err == nil {
		err = control.acceptGrant(call, epoch, reply, snapshot, lease, started)
	}
	if err != nil {
		control.mu.Lock()
		if control.epoch == epoch {
			err = errors.Join(err, control.invalidateLocked())
		}
		control.mu.Unlock()
	}
	return err
}

// Lease is checked again immediately before native dispatch. Unknown, expired,
// revoked or differently scoped authority cannot supply a lease ID.
func (control *Control) Lease(snapshot domain.GenerationSnapshot) (string, error) {
	control.mu.Lock()
	defer control.mu.Unlock()
	if !control.liveLocked(time.Now()) || !control.snapshot.Matches(snapshot) {
		return "", ErrControl
	}
	return control.lease, nil
}

// Manual disables local authority before waiting for any native call. Inspection
// reconciles an uncertain acquire/renew, but never adopts its lease or retries it.
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
	return errors.Join(err, control.revoke(call, a.RevocationReason_REVOCATION_REASON_MANUAL))
}

// Close is retryable when StopWrites fails: local authority stays disabled and
// the profile lock remains held. The caller retains bridge/store ownership until
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
	revokeErr := control.revoke(call, a.RevocationReason_REVOCATION_REASON_SHUTDOWN)
	control.mu.Lock()
	defer control.mu.Unlock()
	if err != nil {
		return errors.Join(err, revokeErr)
	}
	closeErr := control.owner.Close()
	if closeErr == nil {
		control.closed = true
		control.stopLifetime()
	}
	return errors.Join(revokeErr, closeErr)
}

func (control *Control) invalidateLocked() error {
	control.cancelEpoch()
	control.epoch, control.cancelEpoch = context.WithCancel(control.lifetime)
	if control.timer != nil {
		control.timer.Stop()
		control.timer = nil
	}
	control.lease = ""
	control.deadline = time.Time{}
	return control.sink.UpdateAuthority(executor.Authority{Snapshot: control.snapshot})
}
func (control *Control) liveLocked(now time.Time) bool {
	if control.closing || control.lifetime.Err() != nil || control.lease == "" {
		return false
	}
	if !now.Before(control.deadline) {
		control.invalidateLocked()
		return false
	}
	return true
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
func (control *Control) acceptGrant(call, epoch context.Context, reply *a.ControlReply, snapshot domain.GenerationSnapshot, lease string, started time.Time) error {
	grant := reply.GetGranted()
	if grant == nil || bridge.ValidateContext(grant.Context) != nil || !proto.Equal(grant.Context.Identity, controlIdentity(snapshot)) || grant.Context.NativeGeneration == nil || grant.Context.GetNativeGeneration() != uint64(snapshot.Native) || grant.Authority == nil || !proto.Equal(grant.Authority.Owner, control.controlOwner(snapshot)) || grant.Authority.RemainingLeaseMs == nil || grant.Authority.GetRemainingLeaseMs() == 0 || time.Duration(grant.Authority.GetRemainingLeaseMs())*time.Millisecond > control.config.LeaseDuration || !controlID(grant.GetLeaseId()) || lease != "" && grant.GetLeaseId() != lease {
		return ErrControl
	}
	deadline := started.Add(time.Duration(grant.Authority.GetRemainingLeaseMs()) * time.Millisecond)
	control.mu.Lock()
	defer control.mu.Unlock()
	if call.Err() != nil || epoch.Err() != nil || control.closing || !time.Now().Before(deadline) {
		return ErrControl
	}
	control.snapshot, control.lease, control.deadline = snapshot, grant.GetLeaseId(), deadline
	if err := control.sink.UpdateAuthority(executor.Authority{Snapshot: snapshot, Enabled: true}); err != nil {
		control.invalidateLocked()
		return err
	}
	control.armTimerLocked(epoch)
	return nil
}
func (control *Control) armTimerLocked(epoch context.Context) {
	if control.timer != nil {
		control.timer.Stop()
	}
	deadline := control.deadline
	control.timer = time.AfterFunc(time.Until(deadline), func() {
		control.mu.Lock()
		defer control.mu.Unlock()
		if !control.closed && control.epoch == epoch && control.deadline == deadline {
			control.invalidateLocked()
		}
	})
}
func (control *Control) revoke(ctx context.Context, reason a.RevocationReason) error {
	control.mu.Lock()
	snapshot, known := control.snapshot, control.haveTarget
	control.mu.Unlock()
	if !known {
		return nil
	}
	reply, _, err := control.native.ReadAuthority(ctx, controlIdentity(snapshot))
	if err != nil {
		return err
	}
	if err = controlStatus(reply, snapshot); err != nil {
		return err
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	status := reply.GetStatus()
	if status.GetInactive() != nil {
		return control.publishDisabled(ctx, snapshot, status.Context.GetNativeGeneration())
	}
	active := status.GetActive()
	if active == nil || !proto.Equal(active.Owner, control.controlOwner(snapshot)) {
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
	return control.sink.UpdateAuthority(executor.Authority{Snapshot: control.snapshot})
}

// Refresh observes the stored target without acquiring or extending authority.
// Known revocation/generation changes remain available for read reconciliation.
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
	started := time.Now()
	reply, _, err := control.native.ReadAuthority(call, controlIdentity(snapshot))
	if err == nil {
		err = controlStatus(reply, snapshot)
	}
	control.mu.Lock()
	defer control.mu.Unlock()
	if call.Err() != nil || epoch.Err() != nil || control.epoch != epoch || control.closing {
		return errors.Join(ErrControl, call.Err())
	}
	if err != nil {
		return errors.Join(err, control.invalidateLocked())
	}
	status := reply.GetStatus()
	active := status.GetActive()
	if status.Context.GetNativeGeneration() != uint64(snapshot.Native) || active == nil || !proto.Equal(active.Owner, control.controlOwner(snapshot)) || !control.liveLocked(time.Now()) {
		err = control.invalidateLocked()
		control.snapshot.Native = domain.NativeGeneration(status.Context.GetNativeGeneration())
		return errors.Join(err, control.sink.UpdateAuthority(executor.Authority{Snapshot: control.snapshot}))
	}
	// A read may shorten a known lease, never extend or resurrect it.
	observedDeadline := started.Add(time.Duration(active.GetRemainingLeaseMs()) * time.Millisecond)
	if observedDeadline.Before(control.deadline) {
		control.deadline = observedDeadline
		control.armTimerLocked(epoch)
	}
	control.liveLocked(time.Now())
	return nil
}
func controlStatus(reply *a.StatusReply, snapshot domain.GenerationSnapshot) error {
	status := reply.GetStatus()
	if status == nil || bridge.ValidateContext(status.Context) != nil || !proto.Equal(status.Context.Identity, controlIdentity(snapshot)) || status.Context.NativeGeneration == nil {
		return ErrControl
	}
	if inactive := status.GetInactive(); inactive != nil {
		if inactive.Reason == nil || *inactive.Reason <= 0 || *inactive.Reason > a.RevocationReason_REVOCATION_REASON_GENERATION_EXHAUSTED {
			return ErrControl
		}
		return nil
	}
	if active := status.GetActive(); active != nil && active.Owner != nil && controlID(active.Owner.GetControllerSessionId()) && active.Owner.GetPlayerDirection() > 0 && active.RemainingLeaseMs != nil && active.GetRemainingLeaseMs() > 0 && active.GetRemainingLeaseMs() <= 30000 {
		return nil
	}
	return ErrControl
}
func controlIdentity(s domain.GenerationSnapshot) *c.Identity {
	return &c.Identity{ColonyId: proto.String(string(s.Colony)), LoadToken: proto.String(string(s.Load)), MapId: proto.Int32(int32(s.Map))}
}
func (control *Control) controlOwner(s domain.GenerationSnapshot) *a.Owner {
	return &a.Owner{ControllerSessionId: proto.String(string(control.namespace)), PlayerDirection: proto.Uint64(uint64(s.Direction))}
}
func controlID(value string) bool {
	return utf8.ValidString(value) && strings.TrimSpace(value) != "" && len(value) <= 256 && !strings.ContainsRune(value, 0)
}
