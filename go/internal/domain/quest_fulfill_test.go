package domain

import (
	"strings"
	"testing"
)

func TestQuestFulfillIntentAndClosedVariants(t *testing.T) {
	crew := []PawnID{"pawn-1", "pawn-2"}
	fulfill, err := NewQuestFulfill("quest-1", "caravan-1", crew)
	if err != nil || fulfill.Quest() != "quest-1" || fulfill.Caravan() != "caravan-1" {
		t.Fatal(fulfill, err)
	}
	if got := fulfill.CrewIDs(); len(got) != 2 || got[0] != "pawn-1" || got[1] != "pawn-2" {
		t.Fatal(got)
	}
	action, err := NewQuestFulfillAction("quest-fulfill-1", fulfill)
	if err != nil || action.ID() != "quest-fulfill-1" || action.Kind() != QuestFulfillAction {
		t.Fatal(action, err)
	}
	if got, ok := action.QuestFulfill(); !ok || got != fulfill {
		t.Fatal(got, ok)
	}
	if _, ok := action.SettlementGift(); ok {
		t.Fatal("quest fulfill exposed settlement gift")
	}
	if _, ok := action.QuestAccept(); ok {
		t.Fatal("quest fulfill exposed quest accept")
	}
	if _, ok := action.Building(); ok {
		t.Fatal("quest fulfill exposed building")
	}

	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewQuestFulfill(QuestID(invalid), "caravan-1", crew); err == nil {
			t.Fatal("invalid quest identity accepted")
		}
		if _, err := NewQuestFulfill("quest-1", CaravanID(invalid), crew); err == nil {
			t.Fatal("invalid caravan identity accepted")
		}
		if _, err := NewQuestFulfillAction(ActionID(invalid), fulfill); err == nil {
			t.Fatal("invalid action identity accepted")
		}
	}
	if _, err := NewQuestFulfill("quest-1", "caravan-1", nil); err == nil {
		t.Fatal("empty crew accepted")
	}
	if _, err := NewQuestFulfill("quest-1", "caravan-1", []PawnID{"pawn-1", "pawn-1"}); err == nil {
		t.Fatal("duplicate crew accepted")
	}
	tooBig := make([]PawnID, 65)
	for i := range tooBig {
		tooBig[i] = PawnID("pawn-" + string(rune('a'+i)))
	}
	if _, err := NewQuestFulfill("quest-1", "caravan-1", tooBig); err == nil {
		t.Fatal("oversized crew accepted")
	}
}

func TestQuestFulfillPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	fulfill, _ := NewQuestFulfill("quest-1", "caravan-1", []PawnID{"pawn-1"})
	action, _ := NewQuestFulfillAction("quest-fulfill-1", fulfill)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "quest-fulfill-1")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
}

func TestQuestFulfillHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing quest fulfill handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, QuestFulfillAction, QuestFulfillAction}); err == nil {
		t.Fatal("duplicate quest fulfill handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
