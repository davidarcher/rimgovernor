package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type workerFake struct {
	*playerFakeSession
	run                    func(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error)
	observe                func(context.Context, domain.GenerationSnapshot) error
	renew                  func(context.Context) error
	runs, observes, renews atomic.Int32
}

func (f *workerFake) Run(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
	f.runs.Add(1)
	return f.run(ctx, p, a)
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
	config := WorkerConfig{StepInterval: 10 * time.Millisecond, MaxBackoff: time.Second, StepTimeout: time.Second, RenewInterval: 10 * time.Millisecond, RenewTimeout: time.Millisecond * 100}
	w := &Worker{player: p, session: f, config: config, waits: make(map[domain.ActionID]workerWait)}
	return w, f, db
}
func workerPending(t *testing.T, w *Worker, id string, unresolved bool) domain.ProgressView {
	t.Helper()
	q := playerSubmission()
	q.RequestID = id
	submission, _, err := w.player.Submit(context.Background(), q)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: q.World.Colony, Load: q.World.Load, Map: q.World.Map, Plan: submission.Plan, Revision: 1, Direction: 1, Native: 1}
	if unresolved {
		_, err = w.player.journal.ReserveAndPrepare(context.Background(), submission.Plan, submission.Action, store.Admission{Snapshot: snapshot, Tick: 1, Costs: []store.MaterialCost{}, Footprint: []domain.Cell{q.Building.Cell()}})
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
	w, f, db := workerFixture(t)
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
func TestWorkerActualPermissionAndWrongWorld(t *testing.T) {
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
func TestWorkerRefusalRequiresNewExplicitDirection(t *testing.T) {
	w, _, db := workerFixture(t)
	v := workerPending(t, w, "refusal", true)
	progress, err := db.RecordReceipt(context.Background(), v.Plan, v.Action, v.Attempt, domain.ReceiptRefused)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := db.LoadPlan(context.Background(), v.Plan)
	if err != nil {
		t.Fatal(err)
	}
	scope := ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	if workerEligible(plan, progress.View(), scope, playerWorld(v.Snapshot)) {
		t.Fatal("same activation retried refusal")
	}
	scope.Snapshot.Direction++
	if !workerEligible(plan, progress.View(), scope, playerWorld(v.Snapshot)) {
		t.Fatal("new explicit intent could not retry no-effect refusal")
	}
	scope.Snapshot.Native++
	if !workerEligible(plan, progress.View(), scope, playerWorld(v.Snapshot)) {
		t.Fatal("fresh native generation rejected")
	}
}
func TestWorkerManualCancelsRunAndRenewIsIndependent(t *testing.T) {
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
	running, err := newWorker(context.Background(), w.config, w.player, f, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer running.Close(context.Background())
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("run not entered")
	}
	deadline := time.Now().Add(time.Second)
	for f.renews.Load() == 0 && time.Now().Before(deadline) {
		time.Sleep(time.Millisecond)
	}
	if f.renews.Load() == 0 {
		t.Fatal("run blocked independent renew")
	}
	_, err = w.player.Manual(context.Background(), store.ControlRequest{RequestID: "manual", Kind: store.ManualControl, World: playerWorld(v.Snapshot)})
	if err != nil {
		t.Fatal(err)
	}
	if f.State().Enabled {
		t.Fatal("manual retained permission")
	}
	if _, err := newWorker(context.Background(), w.config, w.player, f, time.Second); err == nil {
		t.Fatal("duplicate worker")
	}
}
func TestWorkerCloseTimeoutRetainsSessionAndCanRetry(t *testing.T) {
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
	running, err := newWorker(context.Background(), w.config, w.player, f, time.Second)
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

func TestWorkerRealSessionReopensUncertainAttemptWithoutAcquire(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	path := filepath.Join(dir, "worker.sqlite")
	db, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { db.Close() }()
	_, fixture := newBoundaryFixture(t)
	native := sessionNative{fixture}
	authority := &controlNative{generation: 1}
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second}}
	session, err := NewSession(ctx, config, db, native, authority, native, boundaryClock{})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { session.Close(ctx) }()
	worlds := &playerWorldSource{world: playerSubmission().World}
	player, err := NewPlayer(ctx, PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db, session, worlds)
	if err != nil {
		t.Fatal(err)
	}
	request := playerAcquire(t, player)
	if _, err = player.Acquire(ctx, request); err != nil {
		t.Fatal(err)
	}
	current := session.State().Snapshot
	plan, err := db.LoadPlan(ctx, current.Plan)
	if err != nil {
		t.Fatal(err)
	}
	action := plan.Spec.Actions()[0]
	building, _ := action.Building()
	namespace, err := db.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	fixture.preview.Preview.Action = action
	fixture.preview.Preview.Snapshot = current
	fixture.preview.Stock.Snapshot = current
	fixture.preview.Preview.CanPlace = domain.Known(true)
	fixture.preview.Preview.SafeToPlace = domain.Known(true)
	fixture.preview.Preview.MadeFromStuff = domain.Known(true)
	fixture.preview.Preview.Costs = domain.Known([]policy.Amount{{Resource: "WoodLog", Count: 1}})
	fixture.preview.Preview.Footprint = domain.Known([]domain.Cell{building.Cell()})
	fixture.preview.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(5))}}
	fixture.bounds.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
	fixture.receipt.AdmittedContext.NativeGeneration = proto.Uint64(uint64(current.Native))
	fixture.receipt.AdmittedContext.Tick = proto.Int64(11)
	fixture.receipt.Attempt.ControllerSessionId = proto.String(string(namespace))
	fixture.receipt.Attempt.ActionId = proto.String(string(action.ID()))
	fixture.receipt.AuthorizingOwner.ControllerSessionId = proto.String(string(namespace))
	fixture.progress.Attempt = proto.Clone(fixture.receipt.Attempt).(*c.AttemptKey)
	w := &Worker{player: player, session: session, config: WorkerConfig{StepInterval: time.Millisecond, MaxBackoff: time.Second, StepTimeout: time.Second}, waits: make(map[domain.ActionID]workerWait)}
	fixture.placeErr = &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}
	if err = w.step(ctx, time.Now()); err != nil {
		t.Fatal(err)
	}
	if err = w.step(ctx, time.Now().Add(time.Second)); err != nil {
		t.Fatal(err)
	}
	if fixture.places != 1 {
		t.Fatal("same activation retried known refusal", fixture.places)
	}
	request.RequestID = "acquire-again"
	request.ExpectedDirection = current.Direction
	if _, err = player.Acquire(ctx, request); err != nil {
		t.Fatal(err)
	}
	current = session.State().Snapshot
	fixture.preview.Preview.Snapshot = current
	fixture.preview.Stock.Snapshot = current
	fixture.bounds.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
	fixture.receipt.AdmittedContext.NativeGeneration = proto.Uint64(uint64(current.Native))
	fixture.receipt.Attempt.AttemptId = proto.Uint64(2)
	fixture.receipt.AuthorizingOwner.PlayerDirection = proto.Uint64(uint64(current.Direction))
	fixture.progress.Attempt = proto.Clone(fixture.receipt.Attempt).(*c.AttemptKey)
	fixture.placeErr = nil
	if err = w.step(ctx, time.Now().Add(2*time.Second)); err != nil {
		t.Fatal(err)
	}
	if fixture.places != 2 {
		t.Fatal("new explicit direction did not permit one retry", fixture.places)
	}
	if err = player.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if err = db.Close(); err != nil {
		t.Fatal(err)
	}
	db, err = store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	session, err = NewSession(ctx, config, db, native, authority, native, boundaryClock{})
	if err != nil {
		t.Fatal(err)
	}
	player, err = NewPlayer(ctx, PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db, session, worlds)
	if err != nil {
		t.Fatal(err)
	}
	defer player.Close(ctx)
	// Replaying a historical grant cannot reenable this fresh session.
	record, err := player.Acquire(ctx, request)
	if err != nil || record.Phase != store.GrantedControl || player.State().Enabled {
		t.Fatal(record, err)
	}
	fixture.progress.Context.NativeGeneration = proto.Uint64(uint64(current.Native) + 1)
	fixture.progress.Context.Tick = proto.Int64(12)
	w.player, w.session = player, session
	if err = w.step(ctx, time.Now().Add(time.Second)); err != nil {
		t.Fatal(err)
	}
	plan, err = db.LoadPlan(ctx, current.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if plan.Progress[0].View().Stage != domain.Completed || fixture.places != 2 || authority.acquires.Load() != 2 {
		t.Fatal(plan.Progress[0].View(), fixture.places, authority.acquires.Load())
	}
}

