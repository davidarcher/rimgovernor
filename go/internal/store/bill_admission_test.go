package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func billStoreFixture(t *testing.T) (*Store, string, BillAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "bill.db")
	s := open(t, path)
	bill, _ := domain.NewProductionBill("bench", "recipe", "bench-cas", domain.FoodTarget, 10)
	a, _ := domain.NewProductionBillAction("bill", bill)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := BillAdmission{Snapshot: snapshot, Tick: 12, Bench: "bench", SnapshotToken: "bench-cas"}
	return s, path, v
}

func TestBillAdmissionPrepareAndLoad(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := billStoreFixture(t)
	if _, err := s.PrepareBill(ctx, "plan", "bill", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.BillAdmissions) != 1 || state.BillAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestBillDispatchRequiresCurrentAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := billStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "bill", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a bill action")
	}
}

// Guards the fix for loadBillAdmission: a completed or unsuccessful (terminal)
// action must reject an admission record claiming to be newer than the
// progress it terminated with, the same way an unresolved action already did.
func TestBillAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	t.Parallel()
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := billStoreFixture(t)
			if _, err := s.PrepareBill(ctx, "plan", "bill", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			if _, err := s.RecordReceipt(ctx, "plan", "bill", 1, domain.ReceiptAccepted); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "bill", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
			if effect == domain.EffectUnsuccessful {
				observation.UnsuccessfulReason = domain.NativeFailure
			}
			if _, err := s.Observe(ctx, "plan", observation, v.Snapshot); err != nil {
				t.Fatal(err)
			}
			v.Tick = observation.Tick + 1
			data, err := json.Marshal(v)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE bill_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

// Guards the fix for the bill_claims insertion point: a trusted refusal proves
// no bill was created, so the bench+recipe pair must remain claimable for a
// retry rather than being permanently locked out at dispatch time.
func TestBillClaimNotRecordedOnRefusal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := billStoreFixture(t)
	if _, err := s.PrepareBill(ctx, "plan", "bill", v); err != nil {
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
	// A refusal reopens the action to Pending; retrying it must not error.
	v.Tick++
	if _, err := s.PrepareBill(ctx, "plan", "bill", v); err != nil {
		t.Fatal(err)
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
			if _, err := s.PrepareBill(ctx, "plan", "bill", v); err != nil {
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

// A bill later observed absent (the uncertain write in fact never landed) reopens
// to Pending; retrying and claiming the same bench+recipe again must not conflict.
func TestBillClaimReclaimAfterUncertainThenAbsentDoesNotConflict(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := billStoreFixture(t)
	if _, err := s.PrepareBill(ctx, "plan", "bill", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "bill", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "plan", "bill", 1, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "bill", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick += 2
	if _, err := s.PrepareBill(ctx, "plan", "bill", v); err != nil {
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
func TestBillAdmissionAcceptsResourceTargetGoal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Current.Native = 2
	r.Policy.ResourceTargets = map[policy.Resource]int64{"MeleeWeapon_Club": 3}
	r.Facts.Resources = domain.Known([]policy.Amount{})
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.MaintainResource)
	if g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
	}
	bill, err := domain.NewProductionBill("spot", "Make_MeleeWeapon_Club", "bench-cas", domain.StockTarget, 3)
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
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "resource-club", plan); err != nil {
		t.Fatal(err)
	}
}
