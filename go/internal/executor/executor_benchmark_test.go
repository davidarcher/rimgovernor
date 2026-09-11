package executor

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
)

// These benchmarks measure Executor.Run and its real policy/domain guards, not
// SQLite durability, transport encoding, native work or end-to-end latency.
// A single-action memory journal retains accounting and executes real domain
// transitions. Reset, fixture construction and outcome checks are not timed.
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
func (j *schedulingJournal) ReserveAndPrepare(ctx context.Context, plan domain.PlanID, action domain.ActionID, a store.Admission) (domain.Progress, error) {
	if err := j.check(ctx, plan, action); err != nil {
		return domain.Progress{}, err
	}
	building, _ := j.action.Building()
	if a.Snapshot != j.scope || a.Tick != 100 || len(a.Costs) != 1 || a.Costs[0] != (store.MaterialCost{Definition: "WoodLog", Count: 10}) || len(a.Footprint) != 1 || a.Footprint[0] != building.Cell() {
		return domain.Progress{}, ErrEvidence
	}
	v := j.progress.View()
	if v.Unresolved || a.Tick < v.Tick {
		return domain.Progress{}, ErrEvidence
	}
	switch v.Stage {
	case domain.Pending:
		p, err := j.progress.Prepare(a.Snapshot, a.Tick)
		if err != nil {
			return domain.Progress{}, err
		}
		j.progress = p
	case domain.Prepared:
		if v.Snapshot != a.Snapshot {
			return domain.Progress{}, ErrAuthority
		}
	default:
		return domain.Progress{}, ErrEvidence
	}
	a.Costs = append([]store.MaterialCost(nil), a.Costs...)
	a.Footprint = append([]domain.Cell(nil), a.Footprint...)
	j.admission = []store.ActionAdmission{{Action: action, Admission: a}}
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

func BenchmarkExecutorScheduling(b *testing.B) {
	for _, scenario := range []string{"HeldInsufficientStock", "DispatchAccepted", "ReconcilePending", "ReconcileCompleted"} {
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
			scope := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: plan.ID(), Revision: 1, Direction: 1, Native: 1}
			journal := &schedulingJournal{plan: plan, action: action, progress: pending, scope: scope}
			env := &environment{clock: clock, stock: 20, tick: 100}
			executor, err := New(journal, env, clock, Limits{MaxAge: time.Second, RunTimeout: 2 * time.Second, JournalTimeout: time.Second})
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
			var admissions []store.ActionAdmission
			expectedStage := domain.AwaitingObservation
			inspections, placements, observations := 2, 1, 0
			switch scenario {
			case "HeldInsufficientStock":
				env.stock = 0
				expectedStage = domain.Pending
				inspections, placements = 1, 0
			case "ReconcilePending", "ReconcileCompleted":
				// Prepare the observation checkpoint through the same real guarded dispatch
				// path, outside timing. Every iteration starts from this unresolved attempt.
				result, err := executor.Run(ctx, plan.ID(), action.ID())
				if err != nil || !result.NativeCalled || !result.Progress.View().Unresolved {
					b.Fatal(result, err)
				}
				initial = journal.progress
				admissions = journal.admission
				inspections, placements, observations = 0, 0, 1
				if scenario == "ReconcileCompleted" {
					expectedStage = domain.Completed
					env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
						return env.evidence(p, g, domain.EffectCompleted, true)
					}
				}
			}
			b.ReportAllocs()
			b.ResetTimer()
			for i := 0; i < b.N; i++ {
				journal.progress = initial
				journal.admission = admissions
				env.inspections, env.placements, env.observations = 0, 0, 0
				b.StartTimer()
				result, err := executor.Run(ctx, plan.ID(), action.ID())
				b.StopTimer()
				if scenario == "HeldInsufficientStock" {
					if !errors.Is(err, ErrHeld) || len(result.Refused) == 0 {
						b.Fatal(result, err)
					}
				} else if err != nil {
					b.Fatal(err)
				}
				if result.Progress.View().Stage != expectedStage || result.NativeCalled != (placements == 1) {
					b.Fatal("unexpected path", result)
				}
				unresolved := scenario == "DispatchAccepted" || scenario == "ReconcilePending"
				if result.Progress.View().Unresolved != unresolved {
					b.Fatal("unexpected uncertainty", result)
				}
				if x, y, z := env.counts(); x != inspections || y != placements || z != observations {
					b.Fatalf("calls = %d/%d/%d", x, y, z)
				}
			}
		})
	}
}
