package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
	"sync"
	"time"
)

type ClockNative interface {
	Identity(context.Context) (*l.IdentityReply, bridge.Result, error)
	ReadClockStatus(context.Context, *c.Identity) (*k.StatusReply, bridge.Result, error)
	ReadClockAttempt(context.Context, *k.AttemptRequest) (*k.AttemptReply, bridge.Result, error)
}
type ClockWriter interface {
	OwnedPause(context.Context, *k.OwnedRequest) (*k.StatusReply, bridge.Result, error)
	Start(context.Context, *k.StartRequest) (*k.ControlReply, bridge.Result, error)
	Renew(context.Context, *k.RenewRequest, *k.Epoch) (*k.ControlReply, bridge.Result, error)
	ChangeSpeed(context.Context, *k.SpeedRequest, *k.Epoch) (*k.ControlReply, bridge.Result, error)
}
type ClockCoordinatorConfig struct{ CallTimeout, JournalTimeout time.Duration }
type ClockCoordinator struct {
	mu         sync.Mutex
	gate       chan struct{}
	journal    *store.Store
	native     ClockNative
	writer     ClockWriter
	leases     boundary.LeaseSource
	clock      executor.Clock
	config     ClockCoordinatorConfig
	authority  executor.Authority
	generation context.Context
	invalidate context.CancelFunc
	stopped    bool
}

func NewClockCoordinator(journal *store.Store, native ClockNative, writer ClockWriter, leases boundary.LeaseSource, clock executor.Clock, config ClockCoordinatorConfig) (*ClockCoordinator, error) {
	if journal == nil || native == nil || writer == nil || leases == nil || clock == nil || config.CallTimeout <= 0 || config.CallTimeout > time.Minute || config.JournalTimeout <= 0 || config.JournalTimeout > time.Minute {
		return nil, errors.New("invalid clock coordinator dependencies")
	}
	generation, cancel := context.WithCancel(context.Background())
	return &ClockCoordinator{gate: make(chan struct{}, 1), journal: journal, native: native, writer: writer, leases: leases, clock: clock, config: config, generation: generation, invalidate: cancel}, nil
}
func (q *ClockCoordinator) UpdateAuthority(value executor.Authority) error {
	if value.Enabled && (value.Snapshot.Validate() != nil || value.Snapshot.Native == 0 || value.Snapshot.Revision == 0) {
		return executor.ErrAuthority
	}
	q.mu.Lock()
	defer q.mu.Unlock()
	if q.stopped {
		if value.Enabled {
			return executor.ErrStopped
		}
		return nil
	}
	if value == q.authority {
		return nil
	}
	q.invalidate()
	q.generation, q.invalidate = context.WithCancel(context.Background())
	q.authority = value
	return nil
}
func (q *ClockCoordinator) enter(ctx context.Context) (context.Context, context.Context, func(), error) {
	q.mu.Lock()
	generation, stopped := q.generation, q.stopped
	q.mu.Unlock()
	if stopped {
		return nil, nil, nil, executor.ErrStopped
	}
	call, cancel := context.WithTimeout(ctx, q.config.CallTimeout)
	stop := context.AfterFunc(generation, cancel)
	done := func() { stop(); cancel() }
	select {
	case q.gate <- struct{}{}:
	case <-call.Done():
		done()
		return nil, nil, nil, call.Err()
	}
	if call.Err() != nil || generation.Err() != nil {
		<-q.gate
		done()
		return nil, nil, nil, errors.Join(call.Err(), executor.ErrAuthority)
	}
	return call, generation, func() { <-q.gate; done() }, nil
}
func (q *ClockCoordinator) guard(ctx, generation context.Context, snapshot domain.GenerationSnapshot) error {
	if ctx.Err() != nil {
		return ctx.Err()
	}
	if generation.Err() != nil {
		return executor.ErrAuthority
	}
	q.mu.Lock()
	current, stopped := q.authority, q.stopped
	q.mu.Unlock()
	if stopped {
		return executor.ErrStopped
	}
	if !current.Enabled || current.Snapshot != snapshot {
		return executor.ErrAuthority
	}
	return nil
}
func (q *ClockCoordinator) Stop(ctx context.Context) error {
	q.mu.Lock()
	q.stopped = true
	q.authority.Enabled = false
	q.invalidate()
	q.mu.Unlock()
	select {
	case q.gate <- struct{}{}:
		<-q.gate
		return ctx.Err()
	case <-ctx.Done():
		return ctx.Err()
	}
}
func clockCoordinatorExpectation(v store.ClockAttempt) bridge.ClockExpectation {
	s := v.Intent.Snapshot
	return bridge.ClockExpectation{Identity: boundary.Identity(s), Attempt: v.NativeAttempt, NativeGeneration: uint64(s.Native), Command: v.Intent.Command}
}
func clockCoordinatorTerminal(stage store.ClockEpochStage) bool {
	return stage == store.ClockEpochPaused || stage == store.ClockEpochRetired || stage == store.ClockEpochSuperseded
}
func clockCoordinatorEpoch(s *k.Status) *k.Epoch {
	if s.GetRunning() != nil {
		return s.GetRunning().Epoch
	}
	if s.GetStopping() != nil {
		return s.GetStopping().Epoch
	}
	return s.GetStopped().GetEpoch()
}
func clockCoordinatorSameEpoch(a, b *k.Epoch) bool {
	return a != nil && b != nil && proto.Equal(a.Owner, b.Owner) && proto.Equal(a.Origin, b.Origin) && proto.Equal(a.Policy, b.Policy) && a.GetStartTick() == b.GetStartTick() && a.GetTickDeadline() == b.GetTickDeadline()
}

