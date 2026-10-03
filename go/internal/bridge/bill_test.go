package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

func billFoodTarget(t *testing.T) domain.ProductionBill {
	t.Helper()
	b, err := domain.NewProductionBill("stove", "CookMealSimple", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func TestProductionBillAction(t *testing.T) {
	bill, err := billFoodTarget(t).ReplaceOwnedBill("old-bill")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("bill", bill)
	if err != nil {
		t.Fatal(err)
	}
	sent, err := productionBillAction(action)
	if err != nil {
		t.Fatal(err)
	}
	intent := sent.GetProductionBill()
	if intent.GetBenchId() != "stove" || intent.GetRecipeDef() != "CookMealSimple" || intent.GetReplaceOwnedBill().GetId() != "old-bill" || intent.GetSettings() == nil {
		t.Fatal("lost bench/recipe identity", intent)
	}
	if billIntent(billFoodTarget(t)).ReplaceOwnedBill != nil {
		t.Fatal("a bill without replacement names none")
	}
	if _, err := productionBillAction(domain.Action{}); err == nil {
		t.Fatal("a non-bill action built a bill")
	}
}

func TestBillIntentSettingsByMode(t *testing.T) {
	human, err := domain.NewHumanButcherBill("bench", "ButcherCorpseFlesh", "cook")
	if err != nil {
		t.Fatal(err)
	}
	hs := billIntent(human).GetSettings()
	if hs.GetWorker().GetEntityId() != "cook" || hs.GetRepeatMode() != op.RepeatMode_REPEAT_MODE_FOREVER {
		t.Fatal("human worker lost", hs)
	}
	add := billIntent(billFoodTarget(t))
	if add.Settings.GetRepeatMode() != op.RepeatMode_REPEAT_MODE_TARGET || add.Settings.GetTargetCount() != 10 || add.Settings.GetUnpauseThreshold() != 5 || !add.Settings.GetPauseWhenSatisfied() {
		t.Fatal("unexpected food-target settings", add.Settings)
	}
	if add.Settings.GetSuspended() || add.Settings.GetStore().GetMode() != op.StoreMode_STORE_MODE_DROP_ON_FLOOR {
		t.Fatal("unexpected shared settings", add.Settings)
	}
	forever, err := domain.NewProductionBill("butcher-table", "ButcherCorpseFlesh", domain.ButcherForever, 0)
	if err != nil {
		t.Fatal(err)
	}
	fAdd := billIntent(forever)
	if fAdd.Settings.GetRepeatMode() != op.RepeatMode_REPEAT_MODE_FOREVER || fAdd.Settings.TargetCount != nil {
		t.Fatal("unexpected butcher-forever settings", fAdd.Settings)
	}
	// StockTarget (GearProduce, MaintainResource-* and MaintainMedicalReserves'
	// shared "keep at least Target in stock" mode) reuses FoodTarget's
	// pause-when-satisfied settings shape.
	stock, err := domain.NewProductionBill("tailor", "MakeParka", domain.StockTarget, 1)
	if err != nil {
		t.Fatal(err)
	}
	sAdd := billIntent(stock)
	if sAdd.Settings.GetRepeatMode() != op.RepeatMode_REPEAT_MODE_TARGET || sAdd.Settings.GetTargetCount() != 1 || sAdd.Settings.GetUnpauseThreshold() != 1 || !sAdd.Settings.GetPauseWhenSatisfied() {
		t.Fatal("unexpected stock-target settings", sAdd.Settings)
	}
	if _, err := domain.NewProductionBill("tailor", "MakeParka", domain.StockTarget, 0); err == nil {
		t.Fatal("expected zero stock target to be rejected")
	}
}

func TestBillIntentReplacesIngredientMembership(t *testing.T) {
	bill, err := domain.NewProductionBill("tailor", "MakeParka", domain.StockTarget, 1, "Leather_Plain")
	if err != nil {
		t.Fatal(err)
	}
	filter := billIntent(bill).GetSettings().GetIngredients()
	if filter.GetReplace() == nil || len(filter.GetReplace().GetSelectors()) != 1 || filter.GetReplace().GetSelectors()[0].GetThingDef() != "Leather_Plain" || len(filter.GetAllow()) != 0 {
		t.Fatal("filter must replace the recipe defaults", filter)
	}
	if billIntent(billFoodTarget(t)).GetSettings().Ingredients != nil {
		t.Fatal("unfiltered bills must preserve recipe defaults")
	}
}

func TestBeerReserveBillIntent(t *testing.T) {
	bill, err := domain.NewProductionBill("brewery", "Make_Wort", domain.BeerReserve, 12)
	if err != nil || !billIntent(bill).GetSettings().GetBeerReserve() {
		t.Fatal(bill, err)
	}
}
