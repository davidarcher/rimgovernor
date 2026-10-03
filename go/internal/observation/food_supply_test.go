package observation

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
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
		things = things.With(row.Thing.GetId(), row)
	}
	return supply, things
}

// foodCatalog holds the defs the food fixtures name: raw rice is a vegetable
// whose source race (as a corpse def it stands for) is no humanlike.
func foodCatalog() *bridge.DefinitionCatalog {
	food := func(name string, flags d.FoodTypeFlags) *d.ThingDef {
		return &d.ThingDef{DefName: name, Ingestible: &d.IngestibleProperties{FoodType: flags, SourceDef: "Muffalo"}}
	}
	return &bridge.DefinitionCatalog{ThingDefs: map[string]*d.ThingDef{
		"RawRice":  food("RawRice", d.FoodTypeFlags_FOOD_TYPE_FLAGS_VEGETABLE_OR_FRUIT),
		"Rice":     food("Rice", d.FoodTypeFlags_FOOD_TYPE_FLAGS_VEGETABLE_OR_FRUIT),
		"Pemmican": food("Pemmican", d.FoodTypeFlags_FOOD_TYPE_FLAGS_MEAL),
		"Muffalo":  {DefName: "Muffalo", Race: &d.RaceProperties{Intelligence: d.Intelligence_INTELLIGENCE_ANIMAL}},
		"Human":    {DefName: "Human", Race: &d.RaceProperties{Intelligence: d.Intelligence_INTELLIGENCE_HUMANLIKE}},
	}}
}

func decodeFood(t *testing.T, wire *o.FoodSupplyFacts, things bridge.Things) (policy.FoodSupply, error) {
	t.Helper()
	supply, known, err := DecodeFoodSupply(wire, things, foodCatalog())
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
	// A thing rots exactly while it has a rot deadline: without one it is durable.
	if perishable, known := supply.Stocks[0].Perishable.Value(); !known || !perishable {
		t.Fatal("a thing with a rot deadline is perishable", perishable, known)
	}
	things.At("rice").RotTicks = nil
	supply, err = decodeFood(t, wire, things)
	if err != nil {
		t.Fatal(err)
	}
	if perishable, known := supply.Stocks[0].Perishable.Value(); !known || perishable {
		t.Fatal("a thing without a rot deadline is durable", perishable, known)
	}
	things.At("rice").RotTicks = proto.Int64(60000)
	wire.Stocks[1].Eaters = append(wire.Stocks[1].Eaters, bridge.NewRef("b"))
	if _, _, err = DecodeFoodSupply(wire, things, foodCatalog()); err == nil {
		t.Fatal("shared private inventory accepted")
	}
}

// A food stock the things table misses leaves the supply unknown (#1343).
func TestFoodSupplyUnresolvedStockIsUnknown(t *testing.T) {
	wire, things := foodFixture(t)
	things = things.Without("rice")
	if _, known, err := DecodeFoodSupply(wire, things, foodCatalog()); err != nil || known {
		t.Fatal("an unresolved stock was decided", known, err)
	}
}

func TestCorpseSupplyProjectionPreservesReserveAndYield(t *testing.T) {
	wire, things := foodFixture(t)
	row := things.At("rice")
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
	things.At("rice").Thing.DefName = proto.String("Pemmican")
	wire.Stocks[0].Reserve = proto.Bool(true)
	supply, err := decodeFood(t, wire, things)
	if err != nil || !supply.Stocks[0].Reserve {
		t.Fatal(supply, err)
	}
	things.At("rice").Thing.DefName = proto.String("Rice")
	if _, _, err = DecodeFoodSupply(wire, things, foodCatalog()); err == nil {
		t.Fatal("ordinary food accepted as reserve")
	}
	things.At("rice").Thing.DefName = proto.String("Pemmican")
	wire.Stocks[0].Holder = wire.Stocks[0].Eaters[0]
	if _, _, err = DecodeFoodSupply(wire, things, foodCatalog()); err == nil {
		t.Fatal("held inventory accepted as reserve")
	}
}
