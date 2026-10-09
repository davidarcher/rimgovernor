package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// billAt is where a bill fixture prepares and dispatches its one action.
type billAt struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
}

func billStoreFixture(t *testing.T, modes ...domain.BillMode) (*Store, string, billAt) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "bill.db")
	s := open(t, path)
	mode := domain.HumanButcherForever
	if len(modes) > 0 {
		mode = modes[0]
	}
	bill, _ := domain.NewProductionBill("bench", "recipe", mode, 10)
	if mode == domain.HumanButcherForever {
		bill, _ = domain.NewHumanButcherBill("bench", "recipe", "worker")
	}
	a, _ := domain.NewProductionBillAction("bill", bill)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := billAt{Snapshot: snapshot, Tick: 12}
	return s, path, v
}

// A resource-target goal admits the StockTarget bill its production path
// stages on a workshop bench; bills were once bound to the food goals only.
func TestBillMethodAcceptsResourceTargetGoal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Current.Native = 2
	r.Facts.ResourceNeeds = map[policy.Resource]int64{"MeleeWeapon_Club": 3}
	r.Facts.Resources = domain.Known([]policy.Amount{})
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.MaintainResource)
	if g.Standard.Finding != domain.FindingUnmet {
		t.Fatal(g)
	}
	bill, err := domain.NewProductionBill("spot", "Make_MeleeWeapon_Club", domain.StockTarget, 3)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewProductionBillAction("bill", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("club-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "resource-club", plan); err != nil {
		t.Fatal(err)
	}
}
