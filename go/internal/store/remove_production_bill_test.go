package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func billPlan(t *testing.T) (domain.PlanSpec, domain.RemoveProductionBill) {
	t.Helper()
	bill, err := domain.NewProductionBill("Bench_1", "Make_Pemmican", domain.StockTarget, 20)
	if err != nil {
		t.Fatal(err)
	}
	place, err := domain.NewProductionBillAction("place", bill)
	if err != nil {
		t.Fatal(err)
	}
	removal, err := domain.NewRemoveProductionBill("Bench_1", "Bill_Production_77")
	if err != nil {
		t.Fatal(err)
	}
	remove, err := domain.NewRemoveProductionBillAction("remove", removal)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("p", domain.PlanRevision(^uint64(0)), []domain.Action{place, remove})
	if err != nil {
		t.Fatal(err)
	}
	return p, removal
}

// A remove_production_bill (#2410) persists its bench and native bill id.
func TestRemoveProductionBillRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "bills.db")
	s := open(t, path)
	p, want := billPlan(t)
	if err := s.CreatePlan(ctx, p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	got, ok := loaded.Spec.Actions()[1].RemoveProductionBill()
	if !ok || got != want {
		t.Fatal(got, want)
	}
}

// An applied bill placement journals the native bill id against its action
// and the id survives a reopen (#2410); an action never placed carries none.
func TestBillReceiptJournalsNativeBillID(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "bills.db")
	s := open(t, path)
	p, _ := billPlan(t)
	if err := s.CreatePlan(ctx, p); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Prepare(ctx, "p", "place", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "p", "place", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordBillReceipt(ctx, "p", "place", 1, " "); err == nil {
		t.Fatal("blank bill id journaled")
	}
	got, err := s.RecordBillReceipt(ctx, "p", "place", 1, "Bill_Production_77")
	if id, known := got.View().Bill.Value(); err != nil || !known || id != "Bill_Production_77" {
		t.Fatal(got.View(), err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	if id, known := loaded.Progress[0].View().Bill.Value(); !known || id != "Bill_Production_77" {
		t.Fatal(loaded.Progress[0].View())
	}
	if _, known := loaded.Progress[1].View().Bill.Value(); known {
		t.Fatal("an untouched action carries a bill id")
	}
}

// Only a bill placement may journal a bill id.
func TestBillReceiptRefusesOtherKinds(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	prepare(t, s, "a")
	if _, err := s.Dispatch(ctx, "p", "a", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordBillReceipt(ctx, "p", "a", 1, "Bill_Production_77"); err == nil {
		t.Fatal("a building receipt journaled a bill id")
	}
}
