package executor

import (
	"context"
	"database/sql"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func multiple(t *testing.T) (*fixture, domain.Action) {
	t.Helper()
	f := newFixture(t)
	b, err := domain.NewBuilding("Wall", domain.Cell{X: 20, Z: 20}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	target, err := domain.NewBuildingAction("target", b)
	if err != nil {
		t.Fatal(err)
	}
	b, err = domain.NewBuilding("Wall", domain.Cell{X: 21, Z: 20}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	held, err := domain.NewBuildingAction("held", b)
	if err != nil {
		t.Fatal(err)
	}
	f.plan, err = domain.NewPlan("two", 1, []domain.Action{target, held})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.store.CreatePlan(context.Background(), f.plan); err != nil {
		t.Fatal(err)
	}
	f.action = target
	f.authority.Snapshot.Plan = f.plan.ID()
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, held
}
func heldAdmission(f *fixture, a domain.Action, count int64) store.Admission {
	b, _ := a.Building()
	return store.Admission{Snapshot: f.authority.Snapshot, Tick: 100, Costs: []store.MaterialCost{{Definition: "WoodLog", Count: count}}, Footprint: []domain.Cell{b.Cell()}}
}
func reopenExecutor(t *testing.T, f *fixture) {
	t.Helper()
	if err := f.store.Close(); err != nil {
		t.Fatal(err)
	}
	journal, err := store.Open(context.Background(), f.path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = journal.Close() })
	f.store = journal
	f.executor, err = New(journal, f.env, f.clock, f.executor.limits)
	if err != nil {
		t.Fatal(err)
	}
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
}
func TestRestartRebuildsCompetingPreparedAndDispatchedHolds(t *testing.T) {
	for _, dispatch := range []bool{false, true} {
		t.Run(map[bool]string{false: "prepared", true: "dispatched"}[dispatch], func(t *testing.T) {
			f, held := multiple(t)
			ctx := context.Background()
			if _, err := f.store.ReserveAndPrepare(ctx, f.plan.ID(), held.ID(), heldAdmission(f, held, 12)); err != nil {
				t.Fatal(err)
			}
			if dispatch {
				if _, err := f.store.Dispatch(ctx, f.plan.ID(), held.ID(), f.authority.Snapshot, 100); err != nil {
					t.Fatal(err)
				}
			}
			reopenExecutor(t, f)
			result, err := f.run()
			if !errors.Is(err, ErrHeld) || result.NativeCalled || len(result.Refused) != 1 || result.Refused[0].Reason != policy.InsufficientStock {
				t.Fatal("restart omitted durable hold", result, err)
			}
		})
	}
}

// A reservation recorded as shelter work dispatches under the same class:
// the competing hold that starves a routine wall does not hold a shell's,
// and the rewritten admission keeps the purpose (#602).
func TestRecordedShelterPurposeSpendsWithoutStockAtDispatch(t *testing.T) {
	f, held := multiple(t)
	ctx := context.Background()
	if _, err := f.store.ReserveAndPrepare(ctx, f.plan.ID(), held.ID(), heldAdmission(f, held, 12)); err != nil {
		t.Fatal(err)
	}
	own := heldAdmission(f, f.action, 10)
	own.Purpose = policy.Shelter
	if _, err := f.store.ReserveAndPrepare(ctx, f.plan.ID(), f.action.ID(), own); err != nil {
		t.Fatal(err)
	}
	reopenExecutor(t, f)
	result, err := f.run()
	if err != nil || !result.NativeCalled || len(result.Refused) != 0 {
		t.Fatal("shelter wall held for stock at dispatch", result, err)
	}
	state, err := f.store.LoadPlan(ctx, f.plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	for _, record := range state.Admissions {
		if record.Action == f.action.ID() && record.Admission.Purpose != policy.Shelter {
			t.Fatal("dispatch dropped the recorded purpose", record)
		}
	}
	// A spending rule still holds a shell at dispatch.
	f, _ = multiple(t)
	own = heldAdmission(f, f.action, 10)
	own.Purpose = policy.Shelter
	if _, err := f.store.ReserveAndPrepare(ctx, f.plan.ID(), f.action.ID(), own); err != nil {
		t.Fatal(err)
	}
	f.env.onInspect = func(_ int, in Inspection) Inspection {
		in.Rules = []policy.ResourceRule{{Resource: "WoodLog", Spending: policy.Stop}}
		return in
	}
	if result, err := f.run(); !errors.Is(err, ErrHeld) || result.NativeCalled || len(result.Refused) != 1 || result.Refused[0].Reason != policy.SpendingBlocked {
		t.Fatal("spending rule ignored for shelter", result, err)
	}
}
func TestMissingProtectedAdmissionRefusesButUnadmittedPendingDoesNot(t *testing.T) {
	f, held := multiple(t)
	ctx := context.Background()
	if _, err := f.store.Prepare(ctx, f.plan.ID(), held.ID(), f.authority.Snapshot, 100); err != nil {
		t.Fatal(err)
	}
	result, err := f.run()
	if !errors.Is(err, ErrHeld) || result.NativeCalled {
		t.Fatal("missing protected accounting admitted", err)
	}
	f, _ = multiple(t)
	result, err = f.run()
	if err != nil || !result.NativeCalled {
		t.Fatal("never-admitted pending action blocked first admission", err)
	}
	f = newFixture(t)
	if _, err = f.store.Prepare(ctx, f.plan.ID(), f.action.ID(), f.authority.Snapshot, 100); err != nil {
		t.Fatal(err)
	}
	result, err = f.run()
	if !errors.Is(err, ErrHeld) || result.NativeCalled {
		t.Fatal("target Prepared without accounting dispatched", err)
	}
}
func TestPreparedRestartAndCompletedBudgetWithFreshStock(t *testing.T) {
	f, held := multiple(t)
	ctx := context.Background()
	if _, err := f.store.ReserveAndPrepare(ctx, f.plan.ID(), held.ID(), heldAdmission(f, held, 20)); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.Dispatch(ctx, f.plan.ID(), held.ID(), f.authority.Snapshot, 100); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.Observe(ctx, f.plan.ID(), domain.Observation{Action: held.ID(), Attempt: 1, Snapshot: f.authority.Snapshot, Tick: 101, Effect: domain.EffectCompleted}, f.authority.Snapshot); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.ReserveAndPrepare(ctx, f.plan.ID(), f.action.ID(), heldAdmission(f, f.action, 10)); err != nil {
		t.Fatal(err)
	}
	f.env.tick = 102
	reopenExecutor(t, f)
	result, err := f.run()
	if err != nil || !result.NativeCalled {
		t.Fatal("fresh completed stock or prepared restart blocked", err)
	}
	state, err := f.store.LoadPlan(ctx, f.plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	for _, r := range state.Admissions {
		if r.Action == f.action.ID() && r.Admission.Tick != 102 {
			t.Fatal("latest preview tick not persisted")
		}
	}
}
func TestFreshReplacementSQLFailureNeverCallsNative(t *testing.T) {
	f := newFixture(t)
	db, err := sql.Open("sqlite", f.path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if _, err = db.Exec(`CREATE TRIGGER fail_revalidation BEFORE UPDATE ON admissions BEGIN SELECT RAISE(ABORT,'injected admission update failure'); END`); err != nil {
		t.Fatal(err)
	}
	f.env.onInspect = func(n int, in Inspection) Inspection {
		if n == 2 {
			in.Preview.Costs = domain.Known([]policy.Amount{{Resource: "WoodLog", Count: 15}})
		}
		return in
	}
	result, err := f.run()
	if err == nil || result.NativeCalled {
		t.Fatal("failed reservation transaction permitted write")
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if state.Progress[0].View().Stage != domain.Prepared || state.Admissions[0].Admission.Costs[0].Count != 10 {
		t.Fatal("failed revalidation destroyed original accounting")
	}
}
func TestExternalAccountingMustBeCompleteAndOtherPlan(t *testing.T) {
	f := newFixture(t)
	f.env.onInspect = func(_ int, in Inspection) Inspection { in.ExternalHoldsComplete = false; return in }
	result, err := f.run()
	if !errors.Is(err, ErrHeld) || result.NativeCalled {
		t.Fatal("unknown external holds treated as empty")
	}
	f = newFixture(t)
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	f.env.onInspect = func(_ int, in Inspection) Inspection {
		b, _ := f.action.Building()
		in.Held = []policy.Reservation{{Action: f.action, Progress: state.Progress[0], Snapshot: f.authority.Snapshot, Costs: []policy.Amount{}, Footprint: []domain.Cell{b.Cell()}}}
		return in
	}
	result, err = f.run()
	if !errors.Is(err, ErrEvidence) || result.NativeCalled {
		t.Fatal("external source spoofed current accounting")
	}
}
