package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func foodDeficitRoutineRequest() RoutineReviewRequest {
	r := routineRequest()
	r.Current.Native = 2
	r.Facts.PopulationFoodDays = domain.Known(0.0)
	r.Facts.GrowingCells = domain.Known(int64(0))
	r.Facts.FieldCoverage = domain.Known(1.0)
	return r
}

func acquisitionPlan(t *testing.T, id domain.PlanID, thing string) domain.PlanSpec {
	t.Helper()
	acq, err := domain.NewAcquisition("acq-"+thing, thing, domain.Cell{X: 1, Z: 1})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewAcquisitionAction(domain.ActionID(string(id)+"-a"), acq)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

// Guards the acquisition-planner exception: a goal's already-dispatched,
// still-unresolved production bill must not block committing a fresh
// acquisition method for the same goal, since the bill may be waiting on
// exactly the ingredient the acquisition will fetch.
func TestCommitAcquisitionMethodExemptFromBillOpenWork(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := foodDeficitRoutineRequest()
	tick := r.Tick
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodSupply)
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
	g, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "cook", billPlan)
	if err != nil {
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
	g, err = s.LoadGoal(ctx, g.Goal.ID)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "acquire", acquisitionPlan(t, "acquire-plan", "WoodLog")); err != nil {
		t.Fatal("bill-only open work blocked acquisition commit", err)
	}
}

// The exemption is narrow: it only excludes ProductionBillAction progress.
// Any other still-open work for the goal, including another dispatched
// acquisition action, must still block committing a further acquisition
// method, exactly like it blocks every other family.
func TestCommitAcquisitionMethodNotExemptFromNonBillOpenWork(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := foodDeficitRoutineRequest()
	tick := r.Tick
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodSupply)
	if g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
	}
	first := acquisitionPlan(t, "acquire-plan-1", "WoodLog")
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "acquire-1", first); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "acquire-plan-1", 1
	admission := AcquisitionAdmission{Snapshot: target, Tick: tick, Thing: "acq-WoodLog", SnapshotToken: "acq-cas"}
	if _, err := s.PrepareAcquisition(ctx, "acquire-plan-1", domain.ActionID("acquire-plan-1-a"), admission); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "acquire-plan-1", domain.ActionID("acquire-plan-1-a"), target, tick); err != nil {
		t.Fatal(err)
	}
	g, err := s.LoadGoal(ctx, g.Goal.ID)
	if err != nil {
		t.Fatal(err)
	}
	second := acquisitionPlan(t, "acquire-plan-2", "Steel")
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "acquire-2", second); err == nil {
		t.Fatal("open non-bill work did not block a second acquisition method")
	}
}
