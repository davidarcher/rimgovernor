package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"math"
	"os"
	"testing"
)

func TestFoodSupplyContractRejectsIncompleteAndContradictoryInputs(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/food-supply.json")
	if err != nil {
		t.Fatal(err)
	}
	fixture := &o.FoodSupplyFacts{}
	if err = protojson.Unmarshal(data, fixture); err != nil {
		t.Fatal(err)
	}
	if err = ValidateFoodSupply(fixture); err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*o.FoodSupplyFacts){
		func(v *o.FoodSupplyFacts) { v.Stocks[0].Corpse = proto.Bool(true) },
		func(v *o.FoodSupplyFacts) { v.Stocks[0].MeatAmount = proto.Float64(300) },
		func(v *o.FoodSupplyFacts) { v.Larder = &o.FoodLarderFacts{RawMeatNutrition: math.NaN()} },
		func(v *o.FoodSupplyFacts) {
			v.Larder = &o.FoodLarderFacts{Corpses: []*o.CorpseHandling{{StockId: "missing"}}}
		},
		func(v *o.FoodSupplyFacts) { v.Completeness.Returned = proto.Uint64(3) },
		func(v *o.FoodSupplyFacts) { v.Completeness.Filtered = proto.Uint64(1) },
		func(v *o.FoodSupplyFacts) { v.Consumers[1].PawnId = v.Consumers[0].PawnId },
		func(v *o.FoodSupplyFacts) { v.Stocks[1].Item.Id = v.Stocks[0].Item.Id },
		func(v *o.FoodSupplyFacts) { v.Stocks[0].EaterIds = []string{"missing"} },
		func(v *o.FoodSupplyFacts) { v.Stocks[0].EaterIds = []string{"a", "a"} },
		func(v *o.FoodSupplyFacts) { v.Stocks[1].HolderId = proto.String("") },
		func(v *o.FoodSupplyFacts) { v.Stocks[1].RotTicks = proto.Int64(60000) },
		func(v *o.FoodSupplyFacts) { v.Stocks[0].Nutrition = proto.Float64(math.NaN()) },
		func(v *o.FoodSupplyFacts) { v.Consumers[0].NutritionPerDay = proto.Float64(-1) },
	} {
		v := proto.Clone(fixture).(*o.FoodSupplyFacts)
		change(v)
		if err = ValidateFoodSupply(v); err == nil {
			t.Fatal("invalid food input accepted")
		}
	}
}
