package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// A pinned batch sends its worker with the count settings, and its
// claim key names the worker.
func TestPinnedBatchBillCarriesTheWorker(t *testing.T) {
	batch, err := domain.NewProductionBill("TableSculpting_1", "Make_SculptureSmall", domain.GearBatch, 1)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := batch.PinWorker(""); err == nil {
		t.Fatal("empty worker pinned")
	}
	if b, _ := domain.NewProductionBill("stove", "CookMealSimple", domain.FoodTarget, 5); b.Recipe() != "" {
		if _, err := b.PinWorker("artist"); err == nil {
			t.Fatal("target bill pinned")
		}
	}
	pinned, err := batch.PinWorker("artist")
	if err != nil || pinned.ClaimRecipe() != "Make_SculptureSmall/artist" || batch.ClaimRecipe() != "Make_SculptureSmall" {
		t.Fatal(pinned.ClaimRecipe(), err)
	}
	action, err := domain.NewProductionBillAction("plan-0", pinned)
	if err != nil {
		t.Fatal(err)
	}
	intent, err := productionBillAction(action)
	if err != nil {
		t.Fatal(err)
	}
	s := intent.GetProductionBill().GetSettings()
	if s.GetRepeatMode() != op.RepeatMode_REPEAT_MODE_COUNT || s.GetRepeatCount() != 1 || s.GetWorker().GetEntityId() != "artist" {
		t.Fatalf("settings: %v", s)
	}
	if s := billIntent(batch).GetSettings(); s.GetWorker() != nil {
		t.Fatalf("unpinned batch: %v", s)
	}
}
