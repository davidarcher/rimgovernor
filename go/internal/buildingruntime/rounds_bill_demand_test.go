package buildingruntime

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// An undispatched gear bill is cancelled once it has been seen for longer
// than the expiry, and the caller's plans no longer carry it open; a bill
// seen for less stays open.
func TestExpiredGearBillIsCancelled(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	journal, err := store.Open(ctx, filepath.Join(t.TempDir(), "bills.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	bill, err := domain.NewProductionBill("bench-1", "Make_Apparel_FlakVest", domain.GearBatch, 1, "Steel")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("bill-plan-0", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("bill-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	load := func() []store.PlanState {
		plans, err := journal.LoadPlans(ctx)
		if err != nil {
			t.Fatal(err)
		}
		return plans
	}
	var ages billAges
	plans := load()
	if err = ages.cancelExpired(ctx, journal, plans, 1000); err != nil {
		t.Fatal(err)
	}
	if err = ages.cancelExpired(ctx, journal, plans, 1000+policy.OpenBillExpiry); err != nil {
		t.Fatal(err)
	}
	if v := load()[0].Progress[0].View(); v.Stage != domain.Pending {
		t.Fatalf("a bill within its expiry was cancelled: %s", v.Stage)
	}
	if err = ages.cancelExpired(ctx, journal, plans, 1001+policy.OpenBillExpiry); err != nil {
		t.Fatal(err)
	}
	if v := load()[0].Progress[0].View(); v.Stage != domain.Cancelled {
		t.Fatalf("stage = %s, want cancelled", v.Stage)
	}
	if v := plans[0].Progress[0].View(); v.Stage != domain.Cancelled {
		t.Fatalf("the caller's plans still carry the bill open: %s", v.Stage)
	}
}
