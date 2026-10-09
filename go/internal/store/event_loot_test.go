package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestEventLootRestartAdmissionAndReset(t *testing.T) {
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := roundsRequest()
	r.Facts.EventLoot = domain.Known([]policy.LootItem{})
	reviewRounds(t, s, &r)
	s.Close()
	s = open(t, path)
	defer s.Close()
	cell := domain.Cell{X: 70, Z: 80}
	row := policy.LootItem{Supply: supplyCohort(1, cell)[0], Forbidden: true, SafeToHaul: true, SafetyKnown: true}
	// A safe forbidden stack outside any known extent is a reach hold, not
	// an Allow; the base extent covering its cell admits it.
	r.Facts.EventLoot = domain.Known([]policy.LootItem{row})
	out := reviewRounds(t, s, &r)
	if len(out.Review.EventLoot.Pending) != 0 || len(out.Review.EventLoot.Held) != 1 || out.Review.EventLoot.Held[0].Reason != "bounds_unknown" {
		t.Fatal(out.Review.EventLoot)
	}
	lootExtentFacts(t, &r.Facts, cell)
	out = reviewRounds(t, s, &r)
	goal := roundsGoal(t, out, policy.ManageSupplySafety)
	if goal.Standard.Finding != domain.FindingUnmet || len(out.Review.EventLoot.Pending) != 1 || len(out.Review.EventLoot.Held) != 0 {
		t.Fatal(out)
	}
	if _, err := s.CommitMethod(ctx, goal.Standard.ID, goal.Revision, "loot", supplyPlan(t, "loot", 1, cell)); err != nil {
		t.Fatal(err)
	}
	row.Forbidden = false
	r.Facts.EventLoot = domain.Known([]policy.LootItem{row})
	reviewRounds(t, s, &r)
	row.Forbidden = true
	r.Facts.EventLoot = domain.Known([]policy.LootItem{row})
	out = reviewRounds(t, s, &r)
	if len(out.Review.EventLoot.Pending) != 1 {
		t.Fatal("safe re-forbid not adopted")
	}
	r.Current.Load = "new-load"
	out = reviewRounds(t, s, &r)
	if len(out.Review.EventLoot.Pending) != 1 {
		t.Fatal("new load did not re-evaluate safety", out)
	}
}

// lootExtentFacts puts one wall and its Home coverage at cell, so the
// derived colony extent covers it.
func lootExtentFacts(t *testing.T, f *policy.RoundsFacts, cell domain.Cell) {
	t.Helper()
	b, err := domain.NewBuilding("Wall", cell, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	f.MapBounds = domain.Known(policy.Bounds{Width: 100, Height: 100})
	f.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "wall", Building: b, Cells: []domain.Cell{cell}}}})
	f.HomeCoverage = domain.Known(policy.HomeCoverageObservation{Targets: []policy.HomeCoverageTarget{{ID: "wall", Cells: []domain.Cell{cell}, Shape: domain.Known("shape"), Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0)), ExtentGeometry: domain.Known(policy.HomeExtentGeometry{})}}})
}

func TestSafetyForbidPersistsAsDistinctAction(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	cell := domain.Cell{X: 70, Z: 80}
	r.Facts.EventLoot = domain.Known([]policy.LootItem{{SafetyKnown: true, Supply: policy.StartingSupply{Thing: "loot", Definition: "Steel", Cell: cell}}})
	out := reviewRounds(t, s, &r)
	goal := roundsGoal(t, out, policy.ManageSupplySafety)
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
	if _, err = s.CommitMethod(ctx, goal.Standard.ID, goal.Revision, "forbid", plan); err != nil {
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

// An item the safety census later reports unsafe is no longer cohort work, so
// the plan that allows it is refused. Unsafe loot raises no
// emergency.
func TestUnsafeItemAllowRefusedAfterCensus(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	request := roundsRequest()
	request.Current.Native = 1
	cell := domain.Cell{X: 1, Z: 2}
	lootExtentFacts(t, &request.Facts, cell)
	rows := []policy.LootItem{
		{Supply: supplyCohort(2, cell)[0], Forbidden: true, SafeToHaul: true, SafetyKnown: true},
		{Supply: supplyCohort(2, cell)[1], Forbidden: true, SafeToHaul: true, SafetyKnown: true},
	}
	request.Facts.EventLoot = domain.Known(rows)
	review := reviewRounds(t, s, &request)
	goal := roundsGoal(t, review, policy.ManageSupplySafety)
	plan := supplyPlan(t, "allow", 2, cell)
	if _, err := s.CommitMethod(ctx, goal.Standard.ID, goal.Revision, "allow", plan); err != nil {
		t.Fatal(err)
	}
	// item-0 turns unsafe after admission; the census is still unmet on item-1.
	rows[0].SafeToHaul = false
	request.Facts.EventLoot = domain.Known(rows)
	review = reviewRounds(t, s, &request)
	if got := review.Review.Unsafe; len(got) != 1 || got[0] != "item-0" {
		t.Fatal("unsafe not recorded", got)
	}
	if len(review.Review.Emergency) != 0 {
		t.Fatal("unsafe loot raised an emergency", review.Review.Emergency)
	}
	snapshot := request.Current
	snapshot.Plan, snapshot.Revision = plan.ID(), plan.Revision()
	// The action Safeguard vetoes at dispatch, after a prepare the
	// goal's own admission accepts.
	for _, action := range plan.Actions() {
		if _, err := s.Prepare(ctx, plan.ID(), action.ID(), snapshot, request.Tick); err != nil {
			t.Fatal(err)
		}
		// Only the action allowing the unsafe item-0 is vetoed.
		_, err := s.Dispatch(ctx, plan.ID(), action.ID(), snapshot, request.Tick)
		if unsafe := action.ID() == "allow-0"; unsafe != errors.Is(err, ErrActionVetoed) || !unsafe && err != nil {
			t.Fatal("dispatch of", action.ID(), "gave", err)
		}
	}
}
