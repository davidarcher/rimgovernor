package buildingruntime

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestFoodPlanIncludesAnimalRatesLaborAndDerivedFloor(t *testing.T) {
	p := observation.ColonyProjection{Workers: domain.Known(1), Acquisition: domain.Known([]policy.AcquisitionSource{}),
		CombinedFoodSupply: domain.Known(policy.FoodSupply{Complete: domain.Known(true), Consumers: []policy.FoodConsumer{{ID: "human", NutritionPerDay: domain.Known(2.0)}}}),
		FoodChannels:       domain.Known(observation.FoodChannels{Gatherable: []observation.GatherableAnimal{{PawnID: "cow", Race: "Cow", Resource: domain.Known("Milk"), Active: domain.Known(true), HandlerReachable: domain.Known(true), NutritionPerDay: domain.Known(.9), WorkPerDay: domain.Known(400.0), LeadDays: domain.Known(0.0)}}})}
	cow := func(id, gender string) policy.UpkeepAnimal {
		return policy.UpkeepAnimal{ID: policy.PawnID(id), Definition: "Cow", Gender: gender, Release: domain.Known(false), Slaughter: domain.Known(false), Herd: policy.HerdFacts{FeedPerDay: domain.Known(0.0)}}
	}
	p.Facts.AnimalUpkeep.Animals = domain.Known([]policy.UpkeepAnimal{cow("cow", "Female"), cow("bull", "Male")})
	p.Facts.AnimalUpkeep.AnimalRaces = policy.AnimalRaceCatalog{Races: map[policy.Resource]policy.AnimalRace{"Cow": {Def: "Cow", BodySize: domain.Known(2.5),
		Products: []policy.RaceProduct{{Kind: "milk", Def: "Milk", Amount: domain.Known(12.0), IntervalDays: domain.Known(1.0)}}}}}
	// The cows are seen delivering, so the emergency (no stock) still credits them.
	p.DeliveryLedger = domain.Known(observation.DeliveryLedger{LoadToken: "t", Counts: map[observation.DeliveryKey]observation.DeliveryCount{
		{Kind: observation.DeliveryAnimalProduct, SourceID: "Cow", Def: "Milk"}: {Nutrition: 1}}})
	var credit policy.DeliveryCredit
	plan, known := reviewFoodPlan(p, policy.DefaultRoundsPolicy(), &credit, nil, foodTrade{}).Value()
	if !known || !strings.Contains(plan.Explain(), "MaintainHerd-Cow floor 3") {
		t.Fatal(plan.Explain(), known)
	}
	if plan.DeliveredPerDay != .9 {
		t.Fatal(plan)
	}
	for _, e := range plan.Portfolio {
		if e.Channel.Kind == policy.FoodAnimalProduct {
			if work, k := e.Channel.WorkPerDay.Value(); !k || work != 400 {
				t.Fatal(e)
			}
			return
		}
	}
	t.Fatal("animal product absent")
}

// Food method fixtures now provide the complete competing-consumer census
// required by the ledger; their existing method/admission assertions stay intact.
func foodPlanFixture(v *o.ColonyFactsSnapshot) {
	food := &o.FoodSupplyFacts{Consumers: []*o.FoodConsumer{{PawnId: proto.String("food-pawn"), NutritionPerDay: proto.Float64(1)}}}
	v.FoodSupply = &o.FoodSupplySection{Outcome: &o.FoodSupplySection_Observed{Observed: food}}
	v.Forecast = &o.ForecastSection{Outcome: &o.ForecastSection_Observed{Observed: &o.ForecastFacts{CombinedFoodSupply: proto.Clone(food).(*o.FoodSupplyFacts), Patients: []*o.PatientForecast{{PawnId: proto.String("food-pawn")}}}}}
	v.WorkerCount = proto.Uint32(1)
	issues := v.Issues[:0]
	for _, issue := range v.Issues {
		if issue.GetField() != "acquisition" {
			issues = append(issues, issue)
		}
	}
	v.Issues = issues
}

