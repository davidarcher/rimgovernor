package domain

import (
	"strings"
	"testing"
)

func TestSettlementGiftIntentAndClosedVariants(t *testing.T) {
	crew := []PawnID{"pawn-1", "pawn-2"}
	gift, err := NewSettlementGift("caravan-1", "settlement-1", "faction-1", crew, 500)
	if err != nil || gift.Caravan() != "caravan-1" || gift.Settlement() != "settlement-1" || gift.Faction() != "faction-1" || gift.Silver() != 500 {
		t.Fatal(gift, err)
	}
	if got := gift.CrewIDs(); len(got) != 2 || got[0] != "pawn-1" || got[1] != "pawn-2" {
		t.Fatal(got)
	}
	action, err := NewSettlementGiftAction("settlement-gift-1", gift)
	if err != nil || action.ID() != "settlement-gift-1" || action.Kind() != SettlementGiftAction {
		t.Fatal(action, err)
	}
	if got, ok := action.SettlementGift(); !ok || got != gift {
		t.Fatal(got, ok)
	}
	if _, ok := action.QuestAccept(); ok {
		t.Fatal("settlement gift exposed quest accept")
	}
	if _, ok := action.Building(); ok {
		t.Fatal("settlement gift exposed building")
	}

	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewSettlementGift(CaravanID(invalid), "settlement-1", "faction-1", crew, 500); err == nil {
			t.Fatal("invalid caravan identity accepted")
		}
		if _, err := NewSettlementGift("caravan-1", SettlementID(invalid), "faction-1", crew, 500); err == nil {
			t.Fatal("invalid settlement identity accepted")
		}
		if _, err := NewSettlementGift("caravan-1", "settlement-1", FactionID(invalid), crew, 500); err == nil {
			t.Fatal("invalid faction identity accepted")
		}
		if _, err := NewSettlementGiftAction(ActionID(invalid), gift); err == nil {
			t.Fatal("invalid action identity accepted")
		}
	}
	if _, err := NewSettlementGift("caravan-1", "settlement-1", "faction-1", nil, 500); err == nil {
		t.Fatal("empty crew accepted")
	}
	if _, err := NewSettlementGift("caravan-1", "settlement-1", "faction-1", []PawnID{"pawn-1", "pawn-1"}, 500); err == nil {
		t.Fatal("duplicate crew accepted")
	}
	tooBig := make([]PawnID, 65)
	for i := range tooBig {
		tooBig[i] = PawnID("pawn-" + string(rune('a'+i)))
	}
	if _, err := NewSettlementGift("caravan-1", "settlement-1", "faction-1", tooBig, 500); err == nil {
		t.Fatal("oversized crew accepted")
	}
	if _, err := NewSettlementGift("caravan-1", "settlement-1", "faction-1", crew, 0); err == nil {
		t.Fatal("zero silver accepted")
	}
	if _, err := NewSettlementGift("caravan-1", "settlement-1", "faction-1", crew, -1); err == nil {
		t.Fatal("negative silver accepted")
	}
}

func TestSettlementGiftPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	gift, _ := NewSettlementGift("caravan-1", "settlement-1", "faction-1", []PawnID{"pawn-1"}, 500)
	action, _ := NewSettlementGiftAction("settlement-gift-1", gift)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "settlement-gift-1")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
}

func TestSettlementGiftHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing settlement gift handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, SettlementGiftAction, SettlementGiftAction}); err == nil {
		t.Fatal("duplicate settlement gift handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
