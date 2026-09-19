package domain

import (
	"reflect"
	"testing"
)

func TestHumanButcherRequiresPinnedWorker(t *testing.T) {
	if _, err := NewHumanButcherBill("bench", "token", ""); err == nil {
		t.Fatal("unassigned human bill accepted")
	}
	b, err := NewHumanButcherBill("bench", "token", "cook")
	if err != nil {
		t.Fatal(err)
	}
	if _, err = NewProductionBillAction("action", b); err != nil {
		t.Fatal(err)
	}
	if b.Worker() != "cook" || b.ClaimRecipe() == b.Recipe() {
		t.Fatal("lost distinct pinned bill", b)
	}
	b.target = 1
	if _, err = NewProductionBillAction("action", b); err == nil {
		t.Fatal("mutated bill accepted")
	}
}

func TestProductionBillIngredientsAreCanonicalAndImmutable(t *testing.T) {
	input := []string{"Steel", "Cloth"}
	bill, err := NewProductionBill("bench", "recipe", "token", StockTarget, 1, input...)
	if err != nil {
		t.Fatal(err)
	}
	input[0] = "WoodLog"
	got := bill.Ingredients()
	if !reflect.DeepEqual(got, []string{"Cloth", "Steel"}) {
		t.Fatal(got)
	}
	got[0] = "WoodLog"
	want, _ := NewProductionBill("bench", "recipe", "token", StockTarget, 1, "Cloth", "Steel")
	if bill != want {
		t.Fatal("filter is mutable or noncanonical", bill)
	}
	if _, err := NewProductionBillAction("action", bill); err != nil {
		t.Fatal(err)
	}
	for _, invalid := range [][]string{{""}, {"Cloth", "Cloth"}, make([]string, 257)} {
		if _, err := NewProductionBill("bench", "recipe", "token", StockTarget, 1, invalid...); err == nil {
			t.Fatal("accepted invalid ingredients", invalid)
		}
	}
	if _, err := NewProductionBill("bench", "ButcherCorpseFlesh", "token", ButcherForever, 0, "Cloth"); err == nil {
		t.Fatal("accepted ingredient override for the special butcher bill")
	}
}
