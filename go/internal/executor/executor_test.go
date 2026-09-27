package executor

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
)

type environment struct {
	mu                      sync.Mutex
	clock                   Clock
	stock                   int64
	tick                    domain.Tick
	inspections, placements int
	onInspect               func(int, IntentInspection) IntentInspection
	onPlace                 func(context.Context, Placement) (Receipt, error)
}

func (f *environment) InspectIntent(_ context.Context, target Target) (IntentInspection, error) {
	f.mu.Lock()
	f.inspections++
	count, tick := f.inspections, f.tick
	f.mu.Unlock()
	now := f.clock.Now()
	result := IntentInspection{Current: target.Snapshot, Tick: tick, StartedAt: now, ObservedAt: now}
	if f.onInspect != nil {
		return f.onInspect(count, result), nil
	}
	return result, nil
}
func (f *environment) WriteIntent(ctx context.Context, placement Placement) (Receipt, error) {
	f.mu.Lock()
	f.placements++
	f.mu.Unlock()
	if f.onPlace != nil {
		return f.onPlace(ctx, placement)
	}
	return Receipt{Action: placement.Action.ID(), Attempt: placement.Attempt, Snapshot: placement.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (f *environment) counts() (int, int) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.inspections, f.placements
}

type fixture struct {
	executor  *Executor
	store     *store.Store
	env       *environment
	clock     *testkit.ManualClock
	plan      domain.PlanSpec
	action    domain.Action
	authority Authority
	path      string
}

func newFixture(t *testing.T) *fixture {
	t.Helper()
	return newFixtureAt(t, storetest.Path(t))
}

func newFixtureAt(t *testing.T, path string) *fixture {
	t.Helper()
	ctx := context.Background()
	clock := testkit.NewManualClock(time.Date(2026, 9, 10, 12, 0, 0, 0, time.UTC))
	journal, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = journal.Close() })
	building, err := domain.NewBuilding("Wall", domain.Cell{X: 10, Z: 10}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewBuildingAction("action-1", building)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("plan-1", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	env := &environment{clock: clock, stock: 21, tick: 100}
	executor, err := New(journal, env, clock, Limits{MaxAge: time.Second, RunTimeout: 2 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	authority := Authority{Enabled: true, Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: plan.Revision(), Native: 1}}
	if err = executor.UpdateAuthority(authority); err != nil {
		t.Fatal(err)
	}
	return &fixture{executor, journal, env, clock, plan, action, authority, path}
}
func (f *fixture) run() (Result, error) {
	return f.executor.Run(context.Background(), f.plan.ID(), f.action.ID())
}
func (f *fixture) progress(t *testing.T) domain.ProgressView {
	t.Helper()
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	return state.Progress[0].View()
}

type hookedJournal struct {
	Journal
	afterDispatch func()
	dispatchError error
}

func (j *hookedJournal) Dispatch(ctx context.Context, p domain.PlanID, a domain.ActionID, g domain.GenerationSnapshot, tick domain.Tick) (domain.Progress, error) {
	if j.dispatchError != nil {
		return domain.Progress{}, j.dispatchError
	}
	progress, err := j.Journal.Dispatch(ctx, p, a, g, tick)
	if err == nil && j.afterDispatch != nil {
		j.afterDispatch()
	}
	return progress, err
}

// A building intent is dispatched durably before native is called, and its
// applied receipt completes the action without an observation (#856).
func TestDurableDispatchThenAppliedIntentCompletes(t *testing.T) {
	f := newFixture(t)
	f.env.onPlace = func(_ context.Context, p Placement) (Receipt, error) {
		view := f.progress(t)
		if view.Stage != domain.Dispatched || !view.Unresolved || view.Attempt != p.Attempt || view.Attempt != 1 {
			t.Fatalf("native call preceded durable dispatch: %+v", view)
		}
		return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
	}
	result, err := f.run()
	if err != nil || !result.NativeCalled {
		t.Fatal(err)
	}
	if v := f.progress(t); v.Stage != domain.Completed || v.Unresolved {
		t.Fatalf("applied intent not terminal: %+v", v)
	}
	if result, err = f.run(); err != nil || result.NativeCalled {
		t.Fatal("completed intent redispatched", err)
	}
}

func TestRefusedIntentIsUnsuccessful(t *testing.T) {
	f := newFixture(t)
	f.env.onPlace = func(_ context.Context, p Placement) (Receipt, error) {
		return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptRefused}, nil
	}
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	if v := f.progress(t); v.Stage != domain.Unsuccessful || v.Unresolved {
		t.Fatalf("refused intent not unsuccessful: %+v", v)
	}
}

// A lost reply leaves the intent unknown; later runs resend it under a
// fresh attempt, which native answers idempotently.
func TestLostReplyResendsUnderAFreshAttempt(t *testing.T) {
	f := newFixture(t)
	f.env.onPlace = func(context.Context, Placement) (Receipt, error) { return Receipt{}, errors.New("reply lost") }
	if result, err := f.run(); err == nil || !result.NativeCalled {
		t.Fatal("lost reply hidden", err)
	}
	f.env.onPlace = nil
	for range 3 {
		if _, err := f.run(); err != nil {
			t.Fatal(err)
		}
		if f.progress(t).Stage == domain.Completed {
			break
		}
	}
	if v := f.progress(t); v.Stage != domain.Completed || v.Attempt < 2 {
		t.Fatalf("lost reply not resent: %+v", v)
	}
}

func TestInvalidationAndFreshnessPreventNativeWrites(t *testing.T) {
	for _, change := range []func(*Authority){func(a *Authority) { a.Enabled = false }, func(a *Authority) { a.Snapshot.Load = "new-load" }, func(a *Authority) { a.Snapshot.Map++ }, func(a *Authority) { a.Snapshot.Native++ }, func(a *Authority) { a.Snapshot.Revision++ }} {
		f := newFixture(t)
		f.env.onInspect = func(_ int, in IntentInspection) IntentInspection {
			next := f.authority
			change(&next)
			if err := f.executor.UpdateAuthority(next); err != nil {
				t.Fatal(err)
			}
			return in
		}
		if result, err := f.run(); err == nil || result.NativeCalled {
			t.Fatal("authority invalidation dispatched")
		}
		if _, calls := f.env.counts(); calls != 0 {
			t.Fatal("native write under stale authority")
		}
	}
	for _, change := range []func(*IntentInspection){func(i *IntentInspection) { i.StartedAt = i.StartedAt.Add(-2 * time.Second) }, func(i *IntentInspection) { i.ObservedAt = i.ObservedAt.Add(time.Second) }, func(i *IntentInspection) { i.Current.Load = "other" }} {
		f := newFixture(t)
		f.env.onInspect = func(_ int, in IntentInspection) IntentInspection { change(&in); return in }
		if result, err := f.run(); err == nil || result.NativeCalled {
			t.Fatal("stale facts dispatched")
		}
	}
}

func TestUnsentWriteRetries(t *testing.T) {
	f := newFixture(t)
	unsent := fmt.Errorf("%w: describe operations_apply: deadline", domain.ErrWriteUnsent)
	f.env.onPlace = func(context.Context, Placement) (Receipt, error) { return Receipt{}, unsent }
	if result, err := f.run(); !errors.Is(err, domain.ErrWriteUnsent) || !result.NativeCalled {
		t.Fatal("unsent write not surfaced", err)
	}
	if v := f.progress(t); v.Unresolved || v.Stage != domain.Pending {
		t.Fatalf("unsent write left uncertain: %+v", v)
	}
	f.env.onPlace = nil
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	if v := f.progress(t); v.Attempt != 2 || v.Stage != domain.Completed {
		t.Fatalf("retry not dispatched: %+v", v)
	}
}

func TestCancellationAndWriterQueue(t *testing.T) {
	f := newFixture(t)
	entered := make(chan struct{})
	f.env.onPlace = func(ctx context.Context, _ Placement) (Receipt, error) {
		close(entered)
		<-ctx.Done()
		return Receipt{}, ctx.Err()
	}
	done := make(chan error, 1)
	go func() { _, err := f.run(); done <- err }()
	<-entered
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	if _, err := f.executor.Run(ctx, f.plan.ID(), f.action.ID()); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatal("writer wait ignored cancellation", err)
	}
	if _, err := f.executor.Cancel(context.Background(), f.plan.ID(), f.action.ID()); err != nil {
		t.Fatal(err)
	}
	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatal("action cancellation ignored", err)
	}
	if _, calls := f.env.counts(); calls != 1 {
		t.Fatal("queue issued duplicate native write")
	}
}