func TestFoodPlanReviewBudgetsAnimalsAndUnknownDemand(t *testing.T) {
	p := observation.ColonyProjection{Workers: domain.Known(2), Acquisition: domain.Known([]policy.AcquisitionSource{{ID: "berry", Food: true, NutritionYield: 2}, {ID: "deer", Food: true, Hunt: true, NutritionYield: 4}}),
		CombinedFoodSupply: domain.Known(policy.FoodSupply{Complete: domain.Known(true), Consumers: []policy.FoodConsumer{{ID: "human", NutritionPerDay: domain.Known(1.0)}, {ID: "animal", NutritionPerDay: domain.Known(2.0)}}})}
	plan, known := reviewFoodPlan(p, policy.DefaultRoundsPolicy(), nil, nil, foodTrade{}).Value()
	if !known || plan.DemandPerDay != 3 {
		t.Fatalf("plan = %+v, known=%v", plan, known)
	}
	if !foodPlanSupport(domain.Known(plan), policy.FoodCook, "cooking-capacity") {
		t.Fatal("missing cooking support")
	}
	p.CombinedFoodSupply = domain.Unknown[policy.FoodSupply]()
	if _, known := reviewFoodPlan(p, policy.DefaultRoundsPolicy(), nil, nil, foodTrade{}).Value(); known {
		t.Fatal("unknown demand became a plan")
	}
}

func TestFoodPlanAcquisitionRejectsHeldAndUnknownSources(t *testing.T) {
	plan := policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{{Channel: policy.FoodChannel{Kind: policy.FoodForage, ID: "open"}, Decision: policy.FoodPlanOpen}, {Channel: policy.FoodChannel{Kind: policy.FoodHunt, ID: "held"}, Decision: policy.FoodPlanHold}}}
	rows, need := foodPlanAcquisition(plan, domain.Known([]policy.AcquisitionSource{{ID: "open", Food: true, NutritionYield: 2}, {ID: "held", Food: true, Hunt: true, NutritionYield: 9}, {ID: "unknown", Food: true, NutritionYield: 10}}))
	got, _ := rows.Value()
	nutrition, _ := need.Value()
	if len(got) != 1 || got[0].ID != "open" || nutrition != 2 {
		t.Fatalf("%+v %v", got, nutrition)
	}
}

func TestFoodPlanExpansionWaitsForGap(t *testing.T) {
	r := &RoundsBuildingPlanner{concern: policy.MaintainHousing, phase: policy.HousingExpansion}
	p := observation.ColonyProjection{Facts: policy.RoundsFacts{Colonists: domain.Known(int64(3)), IndoorCapacity: domain.Known(int64(3)), FoodPlan: domain.Known(policy.FoodPlan{GapPerDay: 1})}}
	if _, _, reason := r.selection(p); !reason.Is(RefusalAwaitingPlan) {
		t.Fatal(reason)
	}
	p.Facts.FoodPlan = domain.Known(policy.FoodPlan{GapPerDay: 0})
	if _, _, reason := r.selection(p); !reason.IsZero() {
		t.Fatal(reason)
	}
}

