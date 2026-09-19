package domain

import "testing"

func TestBillReplacementUsesExistingActionForAllProductionModes(t *testing.T) {
	for _, mode := range []BillMode{FoodTarget, StockTarget, ButcherForever} {
		recipe, target := "MakePemmican", int32(6)
		if mode == ButcherForever {
			recipe, target = "ButcherCorpseFlesh", 0
		}
		bill, err := NewProductionBill("bench", recipe, "token", mode, target)
		if err != nil {
			t.Fatal(err)
		}
		bill, err = bill.ReplaceOwnedBill("foreign")
		if err != nil {
			t.Fatal(err)
		}
		action, err := NewProductionBillAction("action", bill)
		if err != nil {
			t.Fatal(err)
		}
		got, ok := action.ProductionBill()
		if !ok || got.Replaces() != "foreign" {
			t.Fatal(action)
		}
	}
}