// inspect checks the journal and the live clock status before a dispatch.
// observed, when non-nil, is a validated status the caller read under the
// same serialized scope moments ago (ClockWindowRequest.Status); it stands in
// for the native clock_read_status read.
func (q *ClockCoordinator) inspect(ctx context.Context, v store.ClockAttempt, observed *k.Status) error {
	epochs, err := q.journal.LoadClockEpochs(ctx, 4096)
	if err != nil {
		return err
	}
	command := v.Intent.Command
	var original *k.Epoch
	if command.Start != nil {
		attempts, err := q.journal.LoadClockAttempts(ctx, 4096)
		if err != nil {
			return err
		}
		for _, other := range attempts {
			if other.SupersededAt == nil && other.Intent.RequestID != v.Intent.RequestID && other.Intent.Command.Start != nil && (other.Phase == store.ClockDispatched || other.Phase == store.ClockUncertain) {
				return executor.ErrHeld
			}
		}
		for _, epoch := range epochs {
			if !clockCoordinatorTerminal(epoch.Stage) {
				return executor.ErrHeld
			}
		}
	} else {
		if command.Renew != nil {
			original = command.Renew.Original
		} else {
			original = command.Speed.Original
		}
		found := false
		for _, epoch := range epochs {
			if clockCoordinatorSameEpoch(epoch.Epoch, original) && epoch.Stage == store.ClockEpochRequired {
				admitted, err := q.journal.LookupClockAttempt(ctx, epoch.StartRequestID)
				if err != nil {
					return err
				}
				if admitted.Intent.Snapshot != v.Intent.Snapshot {
					return executor.ErrHeld
				}
				found = true
			}
		}
		if !found {
			return executor.ErrHeld
		}
	}
	identity := boundary.Identity(v.Intent.Snapshot)
	status := observed
	if status == nil {
		reply, _, err := q.native.ReadClockStatus(ctx, identity)
		if err != nil {
			return err
		}
		status = reply.GetStatus()
	}
	if err = bridge.ValidateClockStatus(status, identity); err != nil {
		return err
	}
	if err = ctx.Err(); err != nil {
		return err
	}
	if status.GetUnavailable() != nil {
		return executor.ErrHeld
	}
	if status.Context.NativeGeneration == nil || status.Context.GetNativeGeneration() != uint64(v.Intent.Snapshot.Native) {
		return executor.ErrHeld
	}
	if command.Start != nil {
		if status.GetRunning() != nil || status.GetStopping() != nil {
			return executor.ErrHeld
		}
		if window := v.Intent.Window; window != nil {
			if status.ActualPaused == nil || !status.GetActualPaused() || status.NativeTickBoundary == nil || !status.GetNativeTickBoundary() || status.DurableEvents == nil || (!status.GetDurableEvents() && status.GetNeverStarted() == nil) || status.Context.GetTick() != int64(window.Tick) || status.NewestCursor == nil || status.GetNewestCursor() != window.CapturedCursor {
				return executor.ErrHeld
			}
		}
		return nil
	}
	actual := clockCoordinatorEpoch(status)
	if status.GetRunning() == nil || !clockCoordinatorSameEpoch(actual, original) || actual.GetRequestedSpeed() != original.GetRequestedSpeed() || actual.GetLastTick() < original.GetLastTick() {
		return executor.ErrHeld
	}
	return nil
}
func (q *ClockCoordinator) uncertain(v store.ClockAttempt, cause error) (store.ClockAttempt, error) {
	ctx, cancel := context.WithTimeout(context.Background(), q.config.JournalTimeout)
	defer cancel()
	saved, err := q.journal.MarkClockUncertain(ctx, v.Intent.RequestID)
	if err != nil {
		return v, errors.Join(cause, err)
	}
	return saved, cause
}
func (q *ClockCoordinator) record(v store.ClockAttempt, reply *k.ControlReply, cause error) (store.ClockAttempt, error) {
	if err := bridge.ValidateClockControlReply(reply, clockCoordinatorExpectation(v)); err != nil {
		return q.uncertain(v, errors.Join(cause, err))
	}
	ctx, cancel := context.WithTimeout(context.Background(), q.config.JournalTimeout)
	defer cancel()
	saved, err := q.journal.RecordClockReply(ctx, v.Intent.RequestID, reply)
	if err != nil {
		return v, errors.Join(cause, err)
	}
	return saved, cause
}
func (q *ClockCoordinator) Command(ctx context.Context, intent store.ClockIntent) (store.ClockAttempt, error) {
	if intent.Window != nil {
		return store.ClockAttempt{}, executor.ErrHeld
	}
	return q.command(ctx, intent, nil, nil)
}

