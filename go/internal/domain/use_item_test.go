package domain

import "testing"

func TestUseItemIntentAndClosedVariant(t *testing.T) {
	use, err := NewUseItem("colonist", "Apparel_PsychicShockLance1", "raider")
	if err != nil || use.Pawn() != "colonist" || use.Item() != "Apparel_PsychicShockLance1" || use.Target() != "raider" {
		t.Fatal(use, err)
	}
	action, err := NewUseItemAction("lance", use)
	if err != nil || action.Kind() != UseItemAction {
		t.Fatal(action, err)
	}
	if got, ok := action.UseItem(); !ok || got != use {
		t.Fatal(got, ok)
	}
	if _, ok := action.Capture(); ok {
		t.Fatal("use item exposed capture")
	}
	if _, err := NewUseItemAction("lance", UseItem{}); err == nil {
		t.Fatal("zero intent accepted")
	}
	if _, err := NewUseItemAction("", use); err == nil {
		t.Fatal("invalid action id accepted")
	}
	for _, bad := range [][3]string{{"a", "i", "a"}, {"a", "a", "b"}, {"a", "b", "b"}, {"", "i", "b"}, {"a", "", "b"}, {"a", "i", ""}} {
		if _, err := NewUseItem(PawnID(bad[0]), bad[1], PawnID(bad[2])); err == nil {
			t.Fatal("invalid use item accepted", bad)
		}
	}
	plan, err := NewPlan("plan", 1, []Action{action})
	if err != nil || len(plan.Actions()) != 1 || plan.Actions()[0] != action {
		t.Fatal(plan, err)
	}
}
