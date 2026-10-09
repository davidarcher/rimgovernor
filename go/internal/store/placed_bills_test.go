package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// PlacedBills names a placed bill's owner, bench and mode from the
// receipt's journaled id, only on the load that placed it; a bill never placed
// by the journal, or placed on another load, is absent.
func TestPlacedBillsNamesTheOwner(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := foodDeficitRoundsRequest()
	tick := r.Tick
	g := roundsGoal(t, reviewRounds(t, s, &r), policy.EnsureFoodSupply)
	bill, err := domain.NewProductionBill("bench", "recipe", domain.GearBatch, 2)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewProductionBillAction("bill", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("bill-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "gear", plan); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "bill-plan", 1
	if _, err = s.Prepare(ctx, "bill-plan", "bill", target, tick); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "bill-plan", "bill", target, tick); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordBillReceipt(ctx, "bill-plan", "bill", 1, "Bill_Production_9"); err != nil {
		t.Fatal(err)
	}
	got, err := s.PlacedBills(ctx, r.Current, []string{"Bill_Production_9", "Bill_Production_10"})
	if err != nil {
		t.Fatal(err)
	}
	want := PlacedBill{Standard: g.Standard.ID, Bench: "bench", Mode: domain.GearBatch}
	if len(got) != 1 || got["Bill_Production_9"] != want {
		t.Fatal(got)
	}
	elsewhere := r.Current
	elsewhere.Load = "another-load"
	if got, err = s.PlacedBills(ctx, elsewhere, []string{"Bill_Production_9"}); err != nil || len(got) != 0 {
		t.Fatal(got, err)
	}
}
