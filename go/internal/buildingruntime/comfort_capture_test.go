package buildingruntime

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestComfortUseAllowanceRetainsRetiredMethodAndExpires(t *testing.T) {
	ctx := context.Background()
	planner, db, session, _, _ := sleepingFixture(t)
	current := session.State().Snapshot
	goal, err := domain.NewGoal("comfort-history", domain.AutopilotGoal, 4, current, 7)
	if err != nil {
		t.Fatal(err)
	}
	err = db.CreateGoal(ctx, goal)
	if err != nil {
		t.Fatal(err)
	}
	g, err := db.LoadGoal(ctx, goal.ID)
	if err != nil {
		t.Fatal(err)
	}
	g, err = db.ReviewGoal(ctx, goal.ID, g.Revision, current, 7, domain.NeedDeficit, false)
	if err != nil {
		t.Fatal(err)
	}
	b, err := domain.NewBuilding("DiningChair", domain.Cell{X: 30, Z: 30}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("chair", b)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("comfort-history-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.CommitGoalMethod(ctx, goal.ID, g.Revision, "comfort-DiningChair", spec); err != nil {
		t.Fatal(err)
	}
	scope := current
	scope.Plan, scope.Revision = spec.ID(), spec.Revision()
	if _, err = db.ReserveAndPrepare(ctx, spec.ID(), a.ID(), store.Admission{Snapshot: scope, Tick: 7, Costs: []store.MaterialCost{}, Footprint: []domain.Cell{b.Cell()}}); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Dispatch(ctx, spec.ID(), a.ID(), scope, 7); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Observe(ctx, spec.ID(), domain.Observation{Action: a.ID(), Attempt: 1, Snapshot: current, Tick: 7, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, current); err != nil {
		t.Fatal(err)
	}
	if _, err = planner.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	g, err = db.LoadGoal(ctx, goal.ID)
	if err != nil || len(g.Methods) != 0 {
		t.Fatal(g, err)
	}
	plan, err := db.LoadPlan(ctx, spec.ID())
	if err != nil || !plan.Retired {
		t.Fatal(plan, err)
	}
	for _, row := range []struct {
		tick domain.Tick
		want uint32
	}{{7, 120}, {10006, 1}, {10007, 0}} {
		got, err := comfortUseAllowance(ctx, db, g.Goal, current, row.tick)
		if err != nil || got != row.want {
			t.Fatal(row, got, err)
		}
	}
	changed := current
	changed.Direction++
	if got, err := comfortUseAllowance(ctx, db, g.Goal, changed, 7); err != nil || got != 0 {
		t.Fatal("changed direction inherited allowance", got, err)
	}
	renewed := g.Goal
	renewed.Epoch++
	if got, err := comfortUseAllowance(ctx, db, renewed, current, 7); err != nil || got != 0 {
		t.Fatal("renewed deficit inherited allowance", got, err)
	}
}

func TestNativeComfortCompletionBudgetCapture(t *testing.T) {
	source := os.Getenv("RIMGOVERNOR_COMFORT_DB")
	if source == "" {
		t.Skip("native comfort database not supplied")
	}
	data, err := os.ReadFile(source)
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "comfort.sqlite")
	if err = os.WriteFile(path, data, 0600); err != nil {
		t.Fatal(err)
	}
	db, err := store.Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	review, err := db.LoadRoutineReview(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	var ticks uint32
	for _, binding := range review.Goals {
		if binding.Need != policy.EnsureComfort {
			continue
		}
		goal, err := db.LoadGoal(context.Background(), binding.Goal)
		if err != nil {
			t.Fatal(err)
		}
		ticks, err = comfortUseAllowance(context.Background(), db, goal.Goal, review.Snapshot, review.Tick)
		if err != nil {
			t.Fatal(err)
		}
	}
	if ticks == 0 {
		t.Fatal("captured observed furniture completion supplied no native-use interval")
	}
}
