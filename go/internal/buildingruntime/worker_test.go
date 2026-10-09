package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"sort"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

type workerFake struct {
	*playerFakeSession
	cleanup                          func(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error)
	run                              func(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error)
	observe                          func(context.Context, domain.GenerationSnapshot) error
	renew                            func(context.Context) error
	batch                            func(context.Context, domain.PlanID, []domain.ActionID) ([]executor.BatchItem, error)
	runs, observes, renews, cleanups atomic.Int32
	batches                          atomic.Int32
}

func (f *workerFake) runOne(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
	f.runs.Add(1)
	return f.run(ctx, p, a)
}

// RunBatch is the batch of Runs unless a test sets batch.
func (f *workerFake) RunBatch(ctx context.Context, p domain.PlanID, ids []domain.ActionID) ([]executor.BatchItem, error) {
	f.batches.Add(1)
	if f.batch != nil {
		return f.batch(ctx, p, ids)
	}
	out := make([]executor.BatchItem, len(ids))
	for i, a := range ids {
		out[i].Action = a
		out[i].Result, out[i].Err = f.runOne(ctx, p, a)
	}
	return out, nil
}
func (f *workerFake) ObserveTarget(ctx context.Context, s domain.GenerationSnapshot) error {
	f.observes.Add(1)
	if f.observe != nil {
		return f.observe(ctx, s)
	}
	f.mu.Lock()
	f.state = ControlState{Snapshot: s, ObservationKnown: true}
	f.mu.Unlock()
	return ctx.Err()
}
func (f *workerFake) Renew(ctx context.Context) error {
	f.renews.Add(1)
	if f.renew != nil {
		return f.renew(ctx)
	}
	return ctx.Err()
}
func workerFixture(t *testing.T) (*Worker, *workerFake, *store.Store) {
	t.Helper()
	p, db, base, _ := playerFixture(t)
	f := &workerFake{playerFakeSession: base}
	p.session = f
	config := WorkerConfig{StepInterval: 10 * time.Millisecond, MaxBackoff: time.Second, StepTimeout: 5 * time.Second, RenewInterval: 10 * time.Millisecond, RenewTimeout: time.Millisecond * 100}
	w := &Worker{player: p, session: f, config: config, waits: make(map[domain.ActionID]workerWait)}
	return w, f, db
}
func workerPending(t *testing.T, w *Worker, id string, unresolved bool) domain.ProgressView {
	t.Helper()
	q := playerSubmission()
	q.RequestID = id
	submission, _, err := w.player.journal.SubmitBuilding(context.Background(), q)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: q.World.Colony, Load: q.World.Load, Map: q.World.Map, Plan: submission.Plan, Revision: 1, Native: 1}
	if unresolved {
		_, err = w.player.journal.Prepare(context.Background(), submission.Plan, submission.Action, snapshot, 1)
		if err != nil {
			t.Fatal(err)
		}
		_, err = w.player.journal.Dispatch(context.Background(), submission.Plan, submission.Action, snapshot, 1)
		if err != nil {
			t.Fatal(err)
		}
	}
	plan, err := w.player.journal.LoadPlan(context.Background(), submission.Plan)
	if err != nil {
		t.Fatal(err)
	}
	v := plan.Progress[0].View()
	if !unresolved {
		v.Snapshot = snapshot
	}
	return v
}
func TestWorkerDisabledFairScanAndPausedBackoff(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	w, f, db := workerFixture(t)
	w.config.MaxDispatches = 1
	first := workerPending(t, w, "one", true)
	second := workerPending(t, w, "two", true)
	seen := map[domain.ActionID]int{}
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		seen[a]++
		plan, err := db.LoadPlan(ctx, p)
		return executor.Result{Progress: plan.Progress[0]}, err
	}
	now := time.Now()
	for i := 0; i < 2; i++ {
		if err := w.step(context.Background(), now); err != nil {
			t.Fatal(err)
		}
	}
	if seen[first.Action] != 1 || seen[second.Action] != 1 || f.acquires.Load() != 0 {
		t.Fatal(seen)
	}
	for i := 0; i < 100; i++ {
		if err := w.step(context.Background(), now); err != nil {
			t.Fatal(err)
		}
	}
	if f.runs.Load() != 2 {
		t.Fatal("paused polling", f.runs.Load())
	}
	if err := w.step(context.Background(), now.Add(w.config.StepInterval)); err != nil {
		t.Fatal(err)
	}
	if f.runs.Load() != 3 {
		t.Fatal("missing fair next step")
	}
}

