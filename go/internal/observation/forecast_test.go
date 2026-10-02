package observation

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func forecastFixture(t *testing.T) (*o.ColonyFactsReply, Identity, bridge.Tables) {
	t.Helper()
	r := &o.ColonyFactsReply{}
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	human, things := foodFixture(t)
	things["rice"].Perishable = proto.Bool(false)
	things["rice"].RotTicks = nil
	combined := proto.Clone(human).(*o.FoodSupplyFacts)
	combined.Consumers = append(combined.Consumers, &o.FoodConsumer{PawnId: proto.String("animal"), NutritionPerDay: proto.Float64(1)})
	combined.Stocks[0].Eaters = append(combined.Stocks[0].Eaters, bridge.NewRef("animal"))
	r.GetObserved().FoodSupply = &o.FoodSupplySection{Outcome: &o.FoodSupplySection_Observed{Observed: human}}
	r.GetObserved().Forecast = &o.ForecastSection{Outcome: &o.ForecastSection_Observed{Observed: &o.ForecastFacts{CombinedFoodSupply: combined, AnimalIds: []string{"animal"}, Patients: []*o.PatientForecast{{PawnId: proto.String("a")}, {PawnId: proto.String("b")}}}}}
	return r, Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}, bridge.Tables{Things: things}
}
func TestCombinedFoodForecastReachesRoutineFacts(t *testing.T) {
	r, identity, tables := forecastFixture(t)
	p, err := DecodeColony(r, identity, tables)
	if err != nil {
		t.Fatal(err)
	}
	if days, known := p.Facts.FoodDays.Value(); !known || days != 2.25 {
		t.Fatal("animal competition was not counted", p.Facts.FoodDays)
	}
	r.GetObserved().Forecast.GetObserved().CombinedFoodSupply.Stocks[0].Nutrition = nil
	p, err = DecodeColony(r, identity, tables)
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
		func(v *o.ForecastFacts) { v.Patients[0].BleedRatePerDay = proto.Float64(-1) },
	} {
		r, identity, tables := forecastFixture(t)
		change(r.GetObserved().Forecast.GetObserved())
		if _, err := DecodeColony(r, identity, tables); err == nil {
			t.Fatal("invalid forecast accepted")
		}
	}
}
