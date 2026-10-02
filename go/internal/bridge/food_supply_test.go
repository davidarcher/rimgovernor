package bridge

import (
	"math"
	"os"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// foodFixture is the committed food supply and the things table rows its
// stocks reference (#1343).
func foodFixture(t *testing.T) (*o.FoodSupplyFacts, Things) {
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
	things := Things{}
	for _, row := range table.Things {
		things[row.Thing.GetId()] = row
	}
	return supply, things
}

func cloneThings(things Things) Things {
	out := Things{}
	for id, row := range things {
		out[id] = proto.Clone(row).(*o.Thing)
	}
	return out
}

func TestFoodSupplyContractRejectsIncompleteAndContradictoryInputs(t *testing.T) {
	fixture, things := foodFixture(t)
	if _, known, err := JoinFoodSupply(fixture, things); err != nil || !known {
		t.Fatal(known, err)
	}
	for _, change := range []func(*o.FoodSupplyFacts){
		func(v *o.FoodSupplyFacts) { v.Larder = &o.FoodLarderFacts{RawMeatNutrition: math.NaN()} },
		func(v *o.FoodSupplyFacts) {
			v.Larder = &o.FoodLarderFacts{Corpses: []*o.CorpseHandling{{StockId: "missing"}}}
		},
		func(v *o.FoodSupplyFacts) { v.Consumers[1].PawnId = v.Consumers[0].PawnId },
		func(v *o.FoodSupplyFacts) { v.Stocks[1].Item.Id = v.Stocks[0].Item.Id },
		func(v *o.FoodSupplyFacts) { v.Stocks[0].Item.DefName = proto.String("RawRice") },
		func(v *o.FoodSupplyFacts) { v.Stocks[0].EaterIds = []string{"missing"} },
		func(v *o.FoodSupplyFacts) { v.Stocks[0].EaterIds = []string{"a", "a"} },
		func(v *o.FoodSupplyFacts) { v.Stocks[1].HolderId = proto.String("") },
		func(v *o.FoodSupplyFacts) { v.Stocks[0].Nutrition = proto.Float64(math.NaN()) },
		func(v *o.FoodSupplyFacts) { v.Consumers[0].NutritionPerDay = proto.Float64(-1) },
	} {
		v := proto.Clone(fixture).(*o.FoodSupplyFacts)
		change(v)
		if err := ValidateFoodSupply(v); err == nil {
			t.Fatal("invalid food input accepted", v)
		}
	}
	// The facts a stock's row decides are checked once it is joined.
	for _, change := range []func(*o.FoodSupplyFacts, Things){
		func(v *o.FoodSupplyFacts, _ Things) { v.Stocks[0].EaterIds = nil },
		func(v *o.FoodSupplyFacts, _ Things) { v.Stocks[0].Reserve = proto.Bool(true) },
		func(v *o.FoodSupplyFacts, _ Things) {
			v.Larder = &o.FoodLarderFacts{Corpses: []*o.CorpseHandling{{StockId: "rice"}}}
		},
		func(_ *o.FoodSupplyFacts, things Things) {
			things["rice"].Corpse, things["rice"].MeatAmount = proto.Bool(true), proto.Float64(0)
		},
	} {
		v, rows := proto.Clone(fixture).(*o.FoodSupplyFacts), cloneThings(things)
		change(v, rows)
		if _, _, err := JoinFoodSupply(v, rows); err == nil {
			t.Fatal("invalid joined food accepted", v)
		}
	}
	// A stock the table misses waits for a later frame.
	rows := cloneThings(things)
	delete(rows, "pack")
	if _, known, err := JoinFoodSupply(fixture, rows); err != nil || known {
		t.Fatal("an unresolved stock was decided", known, err)
	}
}

// A thing row's food facts are consistent with its kind (#1343).
func TestThingRowValidatesFoodFacts(t *testing.T) {
	_, things := foodFixture(t)
	ctx := pbContext()
	for _, row := range things {
		if err := ValidThing(row, ctx); err != nil {
			t.Fatal(row, err)
		}
	}
	corpse := &o.Thing{Thing: &o.EntityRef{Id: proto.String("corpse")}, StackCount: proto.Int64(1), Forbidden: proto.Bool(false), Corpse: proto.Bool(true), MeatAmount: proto.Float64(30), BodySize: proto.Float64(1), TileFootprint: proto.Int64(1)}
	if err := ValidThing(corpse, ctx); err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*o.Thing){
		func(v *o.Thing) { v.Corpse = proto.Bool(true) },
		func(v *o.Thing) { v.MeatAmount = proto.Float64(300) },
		func(v *o.Thing) { v.IsHumanlike = proto.Bool(true) },
		func(v *o.Thing) { v.Perishable, v.RotTicks = proto.Bool(false), proto.Int64(60000) },
		func(v *o.Thing) { v.RawClass = o.FoodIngredientClass(9).Enum() },
		func(v *o.Thing) { v.StackCount = proto.Int64(-1) },
		func(v *o.Thing) { v.TemperatureC = proto.Float64(math.Inf(1)) },
		func(v *o.Thing) { v.Thing.Id = proto.String("") },
	} {
		v := proto.Clone(things["rice"]).(*o.Thing)
		change(v)
		if err := ValidThing(v, ctx); err == nil {
			t.Fatal("invalid thing row accepted", v)
		}
	}
	if _, err := ThingTable(&o.ThingsSnapshot{Context: ctx, Things: []*o.Thing{things["rice"], things["rice"]}}, pbIdentity()); err == nil {
		t.Fatal("duplicate thing row accepted")
	}
}
