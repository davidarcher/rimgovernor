package domain

import "testing"

func dispatchedZone(t *testing.T) (Progress, GenerationSnapshot) {
	t.Helper()
	zone, err := NewZoneCreate(GrowingZone, "Plant_Rice", []Cell{{X: 3, Z: 5}})
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewZoneCreateAction("z1", zone)
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

// The zone identity an applied creation receipt names is the only ownership
// evidence a stockpile claim has, so only an accepted zone_create
// receipt with a valid id records one.
func TestZoneIdentityRequiresAnAppliedZoneCreateReceipt(t *testing.T) {
	p, _ := dispatchedZone(t)
	attempt := p.View().Attempt
	for _, receipt := range []Receipt{ReceiptRefused, ReceiptUnknown, ReceiptUnsent} {
		if got, err := p.RecordZoneReceipt(attempt, receipt, "Zone_7"); err == nil || got != p {
			t.Fatal("a non-applied receipt recorded a zone identity", receipt, got, err)
		}
	}
	if _, err := p.RecordZoneReceipt(attempt, ReceiptAccepted, " "); err == nil {
		t.Fatal("blank zone identity accepted")
	}
	got, err := p.RecordZoneReceipt(attempt, ReceiptAccepted, "Zone_7")
	if id, known := got.View().Zone.Value(); err != nil || !known || id != "Zone_7" {
		t.Fatal(got, err)
	}
	building, _ := dispatched(t)
	if _, err := building.RecordZoneReceipt(building.View().Attempt, ReceiptAccepted, "Zone_7"); err == nil {
		t.Fatal("a building receipt accepted a zone identity")
	}
	if got, err := p.RecordReceipt(attempt, ReceiptAccepted); err != nil {
		t.Fatal(err)
	} else if _, known := got.View().Zone.Value(); known {
		t.Fatal("a receipt without an identity invented one")
	}
}
