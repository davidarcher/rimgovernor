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
		return policy.UpkeepAnimal{ID: policy.PawnID(id), Definition: "Cow", Gender: gender, Release: domain.Known(false), Slaughter: domain.Known(false)}
	}
	p.Facts.AnimalUpkeep.Animals = domain.Known([]policy.UpkeepAnimal{cow("cow", "Female"), cow("bull", "Male")})
	p.Facts.AnimalUpkeep.AnimalRaces = policy.AnimalRaceCatalog{Races: map[policy.Resource]policy.AnimalRace{"Cow": {Def: "Cow", BodySize: domain.Known(2.5),
		Products: []policy.RaceProduct{{Kind: "milk", Def: "Milk", Amount: domain.Known(12.0), IntervalDays: domain.Known(1.0)}}}}}
	plan, known := reviewFoodPlan(p, policy.DefaultRoundsPolicy()).Value()
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
	plan, known := reviewFoodPlan(p, policy.DefaultRoundsPolicy()).Value()
	if !known || plan.DemandPerDay != 3 {
		t.Fatalf("plan = %+v, known=%v", plan, known)
	}
	if !foodPlanSupport(domain.Known(plan), policy.FoodCook, "cooking-capacity") {
		t.Fatal("missing cooking support")
	}
	p.CombinedFoodSupply = domain.Unknown[policy.FoodSupply]()
	if _, known := reviewFoodPlan(p, policy.DefaultRoundsPolicy()).Value(); known {
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
	r := &RoundsBuildingPlanner{goal: policy.MaintainHousing, phase: policy.HousingExpansion}
	p := observation.ColonyProjection{Facts: policy.RoundsFacts{Colonists: domain.Known(int64(3)), IndoorCapacity: domain.Known(int64(3)), FoodPlan: domain.Known(policy.FoodPlan{GapPerDay: 1})}}
	if _, _, reason := r.selection(p); !reason.Is(RefusalAwaitingPlan) {
		t.Fatal(reason)
	}
	p.Facts.FoodPlan = domain.Known(policy.FoodPlan{GapPerDay: 0})
	if _, _, reason := r.selection(p); !reason.IsZero() {
		t.Fatal(reason)
	}
}
