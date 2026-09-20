package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"testing"
)

func TestGearBatchWireCountAndFilter(t *testing.T) {
	bill, err := domain.NewProductionBill("tailor", "Make_Shirt", "token", domain.GearBatch, 11, "Cloth")
	if err != nil {
		t.Fatal(err)
	}
	if _, err = domain.NewProductionBillAction("batch", bill); err != nil {
		t.Fatal(err)
	}
	settings := BillOperation(bill).GetAddBill().GetSettings()
	if settings.GetRepeatMode() != op.RepeatMode_REPEAT_MODE_COUNT || settings.GetRepeatCount() != 11 || settings.TargetCount != nil || settings.GetIngredients().GetReplace().GetSelectors()[0].GetThingDef() != "Cloth" {
		t.Fatal(settings)
	}
}
