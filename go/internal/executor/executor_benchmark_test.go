package executor

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
)

// These benchmarks measure Executor.Run and its real policy/domain guards, not
// SQLite durability, transport encoding, native work or end-to-end latency.
// A single-action memory journal retains accounting and executes real domain
// transitions. Each inspection includes current, complete empty emergency facts
// and the real emergency gate. Reset and outcome checks are not timed.
type schedulingJournal struct {
	plan      domain.PlanSpec
	action    domain.Action
	progress  domain.Progress
	admission []store.ActionAdmission
	scope     domain.GenerationSnapshot
}

func (j *schedulingJournal) check(ctx context.Context, plan domain.PlanID, action domain.ActionID) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if plan != j.plan.ID() || action != j.action.ID() {
		return ErrEvidence
	}
	return nil
}
func (j *schedulingJournal) LoadPlan(ctx context.Context, plan domain.PlanID) (store.PlanState, error) {
	if err := j.check(ctx, plan, j.action.ID()); err != nil {
		return store.PlanState{}, err
	}
	return store.PlanState{Spec: j.plan, Progress: []domain.Progress{j.progress}, Admissions: append([]store.ActionAdmission(nil), j.admission...)}, nil
}
func (j *schedulingJournal) Prepare(ctx context.Context, plan domain.PlanID, action domain.ActionID, g domain.GenerationSnapshot, tick domain.Tick) (domain.Progress, error) {
	if err := j.check(ctx, plan, action); err != nil {
		return domain.Progress{}, err
	}
	if j.progress.View().Stage == domain.Pending {
		p, err := j.progress.Prepare(g, tick)
		if err != nil {
			return domain.Progress{}, err
		}
		j.progress = p
	}
	return j.progress, nil
}
func (j *schedulingJournal) Dispatch(ctx context.Context, plan domain.PlanID, action domain.ActionID, g domain.GenerationSnapshot, tick domain.Tick) (domain.Progress, error) {
	if err := j.check(ctx, plan, action); err != nil {
		return domain.Progress{}, err
	}
	if len(j.admission) != 1 || j.admission[0].Admission.Tick > tick {
		return domain.Progress{}, ErrEvidence
	}
	p, err := j.progress.MarkDispatched(g, tick)
	if err == nil {
		j.progress = p
	}
	return p, err
}
func (j *schedulingJournal) RecordReceipt(ctx context.Context, plan domain.PlanID, action domain.ActionID, attempt domain.AttemptID, receipt domain.Receipt) (domain.Progress, error) {
	if err := j.check(ctx, plan, action); err != nil {
		return domain.Progress{}, err
	}
	p, err := j.progress.RecordReceipt(attempt, receipt)
	if err == nil {
		j.progress = p
	}
	return p, err
}
func (j *schedulingJournal) RecordZoneReceipt(ctx context.Context, plan domain.PlanID, action domain.ActionID, attempt domain.AttemptID, zone string) (domain.Progress, error) {
	if err := j.check(ctx, plan, action); err != nil {
		return domain.Progress{}, err
	}
	p, err := j.progress.RecordZoneReceipt(attempt, domain.ReceiptAccepted, zone)
	if err == nil {
		j.progress = p
	}
	return p, err
}
func (j *schedulingJournal) Observe(ctx context.Context, plan domain.PlanID, o domain.Observation, g domain.GenerationSnapshot) (domain.Progress, error) {
	if err := j.check(ctx, plan, o.Action); err != nil {
		return domain.Progress{}, err
	}
	p, err := j.progress.Observe(o, g)
	if err == nil {
		j.progress = p
	}
	return p, err
}
func (j *schedulingJournal) Cancel(ctx context.Context, plan domain.PlanID, action domain.ActionID) (domain.Progress, error) {
	if err := j.check(ctx, plan, action); err != nil {
		return domain.Progress{}, err
	}
	p, err := j.progress.Cancel()
	if err == nil {
		j.progress = p
	}
	return p, err
}
func (j *schedulingJournal) PrepareBatch(ctx context.Context, attempts []store.BatchAttempt) ([]store.BatchResult, error) {
	return eachAttempt(attempts, func(a store.BatchAttempt) (domain.Progress, error) {
		return j.Prepare(ctx, a.Plan, a.Action, a.Snapshot, a.Tick)
	}), ctx.Err()
}
func (j *schedulingJournal) DispatchBatch(ctx context.Context, attempts []store.BatchAttempt) ([]store.BatchResult, error) {
	return eachAttempt(attempts, func(a store.BatchAttempt) (domain.Progress, error) {
		return j.Dispatch(ctx, a.Plan, a.Action, a.Snapshot, a.Tick)
	}), ctx.Err()
}
func (j *schedulingJournal) RecordReceipts(ctx context.Context, receipts []store.BatchReceipt) ([]store.BatchResult, error) {
	out := make([]store.BatchResult, len(receipts))
	for i, r := range receipts {
		out[i].Progress, out[i].Err = j.RecordReceipt(ctx, r.Plan, r.Action, r.Attempt, r.Receipt)
	}
	return out, ctx.Err()
}
func eachAttempt(attempts []store.BatchAttempt, fn func(store.BatchAttempt) (domain.Progress, error)) []store.BatchResult {
	out := make([]store.BatchResult, len(attempts))
	for i, a := range attempts {
		out[i].Progress, out[i].Err = fn(a)
	}
	return out
}
func (j *schedulingJournal) Hold(ctx context.Context, plan domain.PlanID, action domain.ActionID, reasons []domain.HeldReason, tick domain.Tick) (domain.Progress, error) {
	if err := j.check(ctx, plan, action); err != nil {
		return domain.Progress{}, err
	}
	p, err := j.progress.Hold(reasons, tick)
	if err == nil {
		j.progress = p
	}
	return p, err
}

