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

func TestCorpseSupplyProjectionPreservesReserveAndYield(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/food-supply.json")
	if err != nil {
		t.Fatal(err)
	}
	wire := &o.FoodSupplyFacts{}
	if err = protojson.Unmarshal(data, wire); err != nil {
		t.Fatal(err)
	}
	row := wire.Stocks[0]
	row.Corpse = proto.Bool(true)
	row.Forbidden = proto.Bool(true)
	row.Count = proto.Int64(1)
	row.MeatAmount = proto.Float64(300)
	row.BodySize = proto.Float64(2)
	row.TileFootprint = proto.Int64(1)
	row.Nutrition = proto.Float64(15)
	supply, err := DecodeFoodSupply(wire)
	if err != nil {
		t.Fatal(err)
	}
	s := supply.Stocks[0]
	meat, mk := s.MeatAmount.Value()
	size, sk := s.BodySize.Value()
	forbidden, fk := s.Forbidden.Value()
	tiles, tk := s.TileFootprint.Value()
	if !s.Corpse || !mk || meat != 300 || !sk || size != 2 || !fk || !forbidden || !tk || tiles != 1 {
		t.Fatal(s)
	}
	forecast, err := policy.ForecastFood(supply, nil)
	if err != nil || forecast.UsableNutrition != 4 {
		t.Fatal(forecast, err)
	}
}

func TestFoodReserveWireProjectionAndValidation(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/food-supply.json")
	if err != nil {
		t.Fatal(err)
	}
	wire := &o.FoodSupplyFacts{}
	if err = protojson.Unmarshal(data, wire); err != nil {
		t.Fatal(err)
	}
	wire.Stocks[0].Item.DefName = proto.String("Pemmican")
	wire.Stocks[0].Reserve = proto.Bool(true)
	supply, err := DecodeFoodSupply(wire)
	if err != nil || !supply.Stocks[0].Reserve {
		t.Fatal(supply, err)
	}
	wire.Stocks[0].Item.DefName = proto.String("Rice")
	if _, err = DecodeFoodSupply(wire); err == nil {
		t.Fatal("ordinary food accepted as reserve")
	}
	wire.Stocks[0].Item.DefName = proto.String("Pemmican")
	wire.Stocks[0].HolderId = proto.String(wire.Stocks[0].EaterIds[0])
	if _, err = DecodeFoodSupply(wire); err == nil {
		t.Fatal("held inventory accepted as reserve")
	}
}
