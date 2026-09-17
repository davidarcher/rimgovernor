package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

type clockCoreFake struct {
	status                 *k.Status
	identityError          error
	pauses                 int
	pause                  func(context.Context) error
	receipt                *k.ControlReceipt
	writes, reads, lookups int
	write                  func(context.Context)
	lost                   bool
	lookupFailure          bool
}

func (f *clockCoreFake) ReadClockStatus(ctx context.Context, id *c.Identity) (*k.StatusReply, bridge.Result, error) {
	f.reads++
	return &k.StatusReply{Outcome: &k.StatusReply_Status{Status: proto.Clone(f.status).(*k.Status)}}, bridge.Result{}, ctx.Err()
}
func (f *clockCoreFake) ReadClockAttempt(ctx context.Context, r *k.AttemptRequest) (*k.AttemptReply, bridge.Result, error) {
	f.lookups++
	if f.lookupFailure {
		return &k.AttemptReply{Outcome: &k.AttemptReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_IDENTITY.Enum()}}}, bridge.Result{}, nil
	}
	if f.receipt == nil {
		return &k.AttemptReply{Outcome: &k.AttemptReply_Unknown{Unknown: &k.AttemptUnknown{}}}, bridge.Result{}, nil
	}
	return &k.AttemptReply{Outcome: &k.AttemptReply_Receipt{Receipt: proto.Clone(f.receipt).(*k.ControlReceipt)}}, bridge.Result{}, nil
}
func (f *clockCoreFake) Start(ctx context.Context, r *k.StartRequest) (*k.ControlReply, bridge.Result, error) {
	f.writes++
	if f.write != nil {
		f.write(ctx)
	}
	e := &k.Epoch{Owner: &k.EpochOwner{ControllerSessionId: proto.String(r.Authority.Attempt.GetControllerSessionId()), Epoch: proto.Int64(1)}, Origin: proto.Clone(f.status.Context).(*c.ObservationContext), RequestedSpeed: r.Speed, Policy: r.Policy, StartTick: proto.Int64(12), TickDeadline: proto.Int64(12 + int64(r.GetMaxTicks())), LastTick: proto.Int64(12), LeaseRemainingMs: proto.Uint32(r.GetLeaseMs())}
	f.status.State = &k.Status_Running{Running: &k.Running{Epoch: e}}
	f.receipt = &k.ControlReceipt{Attempt: proto.Clone(r.Authority.Attempt).(*c.AttemptKey), AdmittedContext: proto.Clone(f.status.Context).(*c.ObservationContext), Outcome: &k.ControlReceipt_Applied{Applied: &k.AppliedControl{Status: proto.Clone(f.status).(*k.Status)}}}
	if f.lost {
		return nil, bridge.Result{}, context.DeadlineExceeded
	}
	return &k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: f.receipt}}, bridge.Result{}, nil
}
func (f *clockCoreFake) Renew(ctx context.Context, r *k.RenewRequest, original *k.Epoch) (*k.ControlReply, bridge.Result, error) {
	return f.change(r.Authority)
}
func (f *clockCoreFake) ChangeSpeed(ctx context.Context, r *k.SpeedRequest, original *k.Epoch) (*k.ControlReply, bridge.Result, error) {
	f.status.GetRunning().Epoch.RequestedSpeed = r.Speed
	return f.change(r.Authority)
}
func (f *clockCoreFake) change(pre *a.WritePrecondition) (*k.ControlReply, bridge.Result, error) {
	f.writes++
	r := &k.ControlReceipt{Attempt: pre.Attempt, AdmittedContext: f.status.Context, Outcome: &k.ControlReceipt_Applied{Applied: &k.AppliedControl{Status: proto.Clone(f.status).(*k.Status)}}}
	return &k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: r}}, bridge.Result{}, nil
}

type clockCoreLease struct {
	get func(domain.GenerationSnapshot) (string, error)
}

