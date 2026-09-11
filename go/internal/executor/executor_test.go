package executor

import (
	"context"
	"errors"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
)

type environment struct {
	observeError                          error
	mu                                    sync.Mutex
	clock                                 Clock
	stock                                 int64
	tick                                  domain.Tick
	inspections, placements, observations int
	onInspect                             func(int, Inspection) Inspection
	onPlace                               func(context.Context, Placement) (Receipt, error)
	onObserve                             func(Placement, domain.GenerationSnapshot, int) Evidence
}

func (f *environment) Inspect(_ context.Context, target Target) (Inspection, error) {
	f.mu.Lock()
	f.inspections++
	count := f.inspections
	stock, tick := f.stock, f.tick
	f.mu.Unlock()
	building, _ := target.Action.Building()
	clearance, err := policy.NewEmergencySnapshot(target.Snapshot, tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)})
	if err != nil {
		return Inspection{}, err
	}
	now := f.clock.Now()
	result := Inspection{Emergency: clearance, ExternalHoldsComplete: true, Current: target.Snapshot, Tick: tick, StartedAt: now, ObservedAt: now, Bounds: domain.Known(policy.Bounds{Width: 100, Height: 100}), Preview: policy.Preview{Action: target.Action, Snapshot: target.Snapshot, Tick: tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(true), Footprint: domain.Known([]domain.Cell{building.Cell()}), Costs: domain.Known([]policy.Amount{{Resource: "WoodLog", Count: 10}})}, Stock: policy.StockObservation{Snapshot: target.Snapshot, Tick: tick, Values: []policy.Stock{{Resource: "WoodLog", Available: domain.Known(stock)}}}}
	if f.onInspect != nil {
		return f.onInspect(count, result), nil
	}
	return result, nil
}
func (f *environment) Place(ctx context.Context, placement Placement) (Receipt, error) {
	f.mu.Lock()
	f.placements++
	f.mu.Unlock()
	if f.onPlace != nil {
		return f.onPlace(ctx, placement)
	}
	return Receipt{Action: placement.Action.ID(), Attempt: placement.Attempt, Snapshot: placement.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (f *environment) Observe(_ context.Context, placement Placement, current domain.GenerationSnapshot) (Evidence, error) {
	f.mu.Lock()
	f.observations++
	count := f.observations
	f.mu.Unlock()
	if f.observeError != nil {
		return Evidence{}, f.observeError
	}
	if f.onObserve != nil {
		return f.onObserve(placement, current, count), nil
	}
	return f.evidence(placement, current, domain.EffectPending, false), nil
}
func (f *environment) evidence(placement Placement, current domain.GenerationSnapshot, effect domain.Effect, complete bool) Evidence {
	now := f.clock.Now()
	evidence := Evidence{Observation: domain.Observation{Action: placement.Action.ID(), Attempt: placement.Attempt, Snapshot: current, Tick: placement.Tick + 1, Effect: effect}, StartedAt: now, ObservedAt: now, Complete: complete}
	if effect == domain.EffectCompleted {
		building, _ := placement.Action.Building()
		evidence.Built = domain.Known(building)
	}
	return evidence
}
func (f *environment) counts() (int, int, int) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.inspections, f.placements, f.observations
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
	ctx := context.Background()
	clock := testkit.NewManualClock(time.Date(2026, 9, 10, 12, 0, 0, 0, time.UTC))
	path := filepath.Join(t.TempDir(), "state.sqlite")
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
	env := &environment{clock: clock, stock: 20, tick: 100}
	executor, err := New(journal, env, clock, Limits{MaxAge: time.Second, RunTimeout: 2 * time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	authority := Authority{Enabled: true, Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: plan.Revision(), Native: 1}}
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

func TestPartialInspectionCannotReleaseConstructionAccounting(t *testing.T) {
	f := newFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	f.env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
		evidence := f.env.evidence(p, g, domain.EffectPending, false)
		evidence.Observation.Causality = domain.AfterDispatch
		evidence.Observation.ConstructionObserved = true
		return evidence
	}
	if _, err := f.run(); !errors.Is(err, ErrEvidence) {
		t.Fatal(err)
	}
	if _, known := f.progress(t).ConstructionObserved.Value(); known {
		t.Fatal("partial proof was journaled")
	}
}

func TestDurableDispatchThenObservedConstruction(t *testing.T) {
	f := newFixture(t)
	f.env.onPlace = func(_ context.Context, p Placement) (Receipt, error) {
		view := f.progress(t)
		if view.Stage != domain.Dispatched || !view.Unresolved || view.Attempt != p.Attempt || view.Attempt != 1 {
			t.Fatalf("native call preceded durable dispatch: %+v", view)
		}
		return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
	}
	result, err := f.run()
	if err != nil {
		t.Fatal(err)
	}
	if !result.NativeCalled || result.Progress.View().Stage != domain.AwaitingObservation || !result.Progress.View().Unresolved {
		t.Fatal("receipt completed construction")
	}
	result, err = f.run()
	if err != nil {
		t.Fatal(err)
	}
	if result.NativeCalled || !result.Progress.View().Unresolved {
		t.Fatal("partial construction was completed or reissued")
	}
	f.env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
		return f.env.evidence(p, g, domain.EffectCompleted, true)
	}
	result, err = f.run()
	if err != nil {
		t.Fatal(err)
	}
	if result.Progress.View().Stage != domain.Completed || result.Progress.View().Unresolved {
		t.Fatal("completed native building not reconciled")
	}
	if _, calls, _ := f.env.counts(); calls != 1 {
		t.Fatal("duplicate placement")
	}
}

func TestResourcesLostDuringPreparationHoldPrepared(t *testing.T) {
	f := newFixture(t)
	f.env.onInspect = func(n int, in Inspection) Inspection {
		if n == 2 {
			in.Stock.Values[0].Available = domain.Known(int64(0))
		}
		return in
	}
	result, err := f.run()
	if !errors.Is(err, ErrHeld) || result.NativeCalled || f.progress(t).Stage != domain.Prepared {
		t.Fatalf("resource loss dispatched: %+v %v", result, err)
	}
	f.env.onInspect = nil
	result, err = f.run()
	if err != nil || !result.NativeCalled {
		t.Fatalf("fresh Prepared revalidation failed: %v", err)
	}
}

func TestInvalidationAndFreshnessPreventNativeWrites(t *testing.T) {
	for _, change := range []func(*Authority){func(a *Authority) { a.Enabled = false }, func(a *Authority) { a.Snapshot.Load = "new-load" }, func(a *Authority) { a.Snapshot.Map++ }, func(a *Authority) { a.Snapshot.Direction++ }, func(a *Authority) { a.Snapshot.Revision++ }} {
		f := newFixture(t)
		f.env.onInspect = func(_ int, in Inspection) Inspection {
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
		if _, calls, _ := f.env.counts(); calls != 0 {
			t.Fatal("native write under stale authority")
		}
	}
	for _, change := range []func(*Inspection){func(i *Inspection) { i.StartedAt = i.StartedAt.Add(-2 * time.Second) }, func(i *Inspection) { i.ObservedAt = i.ObservedAt.Add(time.Second) }, func(i *Inspection) { i.Current.Load = "other" }, func(i *Inspection) { i.Preview.SafeToPlace = domain.Unknown[bool]() }} {
		f := newFixture(t)
		f.env.onInspect = func(_ int, in Inspection) Inspection { change(&in); return in }
		if result, err := f.run(); err == nil || result.NativeCalled {
			t.Fatal("stale/unknown facts dispatched")
		}
	}
}

type hookedJournal struct {
	Journal
	afterDispatch func()
	receiptError  error
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
func (j *hookedJournal) RecordReceipt(ctx context.Context, p domain.PlanID, a domain.ActionID, attempt domain.AttemptID, receipt domain.Receipt) (domain.Progress, error) {
	if j.receiptError != nil {
		return domain.Progress{}, j.receiptError
	}
	return j.Journal.RecordReceipt(ctx, p, a, attempt, receipt)
}

func TestAuthorityChangeAfterDurableDispatchKeepsUncertainty(t *testing.T) {
	f := newFixture(t)
	hook := &hookedJournal{Journal: f.store}
	f.executor.journal = hook
	hook.afterDispatch = func() { next := f.authority; next.Enabled = false; _ = f.executor.UpdateAuthority(next) }
	result, err := f.run()
	if err == nil || result.NativeCalled {
		t.Fatal("invalidation after journal invoked native")
	}
	view := f.progress(t)
	if !view.Unresolved || view.Attempt != 1 {
		t.Fatal("dispatch uncertainty erased")
	}
	if receipt, known := view.Receipt.Value(); !known || receipt != domain.ReceiptUnknown {
		t.Fatal("uncertain receipt not journaled")
	}
	f.env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
		return f.env.evidence(p, g, domain.EffectAbsent, true)
	}
	if _, err = f.run(); err != nil {
		t.Fatal("manual authority could not reconcile", err)
	}
	if f.progress(t).Unresolved {
		t.Fatal("complete absence did not reconcile")
	}
}

func TestCancelledNativeCallRetainsAttempt(t *testing.T) {
	f := newFixture(t)
	ctx, cancel := context.WithCancel(context.Background())
	f.env.onPlace = func(ctx context.Context, _ Placement) (Receipt, error) { cancel(); return Receipt{}, ctx.Err() }
	result, err := f.executor.Run(ctx, f.plan.ID(), f.action.ID())
	if !errors.Is(err, context.Canceled) || !result.NativeCalled || !f.progress(t).Unresolved {
		t.Fatal("canceled dispatch lost attempt", err)
	}
	f.env.onPlace = nil
	if _, err = f.run(); err != nil {
		t.Fatal(err)
	}
	if _, calls, observations := f.env.counts(); calls != 1 || observations != 1 {
		t.Fatal("next invocation retried instead of observing")
	}
}

func TestRestartAfterLostReplyAndReceiptJournalFailure(t *testing.T) {
	f := newFixture(t)
	failure := errors.New("receipt store unavailable")
	f.executor.journal = &hookedJournal{Journal: f.store, receiptError: failure}
	f.env.onPlace = func(context.Context, Placement) (Receipt, error) { return Receipt{}, errors.New("reply lost") }
	if _, err := f.run(); !errors.Is(err, failure) {
		t.Fatal("persistence failure hidden", err)
	}
	if view := f.progress(t); view.Stage != domain.Dispatched || !view.Unresolved {
		t.Fatal("durable dispatch lost")
	}
	if err := f.store.Close(); err != nil {
		t.Fatal(err)
	}
	reopened, err := store.Open(context.Background(), f.path)
	if err != nil {
		t.Fatal(err)
	}
	defer reopened.Close()
	f.store = reopened
	restarted, err := New(reopened, f.env, f.clock, f.executor.limits)
	if err != nil {
		t.Fatal(err)
	}
	f.executor = restarted
	_ = restarted.UpdateAuthority(f.authority)
	f.env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
		return f.env.evidence(p, g, domain.EffectPending, false)
	}
	if _, err = f.run(); err != nil {
		t.Fatal(err)
	}
	if _, calls, _ := f.env.counts(); calls != 1 {
		t.Fatal("restart retried uncertain write")
	}
}

func TestOnlyCompleteCorrelatedAbsencePermitsLaterRetry(t *testing.T) {
	f := newFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	f.env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
		return f.env.evidence(p, g, domain.EffectAbsent, false)
	}
	if _, err := f.run(); !errors.Is(err, ErrEvidence) {
		t.Fatal("partial absence accepted", err)
	}
	f.env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
		e := f.env.evidence(p, g, domain.EffectCompleted, true)
		e.Observation.Attempt++
		return e
	}
	if _, err := f.run(); !errors.Is(err, ErrEvidence) {
		t.Fatal("uncorrelated completion accepted", err)
	}
	f.env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
		return f.env.evidence(p, g, domain.EffectAbsent, true)
	}
	result, err := f.run()
	if err != nil || result.NativeCalled || result.Progress.View().Stage != domain.Pending {
		t.Fatal("complete absence not rearmed", err)
	}
	f.env.tick = 102
	result, err = f.run()
	if err != nil || !result.NativeCalled || result.Progress.View().Attempt != 2 {
		t.Fatal("later authorized retry missing fresh attempt", err)
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
	if view := f.progress(t); view.Stage != domain.Cancelled || !view.Unresolved {
		t.Fatal("cancel erased dispatched uncertainty")
	}
	if _, calls, _ := f.env.counts(); calls != 1 {
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
	if _, calls, _ := f.env.counts(); calls != 0 {
		t.Fatal("write after dispatch persistence failure")
	}
}

func TestTransientObservationFailureAndLoadChangeStayUnresolved(t *testing.T) {
	f := newFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	failure := errors.New("read transport failed")
	f.env.observeError = failure
	if _, err := f.run(); !errors.Is(err, failure) {
		t.Fatal("read failure hidden", err)
	}
	if !f.progress(t).Unresolved {
		t.Fatal("read failure became absence")
	}
	f.env.observeError = nil
	next := f.authority
	next.Snapshot.Load = "other-load"
	_ = f.executor.UpdateAuthority(next)
	if _, err := f.run(); !errors.Is(err, ErrAuthority) {
		t.Fatal("cross-load effect attributed", err)
	}
	if _, calls, observations := f.env.counts(); calls != 1 || observations != 1 {
		t.Fatal("cross-load retry/read dispatched")
	}
}

func TestCompletedProofMustMatchResolvedBuilding(t *testing.T) {
	f := newFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	f.env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
		evidence := f.env.evidence(p, g, domain.EffectCompleted, true)
		wrong, err := domain.NewBuilding("Wall", domain.Cell{X: 11, Z: 10}, domain.North, "WoodLog")
		if err != nil {
			t.Fatal(err)
		}
		evidence.Built = domain.Known(wrong)
		return evidence
	}
	if _, err := f.run(); !errors.Is(err, ErrEvidence) {
		t.Fatal("wrong building completed action", err)
	}
	if !f.progress(t).Unresolved {
		t.Fatal("wrong completion cleared attempt")
	}
}

