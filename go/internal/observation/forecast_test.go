package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"os"
	"testing"
)

func forecastFixture(t *testing.T) (*o.ColonyFactsReply, Identity) {
	t.Helper()
	r := &o.ColonyFactsReply{}
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	human := &o.FoodSupplyFacts{}
	data, err = os.ReadFile("../../../contracts/fixtures/food-supply.json")
	if err != nil {
		t.Fatal(err)
	}
	if err = protojson.Unmarshal(data, human); err != nil {
		t.Fatal(err)
	}
	human.Stocks[0].Perishable = proto.Bool(false)
	human.Stocks[0].RotTicks = nil
	combined := proto.Clone(human).(*o.FoodSupplyFacts)
	combined.Consumers = append(combined.Consumers, &o.FoodConsumer{PawnId: proto.String("animal"), NutritionPerDay: proto.Float64(1)})
	combined.Stocks[0].EaterIds = append(combined.Stocks[0].EaterIds, "animal")
	combined.Completeness.Matched = proto.Uint64(5)
	combined.Completeness.Returned = proto.Uint64(5)
	r.GetObserved().FoodSupply = &o.FoodSupplySection{Outcome: &o.FoodSupplySection_Observed{Observed: human}}
	r.GetObserved().Forecast = &o.ForecastSection{Outcome: &o.ForecastSection_Observed{Observed: &o.ForecastFacts{CombinedFoodSupply: combined, AnimalIds: []string{"animal"}, Patients: []*o.PatientForecast{{PawnId: proto.String("a")}, {PawnId: proto.String("b")}}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(4), Returned: proto.Uint64(4), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	return r, Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
}
func TestCombinedFoodForecastReachesRoutineFacts(t *testing.T) {
	r, identity := forecastFixture(t)
	p, err := DecodeColony(r, identity)
	if err != nil {
		t.Fatal(err)
	}
	if days, known := p.Facts.FoodDays.Value(); !known || days != 2.25 {
		t.Fatal("animal competition was not counted", p.Facts.FoodDays)
	}
	r.GetObserved().Forecast.GetObserved().CombinedFoodSupply.Stocks[0].Nutrition = nil
	p, err = DecodeColony(r, identity)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := p.Facts.FoodDays.Value(); known {
		t.Fatal("missing combined quantity certified food")
	}
}
func TestCombinedFoodForecastRejectsContradictoryCensus(t *testing.T) {
	for _, change := range []func(*o.ForecastFacts){
		func(v *o.ForecastFacts) { v.AnimalIds = []string{"a"} },
		func(v *o.ForecastFacts) { v.AnimalIds = []string{"missing"} },
		func(v *o.ForecastFacts) { v.CombinedFoodSupply.Consumers[0].NutritionPerDay = proto.Float64(8) },
		func(v *o.ForecastFacts) { v.Patients[0].PawnId = proto.String("animal") },
		func(v *o.ForecastFacts) { v.Completeness.Filtered = proto.Uint64(1) },
		func(v *o.ForecastFacts) { v.Patients[0].BleedRatePerDay = proto.Float64(-1) },
	} {
		r, identity := forecastFixture(t)
		change(r.GetObserved().Forecast.GetObserved())
		if _, err := DecodeColony(r, identity); err == nil {
			t.Fatal("invalid forecast accepted")
		}
	}
}
