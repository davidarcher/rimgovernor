package domain

import "testing"

func dispatchedBill(t *testing.T, kind string) (Progress, GenerationSnapshot) {
	t.Helper()
	var a Action
	var err error
	switch kind {
	case "surgery":
		var s Surgery
		if s, err = NewSurgery("Human12", "RemoveAppendix", 0, false); err == nil {
			a, err = NewSurgeryAction("a1", s)
		}
	default:
		var b ProductionBill
		if b, err = NewProductionBill("Bench_1", "Make_Pemmican", StockTarget, 20); err == nil {
			a, err = NewProductionBillAction("a1", b)
		}
	}
	if err != nil {
		t.Fatal(err)
	}
	plan, err := NewPlan("p1", 1, []Action{a})
	if err != nil {
		t.Fatal(err)
	}
	p, err := NewProgress(plan, a.ID())
	if err != nil {
		t.Fatal(err)
	}
	s := GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "p1", Revision: 1}
	if p, err = p.Prepare(s, 10); err != nil {
		t.Fatal(err)
	}
	if p, err = p.MarkDispatched(s, 10); err != nil {
		t.Fatal(err)
	}
	return p, s
}

// The bill id an applied placement receipt names is recorded only for an
// accepted production_bill or surgery receipt with a valid id (#2410).
func TestBillIdentityRequiresAnAppliedBillPlacement(t *testing.T) {
	for _, kind := range []string{"production", "surgery"} {
		p, _ := dispatchedBill(t, kind)
		attempt := p.View().Attempt
		for _, receipt := range []Receipt{ReceiptRefused, ReceiptUnknown, ReceiptUnsent} {
			if got, err := p.RecordBillReceipt(attempt, receipt, "Bill_7"); err == nil || got != p {
				t.Fatal("a non-applied receipt recorded a bill identity", kind, receipt, got, err)
			}
		}
		if _, err := p.RecordBillReceipt(attempt, ReceiptAccepted, " "); err == nil {
			t.Fatal("blank bill identity accepted")
		}
		got, err := p.RecordBillReceipt(attempt, ReceiptAccepted, "Bill_7")
		if id, known := got.View().Bill.Value(); err != nil || !known || id != "Bill_7" {
			t.Fatal(kind, got, err)
		}
		if got, err = p.RecordReceipt(attempt, ReceiptAccepted); err != nil {
			t.Fatal(err)
		} else if _, known := got.View().Bill.Value(); known {
			t.Fatal("a receipt without an identity invented one")
		}
	}
	building, _ := dispatched(t)
	if _, err := building.RecordBillReceipt(building.View().Attempt, ReceiptAccepted, "Bill_7"); err == nil {
		t.Fatal("a building receipt accepted a bill identity")
	}
}

func TestRemoveProductionBillValidates(t *testing.T) {
	if _, err := NewRemoveProductionBill("", "Bill_7"); err == nil {
		t.Fatal("empty bench accepted")
	}
	if _, err := NewRemoveProductionBill("Bench_1", " "); err == nil {
		t.Fatal("blank bill accepted")
	}
	r, err := NewRemoveProductionBill("Bench_1", "Bill_7")
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewRemoveProductionBillAction("r1", r)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := a.RemoveProductionBill(); !ok || got != r || a.Kind() != RemoveProductionBillAction {
		t.Fatal(got, ok)
	}
	if _, ok := a.Ignite(); ok {
		t.Fatal("a removal reads as an ignite")
	}
	if _, err := NewPlan("p", 1, []Action{a}); err != nil {
		t.Fatal(err)
	}
}
