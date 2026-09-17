package domain

import "testing"

func TestGrowerCropIdentity(t *testing.T) {
	if _, err := NewGrowerCrop("", "Plant_Potato", "token"); err == nil {
		t.Fatal("expected empty thing to be rejected")
	}
	if _, err := NewGrowerCrop("basin", "", "token"); err == nil {
		t.Fatal("expected empty crop to be rejected")
	}
	if _, err := NewGrowerCrop("basin", "Plant_Potato", ""); err == nil {
		t.Fatal("expected empty before token to be rejected")
	}
	g, err := NewGrowerCrop("basin", "Plant_Potato", "token")
	if err != nil {
		t.Fatal(err)
	}
	if g.Thing() != "basin" || g.Crop() != "Plant_Potato" || g.BeforeToken() != "token" {
		t.Fatal("incorrect grower crop accessors")
	}
	action, err := NewGrowerCropAction("a", g)
	if err != nil {
		t.Fatal(err)
	}
	got, ok := action.GrowerCrop()
	if !ok || got != g || action.Kind() != GrowerCropAction {
		t.Fatal("grower crop action does not carry its value")
	}
	if _, err := NewGrowerCropAction("a", GrowerCrop{thing: "basin"}); err == nil {
		t.Fatal("expected a non-canonical value to be rejected")
	}
	plan, err := NewPlan("p", 1, []Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if plan.Actions()[0] != action {
		t.Fatal("plan did not keep the grower crop action")
	}
}
