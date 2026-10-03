package domain

import "testing"

func TestButcherBillsKeepTheirCorpseFilters(t *testing.T) {
	animal, err := NewProductionBill("Table_1", "ButcherCorpseFlesh", ButcherForever, 0)
	if err != nil || animal.Corpses() != CorpseAnimal || animal.ClaimRecipe() != "ButcherCorpseFlesh" {
		t.Fatal(animal, err)
	}
	human, err := NewHumanButcherBill("Table_1", "ButcherCorpseFlesh", "cook")
	if err != nil || human.Corpses() != CorpseStranger || human.ClaimRecipe() != "ButcherCorpseFlesh/humanlike" {
		t.Fatal(human, err)
	}
}