// A selected action that leaves the candidate set (here: observed complete
// after its run) must not send the rotation back to the catalog's head;
// the next step continues past its position.
func TestWorkerRotationResumesPastADepartedCursor(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	w.config.MaxDispatches = 1
	// Catalog order is by plan id (a digest), so sort the three to know it.
	views := []domain.ProgressView{workerPending(t, w, "one", true), workerPending(t, w, "two", true), workerPending(t, w, "three", true)}
	sort.Slice(views, func(i, j int) bool { return views[i].Plan < views[j].Plan })
	first, second, third := views[0], views[1], views[2]
	var order []domain.ActionID
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		order = append(order, a)
		plan, err := db.LoadPlan(ctx, p)
		return executor.Result{Progress: plan.Progress[0]}, err
	}
	now := time.Now()
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(order) != 2 || order[0] != first.Action || order[1] != second.Action {
		t.Fatal("catalog rotation", order)
	}
	// The second completes and leaves the candidate set, and a wake has
	// dropped every backoff (takeWake): the next step must still reach the
	// third rather than restart at the first.
	w.waits = map[domain.ActionID]workerWait{}
	if _, err := db.RecordReceipt(context.Background(), second.Plan, second.Action, 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(order) != 3 || order[2] != third.Action {
		t.Fatal("rotation restarted at the catalog head", order)
	}
}

func TestWorkerActualPermissionAndWrongWorld(t *testing.T) {
	t.Parallel()
	w, f, _ := workerFixture(t)
	v := workerPending(t, w, "pending", false)
	f.run = func(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error) {
		return executor.Result{}, nil
	}
	if err := w.step(context.Background(), time.Now()); err != nil {
		t.Fatal(err)
	}
	if f.runs.Load() != 0 {
		t.Fatal("disabled pending dispatched")
	}
	f.mu.Lock()
	f.state = ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	f.mu.Unlock()
	if err := w.step(context.Background(), time.Now()); err != nil {
		t.Fatal(err)
	}
	if f.runs.Load() != 1 {
		t.Fatal("current permission ignored")
	}
	w.player.worlds = &playerWorldSource{world: store.World{Colony: "other", Load: "load", Map: 0}}
	if err := w.step(context.Background(), time.Now().Add(time.Second)); err != nil {
		t.Fatal(err)
	}
	if f.runs.Load() != 1 {
		t.Fatal("wrong world dispatched")
	}
}

