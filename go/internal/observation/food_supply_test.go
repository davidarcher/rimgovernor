package observation

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func TestFoodSupplyProjectionPreservesHolderAndUnknownDeadline(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/food-supply.json")
	if err != nil {
		t.Fatal(err)
	}
	wire := &o.FoodSupplyFacts{}
	if err = protojson.Unmarshal(data, wire); err != nil {
		t.Fatal(err)
	}
	supply, err := DecodeFoodSupply(wire)
	if err != nil {
		t.Fatal(err)
	}
	forecast, err := policy.ForecastFood(supply, nil)
	if days, known := forecast.RunwayDays.Value(); err != nil || !known || days != 1 || forecast.InventoryNutrition != 4 || forecast.UsableNutrition != 7 {
		t.Fatal(forecast, err)
	}
	wire.Stocks[0].RotTicks = nil
	supply, err = DecodeFoodSupply(wire)
	if err != nil {
		t.Fatal("optional unknown deadline rejected", err)
	}
	if _, err = policy.ForecastFood(supply, nil); err == nil {
		t.Fatal("unknown deadline certified food")
	}
	wire.Stocks[0].RotTicks = proto.Int64(60000)
	wire.Stocks[1].EaterIds = append(wire.Stocks[1].EaterIds, "b")
	if _, err = DecodeFoodSupply(wire); err == nil {
		t.Fatal("shared private inventory accepted")
	}
}
