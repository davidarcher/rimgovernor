package store

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func batchGoal(t *testing.T, s *Store, r *RoundsRequest) StandardState {
	t.Helper()
	r.Current.Native = 2
	r.Facts.ResourceNeeds = map[policy.Resource]int64{"MeleeWeapon_Club": 3}
	r.Facts.Resources = domain.Known([]policy.Amount{})
	g := roundsGoal(t, reviewRounds(t, s, r), policy.MaintainResource)
	if g.Standard.Finding != domain.FindingUnmet {
		t.Fatal(g)
	}
	return g
}

func batchPlan(t *testing.T, id domain.PlanID, mode domain.BillMode, places, removes int) domain.PlanSpec {
	t.Helper()
	var actions []domain.Action
	for i := 0; i < places; i++ {
		bill, err := domain.NewProductionBill(fmt.Sprintf("bench%d", i%3), fmt.Sprintf("Make_%d", i), mode, 3)
		if err != nil {
			t.Fatal(err)
		}
		a, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-p%d", id, i)), bill)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, a)
	}
	for i := 0; i < removes; i++ {
		rm, err := domain.NewRemoveProductionBill(fmt.Sprintf("bench%d", i%3), fmt.Sprintf("Bill_%d", i))
		if err != nil {
			t.Fatal(err)
		}
		a, err := domain.NewRemoveProductionBillAction(domain.ActionID(fmt.Sprintf("%s-r%d", id, i)), rm)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, a)
	}
	p, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	return p
}

// A Round's mixed place and remove actions across benches, with several
// recipes on one bench, commit as one plan.
func TestBillMethodCommitsMixedBatch(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	g := batchGoal(t, s, &r)
	plan := batchPlan(t, "mixed", domain.StockTarget, 6, 4)
	if _, err := s.CommitMethod(context.Background(), g.Standard.ID, g.Revision, "mixed-1", plan); err != nil {
		t.Fatal(err)
	}
}

// Two place actions for one bench and recipe still conflict.
func TestBillMethodRefusesRepeatedBenchRecipe(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	g := batchGoal(t, s, &r)
	bill, _ := domain.NewProductionBill("bench", "recipe", domain.StockTarget, 3)
	a, _ := domain.NewProductionBillAction("a", bill)
	b, _ := domain.NewProductionBillAction("b", bill)
	plan, err := domain.NewPlan("dup", 1, []domain.Action{a, b})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(context.Background(), g.Standard.ID, g.Revision, "dup-1", plan); err == nil {
		t.Fatal("repeated bench and recipe committed")
	}
}

// A lost StockTarget bill is placed again by a later attempt method: the
// accepted first placement is no obstacle.
func TestLostStockTargetBillReplaces(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	g := batchGoal(t, s, &r)
	first := batchPlan(t, "attempt1", domain.StockTarget, 1, 0)
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "stock-1", first); err != nil {
		t.Fatal(err)
	}
	snap := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "attempt1", Revision: 1, Native: 2}
	if _, err := s.Prepare(ctx, "attempt1", "attempt1-p0", snap, 12); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "attempt1", "attempt1-p0", snap, 12); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "attempt1", "attempt1-p0", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	g = batchGoal(t, s, &r)
	second := batchPlan(t, "attempt2", domain.StockTarget, 1, 0)
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "stock-2", second); err != nil {
		t.Fatal(err)
	}
}