func TestCompetingReservationAndResourceReserveHoldDispatch(t *testing.T) {
	for _, reserve := range []bool{false, true} {
		f := newFixture(t)
		f.env.onInspect = func(_ int, in Inspection) Inspection {
			if reserve {
				in.Rules = []policy.ResourceRule{{Resource: "WoodLog", Reserve: 15, Spending: policy.Allow}}
				return in
			}
			building, _ := domain.NewBuilding("Wall", domain.Cell{X: 20, Z: 20}, domain.North, "WoodLog")
			action, _ := domain.NewBuildingAction("other-action", building)
			plan, _ := domain.NewPlan("other-plan", 1, []domain.Action{action})
			progress, _ := domain.NewProgress(plan, action.ID())
			snapshot := in.Current
			snapshot.Plan = plan.ID()
			in.Held = []policy.Reservation{{Action: action, Progress: progress, Snapshot: snapshot, Costs: []policy.Amount{{Resource: "WoodLog", Count: 15}}, Footprint: []domain.Cell{building.Cell()}}}
			return in
		}
		if result, err := f.run(); !errors.Is(err, ErrHeld) || result.NativeCalled {
			t.Fatal("reserved resources spent", err)
		}
	}
}

func TestManualRoundTripStillInvalidatesActiveGeneration(t *testing.T) {
	f := newFixture(t)
	f.env.onInspect = func(_ int, in Inspection) Inspection {
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

func TestDispatchDelayCannotUseExpiredPreview(t *testing.T) {
	f := newFixture(t)
	f.executor.journal = &hookedJournal{Journal: f.store, afterDispatch: func() { f.clock.Advance(2 * time.Second) }}
	result, err := f.run()
	if !errors.Is(err, ErrHeld) || result.NativeCalled {
		t.Fatal("expired preview dispatched after slow journal", err)
	}
	if view := f.progress(t); !view.Unresolved || view.Attempt != 1 {
		t.Fatal("slow dispatch erased durable attempt")
	}
	if _, calls, _ := f.env.counts(); calls != 0 {
		t.Fatal("expired facts reached native")
	}
}
