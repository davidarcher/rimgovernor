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
	mode := domain.FoodTarget
	if len(modes) > 0 {
		mode = modes[0]
	}
	bill, _ := domain.NewProductionBill("bench", "recipe", mode, 10)
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

func TestFiniteGearBillDoesNotPermanentlyClaimRecipe(t *testing.T) {
	ctx := context.Background()
	s, _, v := billStoreFixture(t, domain.GearBatch)
	if _, err := s.Prepare(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "plan", "bill", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	if claimed, err := s.BillClaimed(ctx, v.Snapshot, "bench", "recipe"); err != nil || claimed {
		t.Fatal("finite batch permanently claimed recipe", claimed, err)
	}
	if _, err := s.LoadPlan(ctx, "plan"); err != nil {
		t.Fatal("finite batch did not survive persistence", err)
	}
}

// Guards the fix for the bill_claims insertion point: a trusted refusal proves
// no bill was created, so the bench+recipe pair must remain claimable for the
// replanned bill rather than being permanently locked out at dispatch time.
func TestBillClaimNotRecordedOnRefusal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := billStoreFixture(t)
	if _, err := s.Prepare(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "plan", "bill", 1, domain.ReceiptRefused); err != nil {
		t.Fatal(err)
	}
	claimed, err := s.BillClaimed(ctx, v.Snapshot, "bench", "recipe")
	if err != nil {
		t.Fatal(err)
	}
	if claimed {
		t.Fatal("refused dispatch permanently claimed the bench and recipe")
	}
}

// An accepted (or uncertain) receipt may have actually created the bill, so the
// claim must be recorded once the outcome is no longer a trusted refusal.
func TestBillClaimRecordedOnAcceptedOrUncertain(t *testing.T) {
	t.Parallel()
	for _, receipt := range []domain.Receipt{domain.ReceiptAccepted, domain.ReceiptUnknown} {
		t.Run(string(receipt), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := billStoreFixture(t)
			if _, err := s.Prepare(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			if _, err := s.RecordReceipt(ctx, "plan", "bill", 1, receipt); err != nil {
				t.Fatal(err)
			}
			claimed, err := s.BillClaimed(ctx, v.Snapshot, "bench", "recipe")
			if err != nil {
				t.Fatal(err)
			}
			if !claimed {
				t.Fatalf("%s receipt did not claim the bench and recipe", receipt)
			}
		})
	}
}

// An unknown receipt reopens the intent to Pending and it is sent again;
// claiming the same bench+recipe on the resend must not conflict.
func TestBillClaimResendAfterUnknownDoesNotConflict(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := billStoreFixture(t)
	if _, err := s.Prepare(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "plan", "bill", 1, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	v.Tick++
	if _, err := s.Prepare(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "plan", "bill", 2, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	claimed, err := s.BillClaimed(ctx, v.Snapshot, "bench", "recipe")
	if err != nil {
		t.Fatal(err)
	}
	if !claimed {
		t.Fatal("second attempt did not claim the bench and recipe")
	}
}

// A resource-target goal admits the StockTarget bill its production path
// stages on a workshop bench; bills were once bound to the food goals only.
func TestBillMethodAcceptsResourceTargetGoal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Current.Native = 2
	r.Policy.ResourceTargets = map[policy.Resource]int64{"MeleeWeapon_Club": 3}
	r.Facts.Resources = domain.Known([]policy.Amount{})
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.MaintainResource)
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

// An admitted bill that has not yet been written holds the bench and recipe
// for a sibling planner of the same step, and lets go once it is terminal.
func TestBillPendingUntilTerminal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := billStoreFixture(t)
	pending, err := s.BillPending(ctx, "bench", "recipe")
	if err != nil || !pending {
		t.Fatal(pending, err)
	}
	if pending, err = s.BillPending(ctx, "bench", "other"); err != nil || pending {
		t.Fatal(pending, err)
	}
	if _, err := s.Prepare(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "plan", "bill", 1, domain.ReceiptRefused); err != nil {
		t.Fatal(err)
	}
	if pending, err = s.BillPending(ctx, "bench", "recipe"); err != nil || pending {
		t.Fatal(pending, err)
	}
}
