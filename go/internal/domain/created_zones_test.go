package domain

import "testing"

func TestStockpileReceiptValidatesAndOwnsAllComponents(t *testing.T) {
	z, err := NewFilteredStockpileZone(FoodFilter(), ImportantPriority, GroundRect{Origin: Cell{X: 3, Z: 5}, Width: 3, Height: 2})
	if err != nil {
		t.Fatal(err)
	}
	a, _ := NewZoneCreateAction("stockpile", z)
	plan, _ := NewPlan("p1", 1, []Action{a})
	p, _ := NewProgress(plan, a.ID())
	snapshot := GenerationSnapshot{Colony: "colony", Load: "load", Plan: "p1", Revision: 1}
	p, _ = p.Prepare(snapshot, 10)
	p, _ = p.MarkDispatched(snapshot, 10)
	rows := []CreatedZone{{ID: "Zone_2", Cells: []Cell{{X: 5, Z: 5}, {X: 5, Z: 6}}}, {ID: "Zone_1", Cells: []Cell{{X: 3, Z: 5}, {X: 3, Z: 6}}}}
	got, err := p.RecordStockpileReceipt(1, ReceiptAccepted, rows)
	if err != nil {
		t.Fatal(err)
	}
	placements, known := got.View().Stockpiles.Value()
	receipt, accepted := got.View().Receipt.Value()
	if !known || !accepted || receipt != ReceiptAccepted {
		t.Fatal(got)
	}
	rows[0].Cells[0].X = 100
	read := placements.Rows()
	read[0].Cells[0].X = 100
	if placements.Rows()[0].Cells[0].X != 3 {
		t.Fatal("mutable receipt")
	}
	for _, bad := range [][]CreatedZone{nil, {{ID: "Zone_1", Cells: []Cell{{X: 2, Z: 5}}}}, {{ID: "Zone_1", Cells: []Cell{{X: 3, Z: 5}, {X: 5, Z: 5}}}}, {{ID: "Zone_1", Cells: []Cell{{X: 3, Z: 5}}}, {ID: "Zone_2", Cells: []Cell{{X: 3, Z: 5}}}}, {{ID: "Zone_1", Cells: []Cell{{X: 3, Z: 5}}}, {ID: "Zone_1", Cells: []Cell{{X: 5, Z: 5}}}}} {
		if next, err := p.RecordStockpileReceipt(1, ReceiptAccepted, bad); err == nil || next != p {
			t.Fatal("invalid receipt accepted", bad, err)
		}
	}
	if _, err := p.RecordZoneReceipt(1, ReceiptAccepted, "Zone_1"); err == nil {
		t.Fatal("singular stockpile receipt accepted")
	}
}
