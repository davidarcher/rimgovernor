package domain

import (
	"strings"
	"testing"
)

func TestEquipIntentAndClosedVariants(t *testing.T) {
	cell := Cell{X: 1, Z: 1}
	intent, err := NewEquip("pawn", "thing", "Gun_Revolver", cell)
	if err != nil || intent.Pawn() != "pawn" || intent.Thing() != "thing" || intent.Definition() != "Gun_Revolver" || intent.Cell() != cell {
		t.Fatal(intent, err)
	}
	action, err := NewEquipAction("equip", intent)
	if err != nil || action.ID() != "equip" || action.Kind() != EquipAction {
		t.Fatal(action, err)
	}
	if got, ok := action.Equip(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Building(); ok {
		t.Fatal("equip exposed building")
	}
	if _, ok := action.Haul(); ok {
		t.Fatal("equip exposed haul")
	}
	if _, err := NewEquipAction("equip", Equip{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewEquip(PawnID(invalid), "thing", "Gun_Revolver", cell); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewEquip("pawn", invalid, "Gun_Revolver", cell); err == nil {
			t.Fatal("invalid thing accepted")
		}
		if _, err := NewEquip("pawn", "thing", invalid, cell); err == nil {
			t.Fatal("invalid definition accepted")
		}
		if _, err := NewEquipAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	if _, err := NewEquip("pawn", "thing", "Gun_Revolver", Cell{X: -1, Z: 0}); err == nil {
		t.Fatal("negative cell accepted")
	}
}

func TestEquipPlanDoesNotRequireADraftPrerequisite(t *testing.T) {
	intent, _ := NewEquip("pawn", "thing", "Gun_Revolver", Cell{X: 1, Z: 1})
	action, _ := NewEquipAction("equip", intent)
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := NewProgress(plan, "equip")
	if err != nil || progress.Action() != action || progress.View().Stage != Pending || progress.View().Unresolved {
		t.Fatal(progress, err)
	}
	if _, known := progress.View().DraftCleanup.Value(); known {
		t.Fatal("equip fabricated draft ownership")
	}
}

func TestEquipHandlerCoverageIsRequired(t *testing.T) {
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction}); err == nil {
		t.Fatal("missing equip handler accepted")
	}
	if err := ValidateHandlerCoverage([]ActionKind{BuildingAction, OwnedDraftAction, MeleeAttackAction, SupplyAllowAction, EquipAction, EquipAction}); err == nil {
		t.Fatal("duplicate equip handler accepted")
	}
	if err := ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
}
