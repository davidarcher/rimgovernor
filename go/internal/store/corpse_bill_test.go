package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCorpseBillSurvivesJournalReload(t *testing.T) {
	s := open(t, filepath.Join(t.TempDir(), "corpse.db"))
	bill, err := domain.NewCremationBill("crematorium", "CremateCorpse", domain.CorpseStranger, "")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("cremate", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, _ := domain.NewPlan("cremation", 1, []domain.Action{action})
	if err = s.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	read, err := s.LoadPlan(context.Background(), plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if got, _ := read.Spec.Actions()[0].ProductionBill(); got != bill {
		t.Fatal(got, bill)
	}
}

func TestAnimalCremationBillKeepsItsMinimumRotStage(t *testing.T) {
	s := open(t, filepath.Join(t.TempDir(), "animal.db"))
	bill, err := domain.NewCremationBill("crematorium", "CremateCorpse", domain.CorpseAnimal, domain.RotRotting)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("cremate", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, _ := domain.NewPlan("cremation", 1, []domain.Action{action})
	if err = s.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	read, err := s.LoadPlan(context.Background(), plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if got, _ := read.Spec.Actions()[0].ProductionBill(); got != bill || got.MinRot() != domain.RotRotting {
		t.Fatal(got, bill)
	}
}

// A pinned art batch (#1190) reloads with its worker (#1195).
func TestPinnedBatchBillSurvivesJournalReload(t *testing.T) {
	s := open(t, filepath.Join(t.TempDir(), "art.db"))
	bill, err := domain.NewProductionBill("table", "Make_SculptureSmall", domain.GearBatch, 1, "Jade")
	if err != nil {
		t.Fatal(err)
	}
	if bill, err = bill.PinWorker("Human1"); err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("sculpt", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, _ := domain.NewPlan("art", 1, []domain.Action{action})
	if err = s.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	read, err := s.LoadPlan(context.Background(), plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if got, _ := read.Spec.Actions()[0].ProductionBill(); got.Worker() != "Human1" || got.Recipe() != bill.Recipe() {
		t.Fatal(got, bill)
	}
}
