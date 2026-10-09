package buildingruntime

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// refuseMethodPlan journals a native refusal of class on every action of plan.
func refuseMethodPlan(t *testing.T, db *store.Store, world domain.GenerationSnapshot, id domain.PlanID, class domain.RefusalClass, reason string) {
	t.Helper()
	ctx := context.Background()
	plan, err := db.LoadPlan(ctx, id)
	if err != nil {
		t.Fatal(err)
	}
	world.Plan, world.Revision = id, plan.Spec.Revision()
	for _, action := range plan.Spec.Actions() {
		if _, err = db.Prepare(ctx, id, action.ID(), world, 7); err != nil {
			t.Fatal(err)
		}
		if _, err = db.Dispatch(ctx, id, action.ID(), world, 7); err != nil {
			t.Fatal(err)
		}
		refusal := &domain.NativeRefusal{Code: "InvalidRequest", Reason: reason, Class: class}
		if _, err = db.RecordReceipts(ctx, []store.BatchReceipt{{Plan: id, Action: action.ID(), Attempt: 1, Receipt: domain.ReceiptRefused, Refusal: refusal}}); err != nil {
			t.Fatal(err)
		}
	}
}

// The clearance planner keeps no attempt constant: a permanent refusal gives
// the batch up under native's real reason, a transient one waits while the
// world stands, and an unclassified one retries.
func TestRoundsClearanceFollowsTheSharedRefusalBudget(t *testing.T) {
	for _, tc := range []struct {
		name   string
		class  domain.RefusalClass
		reason string
		check  func(Verdict) bool
	}{
		{"permanent gives up with the real reason", domain.RefusalPermanent, "cannot reach", func(v Verdict) bool {
			return v.Is(RefusalRetriesSpent) && v.Refusal.Detail == "cannot_reach"
		}},
		{"transient waits in the same world", domain.RefusalTransient, "no worker", func(v Verdict) bool {
			return v.Is(WaitRetryBudget) && v.Refusal.Detail == "no_worker"
		}},
		{"unknown retries", domain.RefusalUnknown, "odd", func(v Verdict) bool { return v == BuildingReasonAdmitted }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			reviewer, db, planner, _ := clearanceBatchFixture(t)
			ctx := context.Background()
			first, err := planner.Step(ctx)
			if err != nil || first.Verdict != BuildingReasonAdmitted {
				t.Fatal(first, err)
			}
			refuseMethodPlan(t, db, reviewer.player.session.State().Snapshot, first.Plan, tc.class, tc.reason)
			if _, err = reviewer.Step(ctx); err != nil {
				t.Fatal(err)
			}
			second, err := planner.Step(ctx)
			if err != nil || !tc.check(second.Verdict) {
				t.Fatal(second, err)
			}
		})
	}
}

// Refusals reach the ledger only from refused receipts; an accepted write
// spends nothing however often it repeats.
func TestRefusalLedgerCountsOnlyNativeRefusals(t *testing.T) {
	reviewer, db, _, _, _ := roundsFixture(t)
	ctx := context.Background()
	world := reviewer.player.session.State().Snapshot
	var plans []domain.PlanID
	add := func(i int, receipt domain.Receipt, class domain.RefusalClass, reason string) {
		id := domain.PlanID(fmt.Sprintf("ledger-plan-%d", i))
		value, err := domain.NewHusbandry("warg", domain.HusbandryTrain, "Release")
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewHusbandryAction(domain.ActionID(string(id)+"-0"), value)
		if err != nil {
			t.Fatal(err)
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			t.Fatal(err)
		}
		if err = db.CreatePlan(ctx, plan); err != nil {
			t.Fatal(err)
		}
		if receipt == domain.ReceiptRefused {
			refuseMethodPlan(t, db, world, id, class, reason)
		} else {
			snapshot := world
			snapshot.Plan, snapshot.Revision = id, 1
			if _, err = db.Prepare(ctx, id, action.ID(), snapshot, 7); err != nil {
				t.Fatal(err)
			}
			if _, err = db.Dispatch(ctx, id, action.ID(), snapshot, 7); err != nil {
				t.Fatal(err)
			}
			if _, err = db.RecordReceipt(ctx, id, action.ID(), 1, receipt); err != nil {
				t.Fatal(err)
			}
		}
		plans = append(plans, id)
	}
	for i := 0; i < 12; i++ {
		add(i, domain.ReceiptAccepted, "", "")
	}
	if _, ok, err := admitSubject(ctx, db, "train-warg-", plans, world); err != nil || !ok {
		t.Fatal("accepted writes spent the budget", ok, err)
	}
	add(12, domain.ReceiptRefused, domain.RefusalTransient, "no worker")
	verdict, ok, err := admitSubject(ctx, db, "train-warg-", plans, world)
	if err != nil || ok || !verdict.Is(WaitRetryBudget) {
		t.Fatal("a transient refusal in the same world must wait", verdict, ok, err)
	}
	// The world moved on (a new native generation): the same request may
	// succeed now.
	moved := world
	moved.Native++
	if _, ok, err = admitSubject(ctx, db, "train-warg-", plans, moved); err != nil || !ok {
		t.Fatal("a changed world must re-arm a transient refusal", ok, err)
	}
	add(13, domain.ReceiptRefused, domain.RefusalPermanent, "not a trainable")
	for _, w := range []domain.GenerationSnapshot{world, moved} {
		verdict, ok, err = admitSubject(ctx, db, "train-warg-", plans, w)
		if err != nil || ok || !verdict.Is(RefusalRetriesSpent) || verdict.Refusal.Subject != "train-warg" || verdict.Refusal.Detail != "not_a_trainable" {
			t.Fatal("a permanent refusal must give up under its real reason", verdict, ok, err)
		}
	}
	if _, ok, err = admitSubject(ctx, db, "train-bear-", nil, world); err != nil || !ok {
		t.Fatal("another subject must stay allowed", ok, err)
	}
}

func TestEquipRefusalsAreKeyedByPawn(t *testing.T) {
	budget := policy.RefusalBudget{}.Record("equip-alice", "no path", domain.RefusalPermanent, domain.GenerationSnapshot{})
	if verdict, ok := budgetVerdict(budget, "equip-alice", domain.GenerationSnapshot{}); ok || !verdict.Is(RefusalRetriesSpent) {
		t.Fatal(verdict, ok)
	}
	if _, ok := budgetVerdict(budget, "equip-bob", domain.GenerationSnapshot{}); !ok {
		t.Fatal("another pawn must stay allowed")
	}
}

func TestNextMethodIDSkipsEveryUsedOrdinal(t *testing.T) {
	if got := nextMethodID("tend-a-", []domain.MethodID{"tend-a-0", "tend-a-1", "tend-b-0"}); got != "tend-a-2" {
		t.Fatal(got)
	}
	if got := nextMethodID("tend-c-", nil); got != "tend-c-0" {
		t.Fatal(got)
	}
}
