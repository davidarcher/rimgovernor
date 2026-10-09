package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func budgetWorld(native domain.NativeGeneration) domain.GenerationSnapshot {
	return domain.GenerationSnapshot{Colony: "c", Map: 1, Load: "l", Plan: "p", Revision: 1, Native: native}
}

func TestRefusalBudgetPermanentGivesUpWithRealReason(t *testing.T) {
	b := RefusalBudget{}.Record("wall:3,4", "cannot_reach", domain.RefusalPermanent, budgetWorld(1))
	for _, world := range []domain.GenerationSnapshot{budgetWorld(1), budgetWorld(9)} {
		ok, why := b.Allowed("wall:3,4", world)
		if ok || why.Code != BudgetSpent || why.Reason != "cannot_reach" || why.Class != domain.RefusalPermanent {
			t.Fatalf("Allowed in %v = %v, %+v; want spent with the real reason", world, ok, why)
		}
	}
	if ok, why := b.Allowed("wall:5,6", budgetWorld(1)); !ok || why != (BudgetVerdict{}) {
		t.Fatalf("another subject = %v, %+v; want allowed and silent", ok, why)
	}
}

func TestRefusalBudgetTransientRearmsOnWorldChange(t *testing.T) {
	b := RefusalBudget{}.Record("bill:1", "no_worker", domain.RefusalTransient, budgetWorld(1))
	ok, why := b.Allowed("bill:1", budgetWorld(1))
	if ok || why.Code != BudgetWaiting || why.Reason != "no_worker" {
		t.Fatalf("same world = %v, %+v; want waiting", ok, why)
	}
	if ok, why = b.Allowed("bill:1", budgetWorld(2)); !ok || why != (BudgetVerdict{}) {
		t.Fatalf("changed world = %v, %+v; want re-armed", ok, why)
	}
}

func TestRefusalBudgetUnknownStaysVisibleNeverBans(t *testing.T) {
	b := RefusalBudget{}.Record("zone:7", "odd", domain.RefusalUnknown, budgetWorld(1))
	ok, why := b.Allowed("zone:7", budgetWorld(1))
	if !ok || why.Code != BudgetUnknown || why.Reason != "odd" {
		t.Fatalf("unknown = %v, %+v; want allowed and visible", ok, why)
	}
	b = RefusalBudget{}.Record("zone:7", "odd", "", budgetWorld(1))
	if ok, why = b.Allowed("zone:7", budgetWorld(1)); !ok || why.Class != domain.RefusalUnknown {
		t.Fatalf("unclassified = %v, %+v; want unknown", ok, why)
	}
}

func TestRefusalBudgetKeysByReasonAndDoesNotAliasInput(t *testing.T) {
	first := RefusalBudget{}.Record("a", "r1", domain.RefusalTransient, budgetWorld(1))
	second := first.Record("a", "r2", domain.RefusalPermanent, budgetWorld(1)).Record("a", "r1", domain.RefusalTransient, budgetWorld(2))
	if len(first.Entries) != 1 || len(second.Entries) != 2 {
		t.Fatalf("entries = %d, %d; want 1, 2 (same subject and reason replaces)", len(first.Entries), len(second.Entries))
	}
	if ok, why := second.Allowed("a", budgetWorld(5)); ok || why.Code != BudgetSpent {
		t.Fatalf("permanent beside transient = %v, %+v; want spent", ok, why)
	}
}