// The ledger's counters reach the channels through each builder's own census:
// zone id -> crop field, water body root -> fishing region, plant def ->
// forage, race -> animal product. A counter under another key credits nothing.
func TestFoodCreditAttributesLedgerCountersToChannels(t *testing.T) {
	p := observation.ColonyProjection{Workers: domain.Known(2),
		Acquisition:        domain.Known([]policy.AcquisitionSource{{ID: "plant-1", Definition: "Berry", Food: true, NutritionYield: 2, Designated: true}, {ID: "plant-2", Definition: "Corn", Food: true, NutritionYield: 2, Designated: true}}),
		CombinedFoodSupply: domain.Known(policy.FoodSupply{Complete: domain.Known(true), Consumers: []policy.FoodConsumer{{ID: "human", NutritionPerDay: domain.Known(4.0)}}}),
		FoodFields: domain.Known([]policy.FoodField{{ID: "z7", RemainingGrowDays: domain.Known(1.0), WorkPerDay: domain.Known(100.0),
			Plan: policy.FieldPlan{Crop: policy.CropChoice{Edible: domain.Known(true), GrowDays: domain.Known(5.0), HarvestNutrition: domain.Known(1.0)}, Sites: policy.FarmSitePlan{Cells: 10}}}}),
		FoodChannels: domain.Known(observation.FoodChannels{
			FishableWater: domain.Known(observation.FishableWater{FishingResearched: domain.Known(true), Regions: []observation.FishableRegion{{Root: domain.Cell{X: 5, Z: 8}, Population: domain.Known(300.0), MaxPopulation: domain.Known(300.0),
				NutritionPerFish: domain.Known(.25), FishPerBatch: domain.Known(6.0), WorkTicksPerBatch: domain.Known(1000.0), PawnFishWorkCapacity: domain.Known(2.0), Reachable: domain.Known(true), Frozen: domain.Known(false), Delivering: domain.Known(true)}}}),
			Gatherable: []observation.GatherableAnimal{{PawnID: "cow", Race: "Cow", Active: domain.Known(true), HandlerReachable: domain.Known(true), NutritionPerDay: domain.Known(0.9), WorkPerDay: domain.Known(400.0), LeadDays: domain.Known(0.0)}}})}
	p.Facts.AnimalUpkeep.Animals = domain.Known([]policy.UpkeepAnimal{{ID: "cow", Herd: policy.HerdFacts{FeedPerDay: domain.Known(0.0)}}})
	count := func(kind observation.DeliveryKind, source string) observation.DeliveryKey {
		return observation.DeliveryKey{Kind: kind, SourceID: source, Def: "x"}
	}
	p.DeliveryLedger = domain.Known(observation.DeliveryLedger{LoadToken: "t", Counts: map[observation.DeliveryKey]observation.DeliveryCount{
		count(observation.DeliveryCrop, "z7"):           {Nutrition: 3},
		count(observation.DeliveryFish, "5,8"):          {Nutrition: 3},
		count(observation.DeliveryForage, "Berry"):      {Nutrition: 3},
		count(observation.DeliveryAnimalProduct, "Cow"): {Nutrition: 3},
		count(observation.DeliveryCrop, "other-zone"):   {Nutrition: 3},
	}})
	var credit policy.DeliveryCredit
	plan, known := reviewFoodPlan(p, policy.DefaultRoundsPolicy(), &credit, nil, foodTrade{}).Value()
	if !known {
		t.Fatal("plan unknown")
	}
	got := map[string]bool{}
	for _, e := range plan.Portfolio {
		open, _ := e.Channel.Open.Value()
		got[string(e.Channel.Kind)+"/"+e.Channel.ID] = open
	}
	for _, id := range []string{"Crop/z7", "Fishing/water-5-8", "AnimalProduct/Cow", "Forage/plant-1"} {
		if !got[id] {
			t.Errorf("%s is not delivering although the ledger counts its source: %v", id, got)
		}
	}
	if got["Forage/plant-2"] {
		t.Errorf("a forage def with no counter is delivering: %v", got)
	}
}

func TestFoodCreditRowShape(t *testing.T) {
	d := foodCreditDecision(policy.CreditChange{Source: "crop:z7", Reason: policy.CreditWindow, State: "delivering", Expected: 4, Observed: 1, Factor: 0.25, WindowDays: 7})
	if d.Kind != "food_credit" || d.Reason != "window" || d.Target != "crop:z7" || d.Attrs["factor"] != 0.25 || d.Attrs["window_days"] != 7.0 || d.Attrs["state"] != "delivering" {
		t.Fatalf("%+v", d)
	}
}
