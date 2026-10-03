package domain

import (
	"strings"
	"testing"
)

func TestDropEquipmentIntentAndClosedVariants(t *testing.T) {
	intent, err := NewDropEquipment("pawn", "thing")
	if err != nil || intent.Pawn() != "pawn" || intent.Thing() != "thing" {
		t.Fatal(intent, err)
	}
	action, err := NewDropEquipmentAction("drop", intent)
	if err != nil || action.Kind() != DropEquipmentAction {
		t.Fatal(action, err)
	}
	if got, ok := action.DropEquipment(); !ok || got != intent {
		t.Fatal(got, ok)
	}
	if _, ok := action.Equip(); ok {
		t.Fatal("drop exposed equip")
	}
	if _, err := NewDropEquipmentAction("drop", DropEquipment{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	if _, err := NewDropEquipment("pawn", "pawn"); err == nil {
		t.Fatal("the pawn dropped itself")
	}
	for _, invalid := range []string{"", " ", "x\x00y", strings.Repeat("x", 257), string([]byte{0xff})} {
		if _, err := NewDropEquipment(PawnID(invalid), "thing"); err == nil {
			t.Fatal("invalid pawn accepted")
		}
		if _, err := NewDropEquipment("pawn", invalid); err == nil {
			t.Fatal("invalid thing accepted")
		}
		if _, err := NewDropEquipmentAction(ActionID(invalid), intent); err == nil {
			t.Fatal("invalid action accepted")
		}
	}
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if progress, err := NewProgress(plan, "drop"); err != nil || progress.Action() != action {
		t.Fatal(progress, err)
	}
}