// A step-budget cancel between durable preparation and dispatch leaves an
// Attempt-0 Prepared action with no receipt. Once the native generation moves
// it must stay eligible so the next run re-prepares it under the current
// authority instead of stranding the plan.
func TestWorkerStalePreparedIsReDriven(t *testing.T) {
	t.Parallel()
	w, _, db := workerFixture(t)
	v := workerPending(t, w, "orphan", false)
	prepared, err := db.Prepare(context.Background(), v.Plan, v.Action, v.Snapshot, 1)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := db.LoadPlan(context.Background(), v.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if view := prepared.View(); view.Stage != domain.Prepared || view.Attempt != 0 || view.Unresolved {
		t.Fatalf("fixture is not an undispatched preparation: %+v", view)
	}
	scope := ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	if !workerEligible(plan, prepared.View(), scope, playerWorld(v.Snapshot)) {
		t.Fatal("current preparation ineligible")
	}
	scope.Snapshot.Native++
	if !workerEligible(plan, prepared.View(), scope, playerWorld(v.Snapshot)) {
		t.Fatal("prepared action orphaned by a moved native generation")
	}
	moved := store.Admission{Snapshot: scope.Snapshot, Tick: 2, Footprint: []domain.Cell{playerSubmission().Building.Cell()}}
	again, err := db.Prepare(context.Background(), v.Plan, v.Action, moved.Snapshot, moved.Tick)
	if err != nil || again.View().Snapshot != scope.Snapshot {
		t.Fatal("re-preparation under current authority refused", err)
	}
	if _, err = db.Dispatch(context.Background(), v.Plan, v.Action, scope.Snapshot, 2); err != nil {
		t.Fatal(err)
	}
}
func TestWorkerRefusedIntentIsTerminalAndUnknownWaitsForNewDirection(t *testing.T) {
	t.Parallel()
	w, _, db := workerFixture(t)
	// A refused intent is settled Unsuccessful: no direction retries it.
	v := workerPending(t, w, "refusal", true)
	progress, err := db.RecordReceipt(context.Background(), v.Plan, v.Action, v.Attempt, domain.ReceiptRefused)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := db.LoadPlan(context.Background(), v.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if progress.View().Stage != domain.Unsuccessful {
		t.Fatal(progress.View())
	}
	scope := ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	for range 2 {
		if workerEligible(plan, progress.View(), scope, playerWorld(v.Snapshot)) {
			t.Fatal("refused intent retried")
		}
		scope.Snapshot.Native++
	}
	// An unknown receipt goes back to Pending; only a newer native
	// generation (a resume) resends it.
	v = workerPending(t, w, "unknown", true)
	if progress, err = db.RecordReceipt(context.Background(), v.Plan, v.Action, v.Attempt, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	if plan, err = db.LoadPlan(context.Background(), v.Plan); err != nil {
		t.Fatal(err)
	}
	if progress.View().Stage != domain.Pending {
		t.Fatal(progress.View())
	}
	scope = ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	if workerEligible(plan, progress.View(), scope, playerWorld(v.Snapshot)) {
		t.Fatal("same activation resent an unknown receipt")
	}
	scope.Snapshot.Native++
	if !workerEligible(plan, progress.View(), scope, playerWorld(v.Snapshot)) {
		t.Fatal("new explicit direction could not resend an unknown receipt")
	}
	if workerEligible(plan, progress.View(), scope, store.World{Colony: "other", Load: "load", Map: 0}) {
		t.Fatal("wrong world resent")
	}
}
func TestWorkerUnsentWriteRetriesUnderSameDirection(t *testing.T) {
	t.Parallel()
	w, _, db := workerFixture(t)
	v := workerPending(t, w, "unsent", true)
	progress, err := db.RecordReceipt(context.Background(), v.Plan, v.Action, v.Attempt, domain.ReceiptUnsent)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := db.LoadPlan(context.Background(), v.Plan)
	if err != nil {
		t.Fatal(err)
	}
	scope := ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	if view := progress.View(); view.Unresolved || view.Stage != domain.Pending {
		t.Fatalf("unsent write left uncertain: %+v", view)
	}
	if !workerEligible(plan, progress.View(), scope, playerWorld(v.Snapshot)) {
		t.Fatal("same activation could not retry a write that was never sent")
	}
	if workerEligible(plan, progress.View(), scope, store.World{Colony: "other", Load: "load", Map: 0}) {
		t.Fatal("wrong world retried")
	}
}
func TestWorkerManualCancelsRun(t *testing.T) {
	t.Parallel()
	w, f, _ := workerFixture(t)
	v := workerPending(t, w, "active", false)
	f.mu.Lock()
	f.state = ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	f.mu.Unlock()
	entered := make(chan struct{})
	var once sync.Once
	f.run = func(ctx context.Context, _ domain.PlanID, _ domain.ActionID) (executor.Result, error) {
		once.Do(func() { close(entered) })
		<-ctx.Done()
		return executor.Result{}, ctx.Err()
	}
	running, err := newWorker(context.Background(), w.config, w.player, f)
	if err != nil {
		t.Fatal(err)
	}
	defer running.Close(context.Background())
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("run not entered")
	}
	_, err = w.player.Pause(context.Background(), store.ControlRequest{RequestID: "manual", Kind: store.PauseControl, World: playerWorld(v.Snapshot)})
	if err != nil {
		t.Fatal(err)
	}
	if f.State().Enabled {
		t.Fatal("manual retained permission")
	}
	if _, err := newWorker(context.Background(), w.config, w.player, f); err == nil {
		t.Fatal("duplicate worker")
	}
}
func TestWorkerCloseTimeoutRetainsSessionAndCanRetry(t *testing.T) {
	t.Parallel()
	w, f, _ := workerFixture(t)
	v := workerPending(t, w, "active", false)
	f.mu.Lock()
	f.state = ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	f.mu.Unlock()
	entered := make(chan struct{})
	release := make(chan struct{})
	f.run = func(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error) {
		close(entered)
		<-release
		return executor.Result{}, errors.New("lost reply")
	}
	running, err := newWorker(context.Background(), w.config, w.player, f)
	if err != nil {
		t.Fatal(err)
	}
	<-entered
	short, cancel := context.WithTimeout(context.Background(), time.Millisecond)
	defer cancel()
	if err = running.Close(short); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatal(err)
	}
	if f.closes.Load() != 0 || f.State().Enabled {
		t.Fatal("closed handles before join or remained enabled")
	}
	close(release)
	if err = running.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if f.closes.Load() != 1 {
		t.Fatal("session not joined")
	}
}

func TestWorkerRealSessionResendsAFailedApplyCall(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	ctx := context.Background()
	dir := t.TempDir()
	path := storetest.Path(t)
	db, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { db.Close() }()
	_, fixture := boundary.NewFixture(t)
	native := sessionNative{fixture}
	authority := &controlNative{generation: 1}
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, CallTimeout: 5 * time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second}}
	session, err := NewSession(ctx, config, db, native, authority, native, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { session.Close(ctx) }()
	worlds := &playerWorldSource{world: playerSubmission().World}
	player, err := NewPlayer(ctx, PlayerConfig{CallTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second}, db, session, worlds)
	if err != nil {
		t.Fatal(err)
	}
	request := playerAcquire(t, player)
	if _, err = player.Resume(ctx, request); err != nil {
		t.Fatal(err)
	}
	plan := playerPlan(t, db)
	// Guidance is dispatched under root authority in the plan's own scope.
	current := session.State().Snapshot
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	action := plan.Spec.Actions()[0]
	building, _ := action.Building()
	namespace, err := db.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	fixture.Preview.Preview.Action = action
	fixture.Preview.Preview.Snapshot = current
	fixture.Preview.Stock.Snapshot = current
	fixture.Preview.Preview.CanPlace = domain.Known(true)
	fixture.Preview.Preview.SafeToPlace = domain.Known(true)
	fixture.Preview.Preview.MadeFromStuff = domain.Known(true)
	fixture.Preview.Preview.Costs = domain.Known([]policy.Amount{{Resource: "WoodLog", Count: 1}})
	fixture.Preview.Preview.Footprint = domain.Known([]domain.Cell{building.Cell()})
	fixture.Preview.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(5))}}
	fixture.Bounds.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
	fixture.Emergency.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
	fixture.Receipt.AdmittedContext.NativeGeneration = proto.Uint64(uint64(current.Native))
	fixture.Receipt.AdmittedContext.Tick = proto.Int64(11)
	fixture.Receipt.Attempt.ControllerSessionId = proto.String(string(namespace))
	fixture.Receipt.Attempt.ActionId = proto.String(string(action.ID()))
	fixture.Progress.Attempt = proto.Clone(fixture.Receipt.Attempt).(*c.AttemptKey)
	w := &Worker{player: player, session: session, config: WorkerConfig{StepInterval: time.Millisecond, MaxBackoff: time.Second, StepTimeout: 5 * time.Second}, waits: make(map[domain.ActionID]workerWait)}
	fixture.PlaceErr = &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}
	// A failed Actions/Apply call leaves the receipt unknown, so the intent
	// goes back to Pending; the same activation does not resend it, a new
	// explicit direction does.
	if err = w.step(ctx, time.Now()); err == nil {
		t.Fatal("a failed apply call reported no error")
	}
	plan, err = db.LoadPlan(ctx, current.Plan)
	if err != nil || plan.Progress[0].View().Stage != domain.Pending || fixture.Places != 1 {
		t.Fatal(plan.Progress[0].View(), fixture.Places, err)
	}
	fixture.PlaceErr = nil
	if err = w.step(ctx, time.Now().Add(time.Hour)); err != nil || fixture.Places != 1 {
		t.Fatal("same activation resent an unknown receipt", fixture.Places, err)
	}
	request.RequestID = "acquire-again"
	if _, err = player.Resume(ctx, request); err != nil {
		t.Fatal(err)
	}
	current = session.State().Snapshot
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	fixture.Preview.Preview.Snapshot = current
	fixture.Preview.Stock.Snapshot = current
	fixture.Bounds.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
	fixture.Emergency.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
	if err = w.step(ctx, time.Now().Add(2*time.Hour)); err != nil {
		t.Fatal(err)
	}
	plan, err = db.LoadPlan(ctx, current.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if plan.Progress[0].View().Stage != domain.Completed || fixture.Places != 2 || authority.acquires.Load() != 2 {
		t.Fatal(plan.Progress[0].View(), fixture.Places, authority.acquires.Load())
	}
}

