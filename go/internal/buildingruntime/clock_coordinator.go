package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"sync"
	"time"
)

type ClockNative interface {
	ReadClockStatus(context.Context, *c.Identity) (*k.StatusReply, bridge.Result, error)
	ReadClockAttempt(context.Context, *k.AttemptRequest) (*k.AttemptReply, bridge.Result, error)
}
type ClockWriter interface {
	Start(context.Context, *k.StartRequest, *a.Owner) (*k.ControlReply, bridge.Result, error)
	Renew(context.Context, *k.RenewRequest, *k.Epoch, *a.Owner) (*k.ControlReply, bridge.Result, error)
	ChangeSpeed(context.Context, *k.SpeedRequest, *k.Epoch, *a.Owner) (*k.ControlReply, bridge.Result, error)
}
type ClockCoordinatorConfig struct{ CallTimeout, JournalTimeout time.Duration }
type ClockCoordinator struct {
	mu         sync.Mutex
	gate       chan struct{}
	journal    *store.Store
	native     ClockNative
	writer     ClockWriter
	leases     LeaseSource
	config     ClockCoordinatorConfig
	authority  executor.Authority
	generation context.Context
	invalidate context.CancelFunc
	stopped    bool
}

func NewClockCoordinator(journal *store.Store, native ClockNative, writer ClockWriter, leases LeaseSource, config ClockCoordinatorConfig) (*ClockCoordinator, error) {
	if journal == nil || native == nil || writer == nil || leases == nil || config.CallTimeout <= 0 || config.CallTimeout > time.Minute || config.JournalTimeout <= 0 || config.JournalTimeout > time.Minute {
		return nil, errors.New("invalid clock coordinator dependencies")
	}
	generation, cancel := context.WithCancel(context.Background())
	return &ClockCoordinator{gate: make(chan struct{}, 1), journal: journal, native: native, writer: writer, leases: leases, config: config, generation: generation, invalidate: cancel}, nil
}
func (q *ClockCoordinator) UpdateAuthority(value executor.Authority) error {
	if value.Enabled && (value.Snapshot.Validate() != nil || value.Snapshot.Native == 0 || value.Snapshot.Direction == 0 || value.Snapshot.Revision == 0) {
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
	return bridge.ClockExpectation{Identity: boundaryIdentity(s), Attempt: v.NativeAttempt, Owner: &a.Owner{ControllerSessionId: proto.String(v.NativeAttempt.GetControllerSessionId()), PlayerDirection: proto.Uint64(uint64(s.Direction))}, NativeGeneration: uint64(s.Native), Command: v.Intent.Command}
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
func (q *ClockCoordinator) inspect(ctx context.Context, v store.ClockAttempt) error {
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
			if other.Intent.RequestID != v.Intent.RequestID && other.Intent.Command.Start != nil && (other.Phase == store.ClockDispatched || other.Phase == store.ClockUncertain) {
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
	identity := boundaryIdentity(v.Intent.Snapshot)
	reply, _, err := q.native.ReadClockStatus(ctx, identity)
	if err != nil {
		return err
	}
	status := reply.GetStatus()
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
	call, generation, done, err := q.enter(ctx)
	if err != nil {
		return store.ClockAttempt{}, err
	}
	defer done()
	if err = q.guard(call, generation, intent.Snapshot); err != nil {
		return store.ClockAttempt{}, err
	}
	v, _, err := q.journal.PrepareClock(call, intent)
	if err != nil {
		return v, err
	}
	if v.Phase != store.ClockPrepared {
		return v, nil
	}
	if err = q.inspect(call, v); err != nil {
		return v, err
	}
	if err = q.guard(call, generation, v.Intent.Snapshot); err != nil {
		return v, err
	}
	lease, err := q.leases.Lease(v.Intent.Snapshot)
	if err != nil {
		return v, err
	}
	if !boundaryID(lease) {
		return v, executor.ErrAuthority
	}
	if err = q.guard(call, generation, v.Intent.Snapshot); err != nil {
		return v, err
	}
	v, err = q.journal.DispatchClock(call, v.Intent.RequestID)
	if err != nil {
		return v, err
	}
	if err = q.guard(call, generation, v.Intent.Snapshot); err != nil {
		return q.uncertain(v, err)
	}
	e := clockCoordinatorExpectation(v)
	pre := &a.WritePrecondition{Identity: e.Identity, Attempt: e.Attempt, ExpectedGeneration: proto.Uint64(e.NativeGeneration), LeaseId: proto.String(lease)}
	var reply *k.ControlReply
	switch command := v.Intent.Command; {
	case command.Start != nil:
		s := command.Start
		reply, _, err = q.writer.Start(call, &k.StartRequest{Authority: pre, Speed: s.Speed.Enum(), Policy: s.Policy, LeaseMs: proto.Uint32(s.LeaseMS), MaxTicks: proto.Uint32(s.MaxTicks)}, e.Owner)
	case command.Renew != nil:
		r := command.Renew
		reply, _, err = q.writer.Renew(call, &k.RenewRequest{Epoch: &k.OwnedRequest{Identity: e.Identity, Owner: r.Original.Owner}, Authority: pre, LeaseMs: proto.Uint32(r.LeaseMS)}, r.Original, e.Owner)
	case command.Speed != nil:
		s := command.Speed
		reply, _, err = q.writer.ChangeSpeed(call, &k.SpeedRequest{Epoch: &k.OwnedRequest{Identity: e.Identity, Owner: s.Original.Owner}, Authority: pre, Speed: s.Speed.Enum()}, s.Original, e.Owner)
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
	v, err := q.journal.LookupClockAttempt(call, id)
	if err != nil {
		return v, err
	}
	if v.Phase != store.ClockDispatched && v.Phase != store.ClockUncertain {
		return v, nil
	}
	reply, _, err := q.native.ReadClockAttempt(call, &k.AttemptRequest{Identity: boundaryIdentity(v.Intent.Snapshot), Attempt: v.NativeAttempt})
	if err != nil || reply.GetReceipt() == nil {
		return q.uncertain(v, errors.Join(err, call.Err(), executor.ErrHeld))
	}
	return q.record(v, &k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: reply.GetReceipt()}}, call.Err())
}
