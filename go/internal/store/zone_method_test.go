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

// A dispatched acquisition batch is refreshed every review and must not
// starve the field planner: a growing-zone method commits alongside open
// acquisition work, while a second field batch still waits for the first.
func TestCommitFieldMethodExemptFromAcquisitionOpenWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := foodDeficitRoutineRequest()
	tick := r.Tick
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodSupply)
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "acquire-1", acquisitionPlan(t, "acquire-plan-1", "WoodLog")); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "acquire-plan-1", 1
	if _, err := s.PrepareAcquisition(ctx, "acquire-plan-1", "acquire-plan-1-a", AcquisitionAdmission{Snapshot: target, Tick: tick, Thing: "acq-WoodLog", SnapshotToken: "acq-cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "acquire-plan-1", "acquire-plan-1-a", target, tick); err != nil {
		t.Fatal(err)
	}
	g, err := s.LoadGoal(ctx, g.Goal.ID)
	if err != nil {
		t.Fatal(err)
	}
	field := growingPlan(t, "field-plan-1", []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}})
	if g, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "field-1", field); err != nil {
		t.Fatal("open acquisition blocked a field method", err)
	}
	// The committed field batch is open until its zone resolves, so another
	// field batch waits.
	second := growingPlan(t, "field-plan-2", []domain.Cell{{X: 5, Z: 5}})
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "field-2", second); err == nil {
		t.Fatal("open field work did not block a second field batch")
	}
}

// A field batch made of farm infrastructure (a sun lamp) shares the
// exemption; any other building does not.
func TestCommitFieldInfrastructureExemptFromAcquisitionOpenWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := foodDeficitRoutineRequest()
	tick := r.Tick
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodSupply)
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "acquire-1", acquisitionPlan(t, "acquire-plan-1", "WoodLog")); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "acquire-plan-1", 1
	if _, err := s.PrepareAcquisition(ctx, "acquire-plan-1", "acquire-plan-1-a", AcquisitionAdmission{Snapshot: target, Tick: tick, Thing: "acq-WoodLog", SnapshotToken: "acq-cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "acquire-plan-1", "acquire-plan-1-a", target, tick); err != nil {
		t.Fatal(err)
	}
	g, err := s.LoadGoal(ctx, g.Goal.ID)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wall-1", buildingOnlyPlan(t, "wall-plan-1", "Wall")); err == nil {
		t.Fatal("open acquisition did not block an unrelated building")
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "lamp-1", buildingOnlyPlan(t, "lamp-plan-1", "SunLamp")); err != nil {
		t.Fatal("open acquisition blocked a sun lamp field batch", err)
	}
}

func buildingOnlyPlan(t *testing.T, id, definition string) domain.PlanSpec {
	t.Helper()
	b, err := domain.NewBuilding(definition, domain.Cell{X: 3, Z: 3}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction(domain.ActionID(id+"-0"), b)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan(domain.PlanID(id), 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	return plan
}
