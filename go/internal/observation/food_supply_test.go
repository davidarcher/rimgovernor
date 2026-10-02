package observation

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// foodFixture is the committed food supply and the things table rows its
// stocks reference (#1343).
func foodFixture(t *testing.T) (*o.FoodSupplyFacts, bridge.Things) {
	t.Helper()
	supply, table := &o.FoodSupplyFacts{}, &o.ThingsSnapshot{}
	for path, message := range map[string]proto.Message{"../../../contracts/fixtures/food-supply.json": supply, "../../../contracts/fixtures/food-things.json": table} {
		data, err := os.ReadFile(path)
		if err != nil {
			t.Fatal(err)
		}
		if err = protojson.Unmarshal(data, message); err != nil {
			t.Fatal(err)
		}
	}
	things := bridge.Things{}
	for _, row := range table.Things {
		things[row.Thing.GetId()] = row
	}
	return supply, things
}

func decodeFood(t *testing.T, wire *o.FoodSupplyFacts, things bridge.Things) (policy.FoodSupply, error) {
	t.Helper()
	supply, known, err := DecodeFoodSupply(wire, things)
	if err == nil && !known {
		t.Fatal("fixture stock unresolved")
	}
	return supply, err
}

func TestFoodSupplyProjectionPreservesHolderAndUnknownDeadline(t *testing.T) {
	wire, things := foodFixture(t)
	supply, err := decodeFood(t, wire, things)
	if err != nil {
		t.Fatal(err)
	}
	forecast, err := policy.ForecastFood(supply, nil)
	if days, known := forecast.RunwayDays.Value(); err != nil || !known || days != 1 || forecast.InventoryNutrition != 4 || forecast.UsableNutrition != 7 {
		t.Fatal(forecast, err)
	}
	things["rice"].RotTicks = nil
	supply, err = decodeFood(t, wire, things)
	if err != nil {
		t.Fatal("optional unknown deadline rejected", err)
	}
	if _, err = policy.ForecastFood(supply, nil); err == nil {
		t.Fatal("unknown deadline certified food")
	}
	things["rice"].RotTicks = proto.Int64(60000)
	wire.Stocks[1].EaterIds = append(wire.Stocks[1].EaterIds, "b")
	if _, _, err = DecodeFoodSupply(wire, things); err == nil {
		t.Fatal("shared private inventory accepted")
	}
}

// A food stock the things table misses leaves the supply unknown (#1343).
func TestFoodSupplyUnresolvedStockIsUnknown(t *testing.T) {
	wire, things := foodFixture(t)
	delete(things, "rice")
	if _, known, err := DecodeFoodSupply(wire, things); err != nil || known {
		t.Fatal("an unresolved stock was decided", known, err)
	}
}

func TestCorpseSupplyProjectionPreservesReserveAndYield(t *testing.T) {
	wire, things := foodFixture(t)
	row := things["rice"]
	row.Corpse = proto.Bool(true)
	row.Forbidden = proto.Bool(true)
	row.StackCount = proto.Int64(1)
	row.MeatAmount = proto.Float64(300)
	row.BodySize = proto.Float64(2)
	row.TileFootprint = proto.Int64(1)
	wire.Stocks[0].Nutrition = proto.Float64(15)
	supply, err := decodeFood(t, wire, things)
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
	wire, things := foodFixture(t)
	things["rice"].Thing.DefName = proto.String("Pemmican")
	wire.Stocks[0].Reserve = proto.Bool(true)
	supply, err := decodeFood(t, wire, things)
	if err != nil || !supply.Stocks[0].Reserve {
		t.Fatal(supply, err)
	}
	things["rice"].Thing.DefName = proto.String("Rice")
	if _, _, err = DecodeFoodSupply(wire, things); err == nil {
		t.Fatal("ordinary food accepted as reserve")
	}
	things["rice"].Thing.DefName = proto.String("Pemmican")
	wire.Stocks[0].HolderId = proto.String(wire.Stocks[0].EaterIds[0])
	if _, _, err = DecodeFoodSupply(wire, things); err == nil {
		t.Fatal("held inventory accepted as reserve")
	}
}
