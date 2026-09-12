package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRoutineExecutionRequiresCurrentReviewedMethod(t *testing.T) {
	for _, change := range []string{"valid", "direction", "native", "load", "revision", "unbound", "disabled", "cancelled", "unknown"} {
		t.Run(change, func(t *testing.T) {
			ctx := context.Background()
			s := open(t, filepath.Join(t.TempDir(), "routine.db"))
			r := routineRequest()
			r.Current.Direction, r.Current.Native = 1, 2
			g := routineGoal(t, reviewRoutine(t, s, &r), policy.MaintainWood)
			q := methodRequest(t, g, "method", 10)
			d, err := s.AdmitBuildingMethod(ctx, q)
			if err != nil || !d.Admitted {
				t.Fatal(d, err)
			}
			root, target := r.Current, q.Current
			switch change {
			case "direction":
				target.Direction++
			case "native":
				target.Native++
			case "load":
				target.Load = "other"
			case "revision":
				target.Revision++
			case "unbound":
				target.Plan = "other"
			case "disabled":
				r.Enabled = false
				reviewRoutine(t, s, &r)
			case "cancelled":
				if _, err = s.CancelGoal(ctx, g.Goal.ID, d.Goal.Revision); err != nil {
					t.Fatal(err)
				}
			case "unknown":
				r.Facts.Wood = domain.Unknown[int64]()
				reviewRoutine(t, s, &r)
			}
			err = s.AuthorizeRoutinePlan(ctx, root, target)
			if (err == nil) != (change == "valid") {
				t.Fatal(change, err)
			}
		})
	}
}

// Guards the narrow NeedRecovered exception: a bill goal whose setup gate has
// recovered may still authorize its already-dispatched, still-unresolved bill
// output, since that pending pawn time is what the recovered gate reflects.
// It must never permit a fresh setup write once the gate has recovered.
func TestRoutineExecutionRecoveredBillNeedPermitsPendingOutputOnly(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := routineRequest()
	r.Current.Native = 2
	r.Facts.Cooking = domain.Known(false)
	tick := r.Tick
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureCooking)
	if g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
	}
	bill, err := domain.NewProductionBill("bench", "recipe", "bench-cas", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewProductionBillAction("bill", bill)
	if err != nil {
		t.Fatal(err)
	}
	billPlan, err := domain.NewPlan("bill-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "cook", billPlan); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "bill-plan", 1
	admission := BillAdmission{Snapshot: target, Tick: tick, Bench: "bench", SnapshotToken: "bench-cas"}
	if _, err = s.PrepareBill(ctx, "bill-plan", "bill", admission); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "bill-plan", "bill", target, tick); err != nil {
		t.Fatal(err)
	}
	r.Facts.Cooking = domain.Known(true)
	out = reviewRoutine(t, s, &r)
	g = routineGoal(t, out, policy.EnsureCooking)
	if g.Goal.Need != domain.NeedRecovered || g.Goal.Status != domain.GoalActive {
		t.Fatal(g)
	}
	if err = s.AuthorizeRoutinePlan(ctx, r.Current, target); err != nil {
		t.Fatal("recovered need with unresolved bill output was not authorized", err)
	}
}

// Once the dispatched bill's output is fully resolved, the recovered gate
// leaves nothing pending: the goal settles to Satisfied rather than staying
// Active, and authorization must refuse it like any other satisfied goal.
func TestRoutineExecutionRecoveredBillNeedRefusesOnceResolved(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := routineRequest()
	r.Current.Native = 2
	r.Facts.Cooking = domain.Known(false)
	tick := r.Tick
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureCooking)
	bill, err := domain.NewProductionBill("bench", "recipe", "bench-cas", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewProductionBillAction("bill", bill)
	if err != nil {
		t.Fatal(err)
	}
	billPlan, err := domain.NewPlan("bill-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "cook", billPlan); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "bill-plan", 1
	admission := BillAdmission{Snapshot: target, Tick: tick, Bench: "bench", SnapshotToken: "bench-cas"}
	if _, err = s.PrepareBill(ctx, "bill-plan", "bill", admission); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "bill-plan", "bill", target, tick); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordReceipt(ctx, "bill-plan", "bill", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	observation := domain.Observation{Action: "bill", Attempt: 1, Snapshot: target, Tick: tick + 1, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}
	if _, err = s.Observe(ctx, "bill-plan", observation, target); err != nil {
		t.Fatal(err)
	}
	r.Facts.Cooking = domain.Known(true)
	out = reviewRoutine(t, s, &r)
	g = routineGoal(t, out, policy.EnsureCooking)
	if g.Goal.Need != domain.NeedRecovered || g.Goal.Status != domain.GoalSatisfied {
		t.Fatal(g)
	}
	if err = s.AuthorizeRoutinePlan(ctx, r.Current, target); err == nil {
		t.Fatal("resolved bill output authorized a satisfied goal")
	}
}

// A recovered gate never authorizes a fresh setup write: if any bill action in
// the bound plan was never dispatched, authorization must refuse the whole
// plan even though a sibling action still has genuinely pending output.
func TestRoutineExecutionRecoveredBillNeedRefusesUndispatchedSibling(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := routineRequest()
	r.Current.Native = 2
	r.Facts.Cooking = domain.Known(false)
	tick := r.Tick
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureCooking)
	bill1, err := domain.NewProductionBill("bench1", "recipe1", "bench1-cas", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	bill2, err := domain.NewProductionBill("bench2", "recipe2", "bench2-cas", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	a1, err := domain.NewProductionBillAction("bill1", bill1)
	if err != nil {
		t.Fatal(err)
	}
	a2, err := domain.NewProductionBillAction("bill2", bill2)
	if err != nil {
		t.Fatal(err)
	}
	billPlan, err := domain.NewPlan("bill-plan", 1, []domain.Action{a1, a2})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "cook", billPlan); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "bill-plan", 1
	admission := BillAdmission{Snapshot: target, Tick: tick, Bench: "bench1", SnapshotToken: "bench1-cas"}
	if _, err = s.PrepareBill(ctx, "bill-plan", "bill1", admission); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "bill-plan", "bill1", target, tick); err != nil {
		t.Fatal(err)
	}
	// bill2 is deliberately left Pending: never prepared or dispatched.
	r.Facts.Cooking = domain.Known(true)
	out = reviewRoutine(t, s, &r)
	g = routineGoal(t, out, policy.EnsureCooking)
	if g.Goal.Need != domain.NeedRecovered || g.Goal.Status != domain.GoalActive {
		t.Fatal(g)
	}
	if err = s.AuthorizeRoutinePlan(ctx, r.Current, target); err == nil {
		t.Fatal("recovered need authorized a plan with an undispatched bill action")
	}
}
