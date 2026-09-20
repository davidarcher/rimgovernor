package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestEventLootRestartAdmissionAndReset(t *testing.T) {
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	r.Facts.EventLoot = domain.Known([]policy.LootItem{})
	reviewRoutine(t, s, &r)
	s.Close()
	s = open(t, path)
	defer s.Close()
	cell := domain.Cell{X: 70, Z: 80}
	row := policy.LootItem{Supply: supplyCohort(1, cell)[0], Forbidden: true, SafeToHaul: true, SafetyKnown: true}
	// A safe forbidden stack outside any known extent is a reach hold, not
	// an Allow (#522); the base extent covering its cell admits it.
	r.Facts.EventLoot = domain.Known([]policy.LootItem{row})
	out := reviewRoutine(t, s, &r)
	if len(out.Review.EventLoot.Pending) != 0 || len(out.Review.EventLoot.Held) != 1 || out.Review.EventLoot.Held[0].Reason != "bounds_unknown" {
		t.Fatal(out.Review.EventLoot)
	}
	lootExtentFacts(t, &r.Facts, cell)
	out = reviewRoutine(t, s, &r)
	goal := routineGoal(t, out, policy.ManageSupplySafety)
	if goal.Goal.Need != domain.NeedDeficit || len(out.Review.EventLoot.Pending) != 1 || len(out.Review.EventLoot.Held) != 0 {
		t.Fatal(out)
	}
	if _, err := s.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, "loot", supplyPlan(t, "loot", 1, cell)); err != nil {
		t.Fatal(err)
	}
	row.Forbidden = false
	r.Facts.EventLoot = domain.Known([]policy.LootItem{row})
	reviewRoutine(t, s, &r)
	row.Forbidden = true
	r.Facts.EventLoot = domain.Known([]policy.LootItem{row})
	out = reviewRoutine(t, s, &r)
	if len(out.Review.EventLoot.Pending) != 1 {
		t.Fatal("safe re-forbid not adopted")
	}
	r.Current.Load = "new-load"
	out = reviewRoutine(t, s, &r)
	if len(out.Review.EventLoot.Pending) != 1 {
		t.Fatal("new load did not re-evaluate safety", out)
	}
}

// lootExtentFacts puts one wall and its Home coverage at cell, so the
// derived colony extent covers it.
func lootExtentFacts(t *testing.T, f *policy.RoutineFacts, cell domain.Cell) {
	t.Helper()
	b, err := domain.NewBuilding("Wall", cell, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	f.MapBounds = domain.Known(policy.Bounds{Width: 100, Height: 100})
	f.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "wall", Building: b, Cells: []domain.Cell{cell}}}})
	f.OwnedStockpiles = domain.Known([]policy.OwnedStockpile{})
	f.HomeCoverage = domain.Known(policy.HomeCoverageObservation{Targets: []policy.HomeCoverageTarget{{ID: "wall", Cells: []domain.Cell{cell}, Shape: domain.Known("shape"), Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0)), ExtentGeometry: domain.Known(policy.HomeExtentGeometry{})}}})
}

func TestSafetyForbidPersistsAsDistinctAction(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := routineRequest()
	cell := domain.Cell{X: 70, Z: 80}
	r.Facts.EventLoot = domain.Known([]policy.LootItem{{SafetyKnown: true, Supply: policy.StartingSupply{Thing: "loot", Definition: "Steel", Cell: cell}}})
	out := reviewRoutine(t, s, &r)
	goal := routineGoal(t, out, policy.ManageSupplySafety)
	target, err := domain.NewSupplyForbid("loot", "Steel", cell)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewSupplyAllowAction("forbid-loot", target)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("forbid-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, "forbid", plan); err != nil {
		t.Fatal(err)
	}
	loaded, err := s.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	actual, ok := loaded.Spec.Actions()[0].SupplyAllow()
	if !ok || actual != target || loaded.Spec.Actions()[0].Kind() != domain.SupplyForbidAction {
		t.Fatal(loaded)
	}
}