// command runs one serialized clock command. check, when set, re-evaluates
// the caller's admission at each gate; observed, when set, is the status the
// pre-dispatch inspection checks instead of reading it natively (see
// ClockWindowRequest.Status).
func (q *ClockCoordinator) command(ctx context.Context, intent store.ClockIntent, check func(store.ClockIntent) error, observed func() *k.Status) (store.ClockAttempt, error) {
	call, generation, done, err := q.enter(ctx)
	if err != nil {
		return store.ClockAttempt{}, err
	}
	defer done()
	if err = q.guard(call, generation, intent.Snapshot); err != nil {
		return store.ClockAttempt{}, err
	}
	if check != nil {
		if err = check(intent); err != nil {
			return store.ClockAttempt{}, err
		}
	}
	v, _, err := q.journal.PrepareClock(call, intent)
	if err != nil {
		return v, err
	}
	if v.Phase != store.ClockPrepared {
		return v, nil
	}
	var status *k.Status
	if observed != nil {
		status = observed()
	}
	if err = q.inspect(call, v, status); err != nil {
		return v, err
	}
	if err = q.guard(call, generation, v.Intent.Snapshot); err != nil {
		return v, err
	}
	lease, err := q.leases.Lease(v.Intent.Snapshot)
	if err != nil {
		return v, err
	}
	if !boundary.ValidID(lease) {
		return v, executor.ErrAuthority
	}
	if err = q.guard(call, generation, v.Intent.Snapshot); err != nil {
		return v, err
	}
	if check != nil {
		if err = check(v.Intent); err != nil {
			return v, err
		}
	}
	v, err = q.journal.DispatchClock(call, v.Intent.RequestID)
	if err != nil {
		return v, err
	}
	if err = q.guard(call, generation, v.Intent.Snapshot); err != nil {
		return q.uncertain(v, err)
	}
	if check != nil {
		if err = check(v.Intent); err != nil {
			return q.uncertain(v, err)
		}
	}
	e := clockCoordinatorExpectation(v)
	if err = q.guard(call, generation, v.Intent.Snapshot); err != nil {
		return q.uncertain(v, err)
	}
	pre := &a.WritePrecondition{Identity: e.Identity, Attempt: e.Attempt, ExpectedGeneration: proto.Uint64(e.NativeGeneration)}
	var reply *k.ControlReply
	switch command := v.Intent.Command; {
	case command.Start != nil:
		s := command.Start
		reply, _, err = q.writer.Start(call, &k.StartRequest{Authority: pre, Speed: s.Speed.Enum(), Policy: s.Policy, LeaseMs: proto.Uint32(s.LeaseMS), MaxTicks: proto.Uint32(s.MaxTicks), TestAcceleration: proto.Bool(s.TestAcceleration)})
	case command.Renew != nil:
		r := command.Renew
		reply, _, err = q.writer.Renew(call, &k.RenewRequest{Epoch: &k.OwnedRequest{Identity: e.Identity, Owner: r.Original.Owner}, Authority: pre, LeaseMs: proto.Uint32(r.LeaseMS)}, r.Original)
	case command.Speed != nil:
		s := command.Speed
		reply, _, err = q.writer.ChangeSpeed(call, &k.SpeedRequest{Epoch: &k.OwnedRequest{Identity: e.Identity, Owner: s.Original.Owner}, Authority: pre, Speed: s.Speed.Enum()}, s.Original)
	}
	return q.record(v, reply, errors.Join(err, call.Err()))
}

