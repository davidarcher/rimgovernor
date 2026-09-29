package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// productionBillAction sends one production bill as a ProductionBillIntent;
// native checks the bench, recipe and a skilled worker when it applies.
func productionBillAction(action domain.Action) (*op.Action, error) {
	bill, ok := action.ProductionBill()
	if !ok {
		return nil, contract("not a production bill action")
	}
	if _, err := domain.NewProductionBillAction(action.ID(), bill); err != nil {
		return nil, contract("invalid production bill")
	}
	return &op.Action{Intent: &op.Action_ProductionBill{ProductionBill: billIntent(bill)}}, nil
}

func billIntent(bill domain.ProductionBill) *op.ProductionBillIntent {
	settings := &op.BillSettings{Suspended: proto.Bool(false), IngredientSearchRadius: proto.Float32(40), Store: &op.BillStore{Destination: &op.BillStore_Mode{Mode: op.StoreMode_STORE_MODE_DROP_ON_FLOOR}}}
	if bill.Mode() == domain.BeerReserve {
		settings.BeerReserve = proto.Bool(true)
	}
	if ingredients := bill.Ingredients(); len(ingredients) > 0 {
		selectors := make([]*op.FilterSelector, 0, len(ingredients))
		for _, name := range ingredients {
			selectors = append(selectors, &op.FilterSelector{Definition: &op.FilterSelector_ThingDef{ThingDef: name}})
		}
		settings.Ingredients = &op.FilterPatch{Replace: &op.SelectorList{Selectors: selectors}}
	}
	if bill.Corpses() != "" {
		settings.CorpseClass = CorpseClass(bill.Corpses()).Enum()
	}
	if bill.Worker() != "" {
		settings.Worker = &op.Assignment{Value: &op.Assignment_EntityId{EntityId: bill.Worker()}}
	}
	if bill.Mode() == domain.ButcherForever || bill.Mode() == domain.HumanButcherForever {
		settings.RepeatMode = op.RepeatMode_REPEAT_MODE_FOREVER.Enum()
	} else if bill.Mode() == domain.GearBatch {
		settings.RepeatMode = op.RepeatMode_REPEAT_MODE_COUNT.Enum()
		settings.RepeatCount = proto.Int32(bill.Target())
	} else {
		settings.RepeatMode = op.RepeatMode_REPEAT_MODE_TARGET.Enum()
		settings.TargetCount = proto.Int32(bill.Target())
		settings.UnpauseThreshold = proto.Int32(max(1, bill.Target()/2))
		settings.PauseWhenSatisfied = proto.Bool(true)
	}
	intent := &op.ProductionBillIntent{BenchId: proto.String(bill.Bench()), RecipeDef: proto.String(bill.Recipe()), Settings: settings}
	if bill.Replaces() != "" {
		intent.ReplaceOwnedBillId = proto.String(bill.Replaces())
	}
	return intent
}