func TestWorkerObserveTargetSerializesAcquireAndManualPreempts(t *testing.T) {
	t.Parallel()
	w, f, _ := workerFixture(t)
	v := workerPending(t, w, "uncertain", true)
	entered := make(chan struct{})
	release := make(chan struct{})
	f.observe = func(ctx context.Context, _ domain.GenerationSnapshot) error {
		close(entered)
		select {
		case <-release:
			return nil
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	f.run = func(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error) {
		t.Error("cancelled observation ran")
		return executor.Result{}, nil
	}
	finished := make(chan error, 1)
	go func() { finished <- w.step(context.Background(), time.Now()) }()
	<-entered
	acquireDone := make(chan error, 1)
	acquireQueued := make(chan struct{})
	w.player.queued = func() { w.player.queued = nil; close(acquireQueued) }
	go func() {
		_, err := w.player.Resume(context.Background(), store.ControlRequest{RequestID: "queued", Kind: store.ResumeControl, World: playerWorld(v.Snapshot)})
		acquireDone <- err
	}()
	// The queued Acquire must have read the epoch it will lose against before
	// Manual bumps it; a Resume that only reads the fresh epoch after Manual
	// would legitimately win the gate and acquire.
	<-acquireQueued
	if f.acquires.Load() != 0 {
		t.Fatal("Acquire passed reconciliation gate")
	}
	_, err := w.player.Pause(context.Background(), store.ControlRequest{RequestID: "manual", Kind: store.PauseControl, World: playerWorld(v.Snapshot)})
	if err != nil {
		t.Fatal(err)
	}
	if err = <-finished; err == nil {
		t.Fatal("observation survived Manual")
	}
	// A queued pre-Manual Acquire must fail.
	if err = <-acquireDone; err == nil || f.acquires.Load() != 0 {
		t.Fatal("queued acquire escaped", err)
	}
}
func TestWorkerUnknownAndTerminalDoNotDispatch(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	v := workerPending(t, w, "unknown", true)
	f.run = func(ctx context.Context, p domain.PlanID, _ domain.ActionID) (executor.Result, error) {
		plan, err := db.LoadPlan(ctx, p)
		return executor.Result{Progress: plan.Progress[0]}, errors.Join(err, executor.ErrEvidence)
	}
	if err := w.step(context.Background(), time.Now()); !errors.Is(err, executor.ErrEvidence) {
		t.Fatal(err)
	}
	if f.observes.Load() != 1 || f.acquires.Load() != 0 {
		t.Fatal("unknown was not read-only")
	}
	plan, err := db.LoadPlan(context.Background(), v.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if !plan.Progress[0].View().Unresolved {
		t.Fatal("unknown erased")
	}
	terminal := v
	terminal.Unresolved = false
	scope := ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	for _, stage := range []domain.Stage{domain.Completed, domain.Unsuccessful, domain.Cancelled} {
		terminal.Stage = stage
		if workerEligible(plan, terminal, scope, playerWorld(v.Snapshot)) {
			t.Fatal("terminal eligible", stage)
		}
	}
}
func TestWorkerLifetimeCancellationStopsPlayer(t *testing.T) {
	t.Parallel()
	w, f, _ := workerFixture(t)
	lifetime, cancel := context.WithCancel(context.Background())
	running, err := newWorker(lifetime, w.config, w.player, f)
	if err != nil {
		t.Fatal(err)
	}
	cancel()
	select {
	case <-running.done:
	case <-time.After(time.Second):
		t.Fatal("worker ignored cancellation")
	}
	if err = running.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
}

func TestWorkerIdentityLossImmediatelyDisablesDispatch(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	cases := []struct {
		name  string
		world store.World
		err   error
	}{
		{name: "colony", world: store.World{Colony: "other", Load: "load", Map: 0}},
		{name: "load", world: store.World{Colony: "colony", Load: "other", Map: 0}},
		{name: "map", world: store.World{Colony: "colony", Load: "load", Map: 1}},
		{name: "unavailable", err: errors.New("identity unavailable")},
		{name: "invalid", world: store.World{Colony: "", Load: "load", Map: 0}},
	}
	for _, test := range cases {
		t.Run(test.name, func(t *testing.T) {
			w, f, _ := workerFixture(t)
			v := workerPending(t, w, "pending", false)
			f.mu.Lock()
			f.state = ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
			f.mu.Unlock()
			w.player.worlds = &playerWorldSource{world: test.world, err: test.err}
			err := w.step(context.Background(), time.Now())
			if (test.err != nil || test.world.Validate() != nil) != (err != nil) {
				t.Fatal(err)
			}
			state := w.player.State()
			if state.Enabled || !state.ObservationKnown || state.Snapshot != v.Snapshot {
				t.Fatal("permission retained or cleanup scope erased", state)
			}
			if f.runs.Load() != 0 || f.acquires.Load() != 0 || f.manuals.Load() != 0 {
				t.Fatal("identity loss caused native work")
			}
		})
	}
}

func (f *workerFake) ReleaseClosedFights(context.Context) error { return nil }

func (f *workerFake) CleanupDraft(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
	f.cleanups.Add(1)
	if f.cleanup != nil {
		return f.cleanup(ctx, p, a)
	}
	return executor.Result{}, executor.ErrHeld
}

func TestWorkerBackoffIgnoresTickAndStretchesForUnknownEffects(t *testing.T) {
	t.Parallel()
	before := domain.ProgressView{Action: "a", Attempt: 1, Stage: domain.AwaitingObservation, Tick: 100, Effect: domain.Known(domain.EffectUnknown)}
	after := before
	after.Tick = 700
	if !workerSameView(before, after) {
		t.Fatal("a re-read at a later tick is not a changed view")
	}
	after.Effect = domain.Known(domain.EffectPending)
	if workerSameView(before, after) {
		t.Fatal("a changed effect is a changed view")
	}
	config := WorkerConfig{StepInterval: time.Second, MaxBackoff: 10 * time.Second}
	if got := workerBackoffCap(config, before); got != time.Minute {
		t.Fatal("unknown effect cap", got)
	}
	if got := workerBackoffCap(config, after); got != 10*time.Second {
		t.Fatal("pending effect cap", got)
	}
	config.MaxBackoff = time.Minute
	if got := workerBackoffCap(config, before); got != time.Minute {
		t.Fatal("cap never exceeds a minute", got)
	}
}

// A dispatch runs under a span of its own beneath the worker step's span,
// which nests under the scheduler's latest step (WorkerConfig.Trace), so
// the rows it leaves join that step's trace; without a scheduler trace the
// step is a root of its own.
func TestWorkerDispatchNestsUnderTheSchedulerTrace(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	workerPending(t, w, "trace", true)
	var seen telemetry.Trace
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		seen = telemetry.TraceFrom(ctx)
		plan, err := db.LoadPlan(ctx, p)
		return executor.Result{Progress: plan.Progress[0]}, err
	}
	if err := w.step(context.Background(), time.Now()); err != nil {
		t.Fatal(err)
	}
	// The step is the root; the dispatch is its direct child.
	if f.runs.Load() != 1 || seen.Empty() || seen.ParentID != seen.TraceID || seen.SpanID == seen.TraceID {
		t.Fatalf("dispatch without a scheduler is not a span under its own step: %+v", seen)
	}
	step := telemetry.NewTrace()
	w.config.Trace = func() telemetry.Trace { return step }
	w.waits = map[domain.ActionID]workerWait{}
	if err := w.step(context.Background(), time.Now()); err != nil {
		t.Fatal(err)
	}
	if seen.TraceID != step.TraceID || seen.ParentID == "" || seen.ParentID == step.SpanID || seen.SpanID == seen.ParentID {
		t.Fatalf("dispatch %+v is not a span two levels under step %+v", seen, step)
	}
}

// The rotation is by plan: a long plan sorted first takes one step per
// round, so a short plan behind it (the work assignments behind a
// forty-action shell) has its turn every round rather than after the long
// plan's last action.
func TestWorkerRotationAlternatesPlans(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	w.config.MaxDispatches = 1
	short := workerPending(t, w, "short", true)
	// "0-long" sorts before the short plan's digest id.
	var actions []domain.Action
	for i := 0; i < 3; i++ {
		b, err := domain.NewBuilding("Wall", domain.Cell{X: int32(10 + i), Z: 2}, domain.North, "WoodLog")
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("0-long-%d", i)), b)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, action)
	}
	long, err := domain.NewPlan("0-long", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreatePlan(context.Background(), long); err != nil {
		t.Fatal(err)
	}
	snapshot := short.Snapshot
	snapshot.Plan = long.ID()
	for _, action := range actions {
		if _, err = db.Prepare(context.Background(), long.ID(), action.ID(), snapshot, 1); err != nil {
			t.Fatal(err)
		}
		if _, err = db.Dispatch(context.Background(), long.ID(), action.ID(), snapshot, 1); err != nil {
			t.Fatal(err)
		}
	}
	var order []domain.ActionID
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		order = append(order, a)
		plan, err := db.LoadPlan(ctx, p)
		for _, progress := range plan.Progress {
			if progress.View().Action == a {
				return executor.Result{Progress: progress}, err
			}
		}
		return executor.Result{}, err
	}
	now := time.Now()
	for i := 0; i < 4; i++ {
		if err := w.step(context.Background(), now); err != nil {
			t.Fatal(err)
		}
	}
	// A plan's building actions go out as one batch, one dispatch:
	// the long plan's batch takes the first step and the short plan the next.
	want := []domain.ActionID{"0-long-0", "0-long-1", "0-long-2", short.Action}
	if fmt.Sprint(order) != fmt.Sprint(want) || f.batches.Load() != 2 {
		t.Fatal(order, want, f.batches.Load())
	}
}