// Reconcile never replays a command or turns a read refusal into no-effect proof.
func (q *ClockCoordinator) Reconcile(ctx context.Context, id string) (store.ClockAttempt, error) {
	call, _, done, err := q.enter(ctx)
	if err != nil {
		return store.ClockAttempt{}, err
	}
	defer done()
	return q.reconcileLocked(call, id)
}

func (q *ClockCoordinator) reconcileLocked(call context.Context, id string) (store.ClockAttempt, error) {
	v, err := q.journal.LookupClockAttempt(call, id)
	if err != nil {
		return v, err
	}
	if v.SupersededAt != nil || v.Phase != store.ClockDispatched && v.Phase != store.ClockUncertain {
		return v, nil
	}
	identity := boundary.Identity(v.Intent.Snapshot)
	reply, _, err := q.native.ReadClockAttempt(call, &k.AttemptRequest{Identity: identity, Attempt: v.NativeAttempt})
	if err != nil {
		return q.uncertain(v, errors.Join(err, call.Err(), executor.ErrHeld))
	}
	if reply.GetReceipt() == nil {
		if reply.GetUnknown() != nil && v.Intent.Command.Start != nil {
			if absent, err := q.startAbsent(call, v, identity); err != nil {
				return q.uncertain(v, errors.Join(err, call.Err(), executor.ErrHeld))
			} else if absent {
				return q.record(v, &k.ControlReply{Outcome: &k.ControlReply_Failure{Failure: &c.Failure{
					Code: c.FailureCode_FAILURE_CODE_NOT_FOUND.Enum(), Detail: proto.String("start never admitted: native ledger unknown and no newer clock epoch")}}}, call.Err())
			}
		}
		return q.uncertain(v, errors.Join(call.Err(), executor.ErrHeld))
	}
	return q.record(v, &k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: reply.GetReceipt()}}, call.Err())
}

// startAbsent turns a native-unknown start into positive no-effect evidence. The
// native ledger is unsaved per load, so under the same load token an unknown key
// was never admitted; a start that had taken effect would have produced a newer
// epoch owned by this session, and the clock status always carries the latest
// epoch. Both must agree before the journal records the refusal.
func (q *ClockCoordinator) startAbsent(ctx context.Context, v store.ClockAttempt, identity *c.Identity) (bool, error) {
	reply, _, err := q.native.ReadClockStatus(ctx, identity)
	if err != nil {
		return false, err
	}
	status := reply.GetStatus()
	if err = bridge.ValidateClockStatus(status, identity); err != nil {
		return false, err
	}
	if status.GetUnavailable() != nil || !proto.Equal(status.Context.Identity, identity) {
		return false, nil
	}
	actual := clockCoordinatorEpoch(status)
	if actual == nil {
		return status.GetNeverStarted() != nil, nil
	}
	if actual.Owner.GetControllerSessionId() != v.NativeAttempt.GetControllerSessionId() {
		return true, nil
	}
	epochs, err := q.journal.LoadClockEpochs(ctx, 4096)
	if err != nil {
		return false, err
	}
	for _, known := range epochs {
		if known.StartRequestID != v.Intent.RequestID && known.Epoch != nil && known.Epoch.Owner.GetEpoch() >= actual.Owner.GetEpoch() {
			return true, nil
		}
	}
	return false, nil
}