func BenchmarkExecutorScheduling(b *testing.B) {
	for _, scenario := range []string{"DispatchApplied"} {
		b.Run(scenario, func(b *testing.B) {
			b.StopTimer()
			ctx := context.Background()
			clock := testkit.NewManualClock(time.Date(2026, 9, 10, 12, 0, 0, 0, time.UTC))
			building, err := domain.NewBuilding("Wall", domain.Cell{X: 10, Z: 10}, domain.North, "WoodLog")
			if err != nil {
				b.Fatal(err)
			}
			action, err := domain.NewBuildingAction("action", building)
			if err != nil {
				b.Fatal(err)
			}
			plan, err := domain.NewPlan("plan", 1, []domain.Action{action})
			if err != nil {
				b.Fatal(err)
			}
			pending, err := domain.NewProgress(plan, action.ID())
			if err != nil {
				b.Fatal(err)
			}
			scope := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: plan.ID(), Revision: 1, Native: 1}
			journal := &schedulingJournal{plan: plan, action: action, progress: pending, scope: scope}
			env := &environment{clock: clock, stock: 20, tick: 100}
			executor, err := New(journal, env, clock, Limits{MaxAge: time.Second, RunTimeout: 2 * time.Second, JournalTimeout: 5 * time.Second})
			if err != nil {
				b.Fatal(err)
			}
			b.Cleanup(func() {
				if err := executor.Stop(ctx); err != nil {
					b.Fatal(err)
				}
			})
			if err = executor.UpdateAuthority(Authority{Snapshot: scope, Enabled: true}); err != nil {
				b.Fatal(err)
			}
			initial := pending
			b.ReportAllocs()
			b.ResetTimer()
			for i := 0; i < b.N; i++ {
				journal.progress = initial
				env.inspections, env.placements = 0, 0
				b.StartTimer()
				result, err := executor.runOne(ctx, plan.ID(), action.ID())
				b.StopTimer()
				if err != nil {
					b.Fatal(err)
				}
				if result.Progress.View().Stage != domain.Completed || !result.NativeCalled || result.Progress.View().Unresolved {
					b.Fatal("unexpected path", result)
				}
				if x, y := env.counts(); x != 1 || y != 1 {
					b.Fatalf("calls = %d/%d", x, y)
				}
			}
		})
	}
}