// Every eligible plan's batch dispatches in the step that finds it, in
// catalog order, up to the dispatch budget; one per step cost a scheduler round per
// wall segment. Actions on their backoff are skipped, and the budget
// leaves the rest for the next step past the rotation cursor.
func TestWorkerDispatchesEveryEligibleActionPerStep(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	views := []domain.ProgressView{workerPending(t, w, "one", true), workerPending(t, w, "two", true), workerPending(t, w, "three", true)}
	sort.Slice(views, func(i, j int) bool { return views[i].Plan < views[j].Plan })
	var order []domain.ActionID
	f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
		order = append(order, a)
		plan, err := db.LoadPlan(ctx, p)
		return executor.Result{Progress: plan.Progress[0]}, err
	}
	now := time.Now()
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(order) != 3 || order[0] != views[0].Action || order[1] != views[1].Action || order[2] != views[2].Action || f.batches.Load() != 3 {
		t.Fatal("one step dispatches every eligible plan's batch", order, f.batches.Load())
	}
	// All three are now on their backoff: a step at the same instant runs none.
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(order) != 3 {
		t.Fatal("backoff ignored", order)
	}
	// A budget of two takes two and leaves the third for the next step,
	// which resumes past the cursor rather than at the head.
	w.waits = map[domain.ActionID]workerWait{}
	w.cursorKnown = false
	w.config.MaxDispatches = 2
	order = nil
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(order) != 2 || order[0] != views[0].Action || order[1] != views[1].Action || f.batches.Load() != 5 {
		t.Fatal("budget counts batches", order, f.batches.Load())
	}
	w.waits = map[domain.ActionID]workerWait{}
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if len(order) != 4 || order[2] != views[2].Action || order[3] != views[0].Action {
		t.Fatal("rotation past the budget cursor", order)
	}
}