func TestWorkerObserveTargetSerializesAcquireAndManualPreempts(t *testing.T) {
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
	go func() {
		_, err := w.player.Acquire(context.Background(), store.ControlRequest{RequestID: "queued", Kind: store.AcquireControl, World: playerWorld(v.Snapshot), Plan: v.Plan, Revision: 1})
		acquireDone <- err
	}()
	if f.acquires.Load() != 0 {
		t.Fatal("Acquire passed reconciliation gate")
	}
	_, err := w.player.Manual(context.Background(), store.ControlRequest{RequestID: "manual", Kind: store.ManualControl, World: playerWorld(v.Snapshot)})
	if err != nil {
		t.Fatal(err)
	}
	if err = <-finished; err == nil {
		t.Fatal("observation survived Manual")
	}
	// A queued pre-Manual Acquire must fail; even if it queues only afterward,
	// its CAS against the now durable Manual direction cannot acquire.
	if err = <-acquireDone; err == nil || f.acquires.Load() != 0 {
		t.Fatal("queued acquire escaped", err)
	}
}
func TestWorkerUnknownAndTerminalDoNotDispatch(t *testing.T) {
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
	w, f, _ := workerFixture(t)
	lifetime, cancel := context.WithCancel(context.Background())
	running, err := newWorker(lifetime, w.config, w.player, f, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	cancel()
	select {
	case <-running.done:
	case <-time.After(time.Second):
		t.Fatal("worker ignored cancellation")
	}
	if _, _, err = w.player.Submit(context.Background(), playerSubmission()); err == nil {
		t.Fatal("stopped worker accepted request")
	}
	if err = running.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
}

func TestWorkerIdentityLossImmediatelyDisablesDispatchAndRenewal(t *testing.T) {
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
			ctx, cancel := context.WithTimeout(context.Background(), 3*w.config.RenewInterval)
			defer cancel()
			w.ctx = ctx
			w.renewals()
			if f.renews.Load() != 0 {
				t.Fatal("renewal continued after identity loss")
			}
		})
	}
}