func (l clockCoreLease) Lease(s domain.GenerationSnapshot) (string, error) { return l.get(s) }
func clockCoreFixture(t *testing.T) (*ClockCoordinator, *store.Store, *clockCoreFake, store.ClockIntent) {
	t.Helper()
	db, err := store.Open(context.Background(), storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 7}
	policy := &k.WatchPolicy{Mode: k.WatchMode_WATCH_MODE_COLONY.Enum(), HealthDropFraction: proto.Float32(.1), MinHealthFraction: proto.Float32(.2), HostileWithin: proto.Float32(20), InjuryStopCooldownMs: proto.Uint32(0)}
	intent := store.ClockIntent{RequestID: clockTestNextID(t, db), Snapshot: snapshot, Command: bridge.ClockCommand{Start: &bridge.ClockStart{Speed: k.Speed_SPEED_NORMAL, Policy: policy, LeaseMS: 1000, MaxTicks: 100}}}
	fake := &clockCoreFake{status: &k.Status{Context: &c.ObservationContext{Identity: boundary.Identity(snapshot), Tick: proto.Int64(12), NativeGeneration: proto.Uint64(7)}, State: &k.Status_NeverStarted{NeverStarted: &k.NeverStarted{}}, ActualPaused: proto.Bool(true), ObservedSpeed: k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum(), NativeTickBoundary: proto.Bool(true), DurableEvents: proto.Bool(false), NewestCursor: proto.Int64(0), EvidenceCompleteness: &c.PageInfo{Complete: proto.Bool(true)}}}
	q, err := NewClockCoordinator(db, fake, fake, clockCoreLease{func(domain.GenerationSnapshot) (string, error) { return "lease", nil }}, boundary.FixedClock{}, ClockCoordinatorConfig{CallTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { q.Stop(context.Background()) })
	return q, db, fake, intent
}
func TestClockCoordinatorDisabledAndExactReplay(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCoreFixture(t)
	if _, err := q.Command(context.Background(), intent); !errors.Is(err, executor.ErrAuthority) {
		t.Fatal(err)
	}
	if _, err := db.LookupClockAttempt(context.Background(), intent.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	if err := q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true}); err != nil {
		t.Fatal(err)
	}
	result, err := q.Command(context.Background(), intent)
	if err != nil || result.Phase != store.ClockApplied {
		t.Fatal(result.Phase, err)
	}
	if _, err = q.Command(context.Background(), intent); err != nil || f.writes != 1 {
		t.Fatal(f.writes, err)
	}
	if _, err = q.Reconcile(context.Background(), intent.RequestID); err != nil || f.lookups != 0 {
		t.Fatal(err)
	}
	next := intent
	next.RequestID = clockTestNextID(t, db)
	if _, err = q.Command(context.Background(), next); !errors.Is(err, executor.ErrHeld) || f.writes != 1 {
		t.Fatal(err, f.writes)
	}
}
func TestClockCoordinatorLostStartRecoveryDisabled(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCoreFixture(t)
	_ = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true})
	f.lost = true
	result, err := q.Command(context.Background(), intent)
	if err == nil || result.Phase != store.ClockUncertain {
		t.Fatal(result.Phase, err)
	}
	restarted, err := NewClockCoordinator(db, f, f, q.leases, q.clock, q.config)
	if err != nil {
		t.Fatal(err)
	}
	defer restarted.Stop(context.Background())
	f.lookupFailure = true
	result, err = restarted.Reconcile(context.Background(), intent.RequestID)
	if err == nil || result.Phase != store.ClockUncertain {
		t.Fatal(result.Phase, err)
	}
	f.lookupFailure = false
	result, err = restarted.Reconcile(context.Background(), intent.RequestID)
	if err != nil || result.Phase != store.ClockApplied || f.writes != 1 {
		t.Fatal(result.Phase, err, f.writes)
	}
	epochs, err := db.LoadClockEpochs(context.Background(), 4096)
	if err != nil || len(epochs) != 1 {
		t.Fatal(epochs, err)
	}
}
func TestClockCoordinatorUnknownDoesNotAdoptStatus(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCoreFixture(t)
	_ = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true})
	f.lost = true
	_, _ = q.Command(context.Background(), intent)
	f.receipt = nil
	v, err := q.Reconcile(context.Background(), intent.RequestID)
	if err == nil || v.Phase != store.ClockUncertain {
		t.Fatal(v.Phase, err)
	}
	epochs, _ := db.LoadClockEpochs(context.Background(), 4096)
	if len(epochs) != 0 {
		t.Fatal("adopted status")
	}
	other := intent
	other.RequestID = clockTestNextID(t, db)
	if _, err = q.Command(context.Background(), other); !errors.Is(err, executor.ErrHeld) || f.writes != 1 {
		t.Fatal(err)
	}
}
func TestClockCoordinatorUnknownStartWithoutEpochIsRefused(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCoreFixture(t)
	_ = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true})
	f.lost = true
	_, _ = q.Command(context.Background(), intent)
	// The native never admitted the start: its ledger is unknown and the clock
	// still reports never started, so the journal records a refusal and the
	// next start is no longer held behind it.
	f.receipt = nil
	f.status.State = &k.Status_NeverStarted{NeverStarted: &k.NeverStarted{}}
	v, err := q.Reconcile(context.Background(), intent.RequestID)
	if err != nil || v.Phase != store.ClockRefused {
		t.Fatal(v.Phase, err)
	}
	if epochs, _ := db.LoadClockEpochs(context.Background(), 4096); len(epochs) != 0 {
		t.Fatal("adopted status")
	}
	f.lost = false
	other := intent
	other.RequestID = clockTestNextID(t, db)
	if v, err = q.Command(context.Background(), other); err != nil || v.Phase != store.ClockApplied || f.writes != 2 {
		t.Fatal(v.Phase, err, f.writes)
	}
}
func TestClockCoordinatorUnknownStartBehindKnownEpochIsRefused(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCoreFixture(t)
	_ = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true})
	if _, err := q.Command(context.Background(), intent); err != nil {
		t.Fatal(err)
	}
	epochs, _ := db.LoadClockEpochs(context.Background(), 4096)
	if len(epochs) != 1 {
		t.Fatal(epochs)
	}
	// The first epoch ran out and stopped; the journal retires its obligation.
	f.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epochs[0].Epoch, Reason: k.StopReason_STOP_REASON_TICK_BUDGET.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(false), StoppedAtUnixMs: proto.Int64(1)}}
	if err := q.Cleanup(context.Background()); err != nil {
		t.Fatal(err)
	}
	// A second start is lost in transport before admission: the status still
	// carries the first epoch, so the lost start had no effect.
	f.lost = true
	second := intent
	second.RequestID = clockTestNextID(t, db)
	if v, err := q.Command(context.Background(), second); err == nil || v.Phase != store.ClockUncertain {
		t.Fatal(v.Phase, err)
	}
	f.receipt = nil
	f.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epochs[0].Epoch, Reason: k.StopReason_STOP_REASON_TICK_BUDGET.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(false), StoppedAtUnixMs: proto.Int64(1)}}
	v, err := q.Reconcile(context.Background(), second.RequestID)
	if err != nil || v.Phase != store.ClockRefused {
		t.Fatal(v.Phase, err)
	}
	if epochs, _ = db.LoadClockEpochs(context.Background(), 4096); len(epochs) != 1 {
		t.Fatal(epochs)
	}
}
func TestClockCoordinatorRenewRequiresOriginalProofAndFreshSpeed(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCoreFixture(t)
	_ = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true})
	_, err := q.Command(context.Background(), intent)
	if err != nil {
		t.Fatal(err)
	}
	epochs, _ := db.LoadClockEpochs(context.Background(), 4096)
	renew := store.ClockIntent{RequestID: clockTestNextID(t, db), Snapshot: intent.Snapshot, Command: bridge.ClockCommand{Renew: &bridge.ClockRenew{Original: epochs[0].Epoch, LeaseMS: 1000}}}
	f.status.GetRunning().Epoch.LastTick = proto.Int64(13)
	f.status.Context.Tick = proto.Int64(13)
	v, err := q.Command(context.Background(), renew)
	if err != nil || v.Phase != store.ClockApplied {
		t.Fatal(v.Phase, err)
	}
	renew.RequestID = clockTestNextID(t, db)
	f.status.GetRunning().Epoch.RequestedSpeed = k.Speed_SPEED_FAST.Enum()
	if _, err = q.Command(context.Background(), renew); !errors.Is(err, executor.ErrHeld) || f.writes != 2 {
		t.Fatal(err, f.writes)
	}
}
func TestClockCoordinatorInvalidationBeforeWrite(t *testing.T) {
	t.Parallel()
	q, _, f, intent := clockCoreFixture(t)
	_ = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true})
	q.leases = clockCoreLease{func(domain.GenerationSnapshot) (string, error) {
		_ = q.UpdateAuthority(executor.Authority{})
		return "lease", nil
	}}
	if _, err := q.Command(context.Background(), intent); err == nil || f.writes != 0 {
		t.Fatal(err, f.writes)
	}
}
func TestClockCoordinatorStopJoinsUncertainWrite(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCoreFixture(t)
	_ = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true})
	entered := make(chan struct{})
	release := make(chan struct{})
	f.lost = true
	f.write = func(ctx context.Context) { close(entered); <-release }
	finished := make(chan error, 1)
	go func() { _, err := q.Command(context.Background(), intent); finished <- err }()
	<-entered
	ctx, cancel := context.WithTimeout(context.Background(), time.Millisecond)
	defer cancel()
	if err := q.Stop(ctx); err == nil {
		t.Fatal("did not retain pending writer")
	}
	close(release)
	if err := <-finished; err == nil {
		t.Fatal("lost reply accepted")
	}
	if err := q.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
	v, err := db.LookupClockAttempt(context.Background(), intent.RequestID)
	if err != nil || v.Phase != store.ClockUncertain {
		t.Fatal(v.Phase, err)
	}
}

func TestClockCoordinatorRejectsChangedPlanForOwnedEpoch(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCoreFixture(t)
	_ = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true})
	if _, err := q.Command(context.Background(), intent); err != nil {
		t.Fatal(err)
	}
	epochs, _ := db.LoadClockEpochs(context.Background(), 4096)
	intent.Snapshot.Plan = "replacement-plan"
	_ = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true})
	renew := store.ClockIntent{RequestID: clockTestNextID(t, db), Snapshot: intent.Snapshot, Command: bridge.ClockCommand{Renew: &bridge.ClockRenew{Original: epochs[0].Epoch, LeaseMS: 1000}}}
	if _, err := q.Command(context.Background(), renew); !errors.Is(err, executor.ErrHeld) || f.writes != 1 {
		t.Fatal(err, f.writes)
	}
}

func clockTestNextID(t *testing.T, db *store.Store) string {
	t.Helper()
	sequence, err := db.ReadClockSequence(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	id, err := sequence.NextRequestID()
	if err != nil {
		t.Fatal(err)
	}
	return id
}