// workerWallPlan creates plan id with n unresolved wall actions under
// snapshot's world and returns their ids in catalog order.
func workerWallPlan(t *testing.T, db *store.Store, id domain.PlanID, n int, snapshot domain.GenerationSnapshot) []domain.ActionID {
	t.Helper()
	var actions []domain.Action
	var ids []domain.ActionID
	for i := 0; i < n; i++ {
		b, err := domain.NewBuilding("Wall", domain.Cell{X: int32(10 + i), Z: 2}, domain.North, "WoodLog")
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%02d", id, i)), b)
		if err != nil {
			t.Fatal(err)
		}
		actions, ids = append(actions, action), append(ids, action.ID())
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	snapshot.Plan = id
	for _, action := range ids {
		if _, err = db.Prepare(context.Background(), id, action, snapshot, 1); err != nil {
			t.Fatal(err)
		}
		if _, err = db.Dispatch(context.Background(), id, action, snapshot, 1); err != nil {
			t.Fatal(err)
		}
	}
	return ids
}

// workerBatchRecorder records each RunBatch call's plan and actions.
func workerBatchRecorder(f *workerFake, db *store.Store) *[][]domain.ActionID {
	var calls [][]domain.ActionID
	f.batch = func(ctx context.Context, p domain.PlanID, ids []domain.ActionID) ([]executor.BatchItem, error) {
		calls = append(calls, append([]domain.ActionID(nil), ids...))
		plan, err := db.LoadPlan(ctx, p)
		out := make([]executor.BatchItem, len(ids))
		for i, id := range ids {
			out[i].Action = id
			for _, progress := range plan.Progress {
				if progress.View().Action == id {
					out[i].Result.Progress = progress
				}
			}
		}
		return out, err
	}
	return &calls
}

// A 32-wall shell plan goes out in one step as one batched Apply.
func TestWorkerShellPlanIsOneBatch(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	w, f, db := workerFixture(t)
	short := workerPending(t, w, "seed", true)
	f.run = func(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error) {
		return executor.Result{}, nil
	}
	ids := workerWallPlan(t, db, "0-shell", 32, short.Snapshot)
	calls := workerBatchRecorder(f, db)
	w.config.MaxDispatches = 1
	if err := w.step(context.Background(), time.Now()); err != nil {
		t.Fatal(err)
	}
	if len(*calls) != 1 || fmt.Sprint((*calls)[0]) != fmt.Sprint(ids) || f.runs.Load() != 0 {
		t.Fatal("shell plan not one batch", *calls, f.runs.Load())
	}
}

// Two plans give two batches per step, and the rotation cursor still moves
// by plan when the budget admits only one.
func TestWorkerTwoPlansTwoBatchesRotate(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	seed := workerPending(t, w, "seed", false)
	f.run = func(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error) {
		return executor.Result{}, nil
	}
	if _, err := w.player.journal.Cancel(context.Background(), seed.Plan, seed.Action); err != nil {
		t.Fatal(err)
	}
	a := workerWallPlan(t, db, "0-a", 3, seed.Snapshot)
	b := workerWallPlan(t, db, "0-b", 2, seed.Snapshot)
	calls := workerBatchRecorder(f, db)
	now := time.Now()
	if err := w.step(context.Background(), now); err != nil {
		t.Fatal(err)
	}
	if fmt.Sprint(*calls) != fmt.Sprint([][]domain.ActionID{a, b}) {
		t.Fatal("two plans, two batches", *calls)
	}
	*calls = nil
	w.waits = map[domain.ActionID]workerWait{}
	w.cursorKnown = false
	w.config.MaxDispatches = 1
	for i := 0; i < 3; i++ {
		w.waits = map[domain.ActionID]workerWait{}
		if err := w.step(context.Background(), now); err != nil {
			t.Fatal(err)
		}
	}
	if fmt.Sprint(*calls) != fmt.Sprint([][]domain.ActionID{a, b, a}) {
		t.Fatal("rotation by plan", *calls)
	}
}
