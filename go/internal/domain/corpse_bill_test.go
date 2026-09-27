package domain

import "testing"

func TestCorpseBillIsRecipePlusCorpseFilter(t *testing.T) {
	// Butchering animals is the plain butcher bill; cremation takes any class.
	butcher, err := NewCorpseBill("Table_1", ButcherRecipe, "tok", CorpseAnimal)
	plain, _ := NewProductionBill("Table_1", ButcherRecipe, "tok", ButcherForever, 0)
	if err != nil || butcher != plain || butcher.Corpses() != CorpseAnimal || butcher.ClaimRecipe() != ButcherRecipe {
		t.Fatalf("animal butcher: %+v %v", butcher, err)
	}
	cremate, err := NewCorpseBill("Crem_1", CremateRecipe, "tok", CorpseStranger)
	if err != nil || cremate.Mode() != ButcherForever || cremate.Corpses() != CorpseStranger || cremate.ClaimRecipe() != "CremateCorpse/stranger" {
		t.Fatalf("stranger cremation: %+v %v", cremate, err)
	}
	if _, err := NewCorpseBill("Crem_1", CremateRecipe, "tok", CorpseColonist); err != nil {
		t.Errorf("colonist cremation refused: %v", err)
	}
	if human, _ := NewHumanButcherBill("Table_1", "tok", "cook"); human.Corpses() != CorpseStranger || human.ClaimRecipe() != "ButcherCorpseFlesh/humanlike" {
		t.Errorf("human butcher: %+v", human)
	}
	if _, err := NewProductionBillAction("a-0", cremate); err != nil {
		t.Fatalf("cremation action: %v", err)
	}
	for _, bad := range []struct {
		recipe string
		of     CorpseOf
	}{{CremateRecipe, "raider"}, {ButcherRecipe, CorpseStranger}, {"Make_Pemmican", CorpseStranger}} {
		if _, err := NewCorpseBill("Crem_1", bad.recipe, "tok", bad.of); err == nil {
			t.Errorf("%s/%s accepted", bad.recipe, bad.of)
		}
	}
	// A plain cremation bill without a corpse filter is not a bill.
	if _, err := NewProductionBill("Crem_1", CremateRecipe, "tok", ButcherForever, 0); err == nil {
		t.Error("unfiltered cremation accepted")
	}
}