func TestDispatchJournalFailureNeverAuthorizesNative(t *testing.T) {
	f := newFixture(t)
	failure := errors.New("dispatch commit failed")
	f.executor.journal = &hookedJournal{Journal: f.store, dispatchError: failure}
	if result, err := f.run(); !errors.Is(err, failure) || result.NativeCalled {
		t.Fatal("failed commit authorized native", err)
	}
	if _, calls := f.env.counts(); calls != 0 {
		t.Fatal("write after dispatch persistence failure")
	}
}

func TestAuthorityChangeAfterDurableDispatchKeepsUncertainty(t *testing.T) {
	f := newFixture(t)
	hook := &hookedJournal{Journal: f.store}
	f.executor.journal = hook
	hook.afterDispatch = func() { next := f.authority; next.Enabled = false; _ = f.executor.UpdateAuthority(next) }
	if result, err := f.run(); err == nil || result.NativeCalled {
		t.Fatal("invalidation after journal invoked native")
	}
	if receipt, known := f.progress(t).Receipt.Value(); !known || receipt != domain.ReceiptUnknown {
		t.Fatal("uncertain receipt not journaled")
	}
}

func TestManualRoundTripStillInvalidatesActiveGeneration(t *testing.T) {
	f := newFixture(t)
	f.env.onInspect = func(_ int, in IntentInspection) IntentInspection {
		manual := f.authority
		manual.Enabled = false
		_ = f.executor.UpdateAuthority(manual)
		_ = f.executor.UpdateAuthority(f.authority)
		return in
	}
	if result, err := f.run(); err == nil || result.NativeCalled {
		t.Fatal("old generation survived authority roundtrip")
	}
}
