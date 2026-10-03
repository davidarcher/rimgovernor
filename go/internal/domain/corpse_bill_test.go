package domain

import "testing"

func TestCorpseBillIsRecipePlusCorpseFilter(t *testing.T) {
	// Butchering animals is the plain butcher bill; cremation takes any class.
	butcher, err := NewProductionBill("Table_1", "Butcher", ButcherForever, 0)
	if err != nil || butcher.Corpses() != CorpseAnimal || butcher.ClaimRecipe() != "Butcher" {
		t.Fatalf("animal butcher: %+v %v", butcher, err)
	}
	cremate, err := NewCremationBill("Crem_1", "Cremate", CorpseStranger, "")
	if err != nil || cremate.Mode() != CremateForever || cremate.Corpses() != CorpseStranger || cremate.ClaimRecipe() != "Cremate/stranger" {
		t.Fatalf("stranger cremation: %+v %v", cremate, err)
	}
	if _, err := NewCremationBill("Crem_1", "Cremate", CorpseColonist, ""); err != nil {
		t.Errorf("colonist cremation refused: %v", err)
	}
	if human, _ := NewHumanButcherBill("Table_1", "Butcher", "cook"); human.Corpses() != CorpseStranger || human.ClaimRecipe() != "Butcher/humanlike" {
		t.Errorf("human butcher: %+v", human)
	}
	if _, err := NewProductionBillAction("a-0", cremate); err != nil {
		t.Fatalf("cremation action: %v", err)
	}
	if _, err := NewCremationBill("Crem_1", "Cremate", "raider", ""); err == nil {
		t.Error("an unknown corpse class accepted")
	}
	if _, err := NewCremationBill("Crem_1", "", CorpseStranger, ""); err == nil {
		t.Error("a cremation bill without a recipe accepted")
	}
	// Animal cremation names its minimum so fresh corpses stay for the butcher.
	animal, err := NewCremationBill("Crem_1", "Cremate", CorpseAnimal, RotRotting)
	if err != nil || animal.MinRot() != RotRotting || animal.ClaimRecipe() != "Cremate/animal" {
		t.Fatalf("animal cremation: %+v %v", animal, err)
	}
	if _, err := NewProductionBillAction("a-1", animal); err != nil {
		t.Fatalf("animal cremation action: %v", err)
	}
	for _, bad := range []struct {
		of  CorpseOf
		rot RotStage
	}{{CorpseAnimal, ""}, {CorpseAnimal, RotFresh}, {CorpseStranger, RotFresh}} {
		if _, err := NewCremationBill("Crem_1", "Cremate", bad.of, bad.rot); err == nil {
			t.Errorf("%s/%s accepted", bad.of, bad.rot)
		}
	}
	// A plain bill mode cannot carry a cremation corpse filter.
	if _, err := NewProductionBill("Crem_1", "Cremate", CremateForever, 0); err == nil {
		t.Error("unfiltered cremation accepted")
	}
}
