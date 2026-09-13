package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func foodStorageDeficitRoutineRequest() RoutineReviewRequest {
	r := routineRequest()
	r.Current.Native = 2
	r.Facts.FoodStorage = domain.Known(false)
	return r
}

func stockpilePlan(t *testing.T, id domain.PlanID, cells ...[]domain.Cell) domain.PlanSpec {
	t.Helper()
	var actions []domain.Action
	for i, block := range cells {
		zone, err := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, block)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewZoneCreateAction(domain.ActionID(string(id)+"-"+string(rune('a'+i))), zone)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

func growingPlan(t *testing.T, id domain.PlanID, cells []domain.Cell) domain.PlanSpec {
	t.Helper()
	zone, err := domain.NewZoneCreate(domain.GrowingZone, "Plant_Rice", cells)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewZoneCreateAction(domain.ActionID(string(id)+"-a"), zone)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

func TestCommitStockpileZoneMethodBindsToFoodStorageGoal(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := foodStorageDeficitRoutineRequest()
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodStorage)
	if g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
	}
	plan := stockpilePlan(t, "storage-plan", []domain.Cell{{X: 4, Z: 6}, {X: 5, Z: 6}, {X: 6, Z: 6}})
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "food-storage", plan); err != nil {
		t.Fatal(err)
	}
}

// A stockpile zone is created once per colony in this slice: a plan proposing
// more than one stockpile action for the bound goal must be refused, unlike
// growing-field plans which may batch many patches per method.
func TestCommitStockpileZoneMethodCappedAtOneAction(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := foodStorageDeficitRoutineRequest()
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodStorage)
	plan := stockpilePlan(t, "storage-plan", []domain.Cell{{X: 4, Z: 6}}, []domain.Cell{{X: 8, Z: 6}})
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "food-storage", plan); err == nil {
		t.Fatal("expected rejection of multi-action stockpile plan")
	}
}

// A goal bound only to EnsureFoodStorage must not admit a growing-zone plan:
// admitZoneMethod requires the plan's own zone kind to match the need the
// goal was actually bound under, not just any zone-create action family.
func TestCommitZoneMethodRejectsKindGoalMismatch(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := foodStorageDeficitRoutineRequest()
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodStorage)
	plan := growingPlan(t, "fields-plan", []domain.Cell{{X: 4, Z: 6}, {X: 5, Z: 6}})
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "fields", plan); err == nil {
		t.Fatal("expected rejection of growing zone bound to food storage goal")
	}
}

// The allow-list (NothingPreset) stockpile variant persists and reloads its
// definition allow-list exactly, exercising insertAction/scanAction's new
// zonePayload.Allow field the same way the food-preset plans above exercise
// the rest of zonePayload.
func TestCommitAllowListStockpileZoneMethodRoundTrips(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := foodStorageDeficitRoutineRequest()
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodStorage)
	zone, err := domain.NewAllowListStockpileZone(domain.ImportantPriority, []string{"MealSimple", "MealFine"}, []domain.Cell{{X: 4, Z: 6}, {X: 5, Z: 6}, {X: 6, Z: 6}, {X: 4, Z: 7}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewZoneCreateAction("storage-plan-a", zone)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("storage-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "food-storage", plan); err != nil {
		t.Fatal(err)
	}
	loaded, err := s.LoadPlan(ctx, "storage-plan")
	if err != nil {
		t.Fatal(err)
	}
	if len(loaded.Spec.Actions()) != 1 {
		t.Fatal("expected one reloaded action", loaded.Spec)
	}
	got, ok := loaded.Spec.Actions()[0].ZoneCreate()
	if !ok || got != zone {
		t.Fatal("allow-list zone did not round-trip", got, zone)
	}
}

func TestCommitStockpileZoneMethodRejectsOverlappingCells(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := foodStorageDeficitRoutineRequest()
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodStorage)
	zone1, err := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, []domain.Cell{{X: 4, Z: 6}})
	if err != nil {
		t.Fatal(err)
	}
	zone2, err := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, []domain.Cell{{X: 4, Z: 6}})
	if err != nil {
		t.Fatal(err)
	}
	a1, err := domain.NewZoneCreateAction("storage-plan-a", zone1)
	if err != nil {
		t.Fatal(err)
	}
	a2, err := domain.NewZoneCreateAction("storage-plan-b", zone2)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("storage-plan", 1, []domain.Action{a1, a2})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "food-storage", plan); err == nil {
		t.Fatal("expected rejection of overlapping stockpile cells")
	}
}
