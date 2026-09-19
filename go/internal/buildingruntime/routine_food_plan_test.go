package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Food method fixtures now provide the complete competing-consumer census
// required by the ledger; their existing method/admission assertions stay intact.
func foodPlanFixture(v *o.ColonyFactsSnapshot) {
	count := func(n uint64) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(n), Returned: proto.Uint64(n), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	food := &o.FoodSupplyFacts{Consumers: []*o.FoodConsumer{{PawnId: proto.String("food-pawn"), NutritionPerDay: proto.Float64(1)}}, Completeness: count(1)}
	v.FoodSupply = &o.FoodSupplySection{Outcome: &o.FoodSupplySection_Observed{Observed: food}}
	v.Forecast = &o.ForecastSection{Outcome: &o.ForecastSection_Observed{Observed: &o.ForecastFacts{CombinedFoodSupply: proto.Clone(food).(*o.FoodSupplyFacts), Patients: []*o.PatientForecast{{PawnId: proto.String("food-pawn")}}, Completeness: count(2)}}}
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
	plan, known := reviewFoodPlan(p, policy.DefaultRoutinePolicy()).Value()
	if !known || plan.DemandPerDay != 3 {
		t.Fatalf("plan = %+v, known=%v", plan, known)
	}
	if !foodPlanSupport(domain.Known(plan), policy.FoodCook, "cooking-capacity") {
		t.Fatal("missing cooking support")
	}
	p.CombinedFoodSupply = domain.Unknown[policy.FoodSupply]()
	if _, known := reviewFoodPlan(p, policy.DefaultRoutinePolicy()).Value(); known {
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
	r := &RoutineBuildingPlanner{goal: policy.EnsureExpansion}
	p := observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(3)), IndoorCapacity: domain.Known(int64(3)), FoodPlan: domain.Known(policy.FoodPlan{GapPerDay: 1})}}
	if _, _, reason := r.selection(p); reason != BuildingMethodRefused {
		t.Fatal(reason)
	}
	p.Facts.FoodPlan = domain.Known(policy.FoodPlan{GapPerDay: 0})
	if _, _, reason := r.selection(p); reason != "" {
		t.Fatal(reason)
	}
}
