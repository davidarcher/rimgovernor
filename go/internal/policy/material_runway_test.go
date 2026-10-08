package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func materialRunways(steel, components int64) []ResourceRunway {
	use := domain.Known(ResourceConsumption{WindowDays: 1, Recurring: map[Resource]int64{"Steel": 10, ComponentResource: 4}})
	return []ResourceRunway{
		ForecastResourceRunway("Steel", domain.Known(steel), domain.Known(int64(0)), 70, 60000, use),
		ForecastResourceRunway(ComponentResource, domain.Known(components), domain.Known(int64(0)), 0, 60000, use),
	}
}

func componentMethod() ResourceMethod {
	recipe := GearRecipe{Definition: "MakeComponent", Products: []Resource{ComponentResource}, Available: domain.Known(true), AvailableOn: domain.Known(true),
		Ingredients: domain.Known([][]Amount{{{Resource: "Steel", Count: 12}}}), RequiredWork: domain.Known([]WorkRequirement{})}
	got, _ := SelectResourceMethod(ResourceMethodRequest{Resource: ComponentResource, Target: 20,
		Benches: domain.Known([]GearBench{{ID: "fabrication", Bills: domain.Known([]GearBill{}), Recipes: domain.Known([]GearRecipe{recipe})}})})
	return got
}

func componentSupply(t *testing.T, runways []ResourceRunway, available domain.Fact[int64], deficit int64) SupplyEntry {
	t.Helper()
	method := componentMethod()
	candidate, ok := ProduceCandidate(method, deficit, runways)
	if !ok || len(candidate.UpfrontCost.Resources) != 1 || candidate.UpfrontCost.Resources[0].Count != 12*deficit {
		t.Fatalf("the recipe's steel is the candidate's upfront cost: %+v %v", candidate, method)
	}
	plan, err := PlanResourceSupply([]ResourceSupplyInput{{Resource: ComponentResource, Deficit: deficit, Candidates: []SupplyCandidate{candidate},
		Usable: UsableIngredients(runways, []Stock{{Resource: "Steel", Available: available}})}}, domain.Known(1e6))
	if err != nil {
		t.Fatal(err)
	}
	if len(plan.Plan.Portfolio) == 1 {
		return plan.Plan.Portfolio[0]
	}
	return plan.Plan.Unknown[0]
}

func TestComponentBillSteelDrawIsPriced(t *testing.T) {
	// 157 steel, a 70 reserve and 10 a day for 5 days protect 120: 37 are
	// spendable, three components of 12.
	for _, tc := range []struct {
		name      string
		runways   []ResourceRunway
		available domain.Fact[int64]
		decision  SupplyDecision
		units     float64
	}{
		{"partial", materialRunways(157, 2), domain.Known(int64(157)), SupplyOpen, 3},
		{"census below stock", materialRunways(157, 2), domain.Known(int64(132)), SupplyOpen, 1},
		{"under one component", materialRunways(157, 2), domain.Known(int64(131)), SupplyHold, 0},
		{"stock fell", materialRunways(120, 2), domain.Known(int64(157)), SupplyHold, 0},
	} {
		t.Run(tc.name, func(t *testing.T) {
			e := componentSupply(t, tc.runways, tc.available, 18)
			if e.Decision != tc.decision {
				t.Fatalf("%s", e.Reason)
			}
			got := 0.0
			for _, c := range e.Credit {
				got += c.Amount
			}
			if got != tc.units {
				t.Fatalf("admitted %v, want %v", got, tc.units)
			}
			if tc.decision == SupplyHold && e.Reason != "ingredient_unavailable" {
				t.Fatal(e.Reason)
			}
		})
	}
}

func TestComponentBillUnknownIngredients(t *testing.T) {
	runways := materialRunways(157, 2)
	if e := componentSupply(t, runways, domain.Unknown[int64](), 18); e.Decision == SupplyOpen || e.Reason != "unknown: steel_usable" {
		t.Fatalf("unknown census: %+v", e)
	}
	unread := []ResourceRunway{ForecastResourceRunway("Steel", domain.Known(int64(157)), domain.Known(int64(0)), 70, 0, domain.Unknown[ResourceConsumption]())}
	candidate, _ := ProduceCandidate(componentMethod(), 18, unread)
	if len(candidate.UpfrontCost.Resources) != 1 {
		t.Fatal("a runway with an unread rate still declares the draw")
	}
	plan, err := PlanResourceSupply([]ResourceSupplyInput{{Resource: ComponentResource, Deficit: 18, Candidates: []SupplyCandidate{candidate}, Usable: UsableIngredients(unread, nil)}}, domain.Known(1e6))
	if err != nil || len(plan.Plan.Unknown) != 1 {
		t.Fatalf("unread rate is unknown: %s %v", plan.Plan.Explain(), err)
	}
}

func TestIngredientDrawsCompete(t *testing.T) {
	steel := ResourceKey{Def: "Steel"}
	bill := func(id string, units int64) SupplyCandidate {
		c := SourceCandidate(CandidateProduce, id, domain.Known(60.0), domain.Known(0.0), false, 0,
			SourceYield(ResourceKey{Def: Resource(id)}, units, 0, domain.Unknown[int64]()))
		c.UpfrontCost.Resources = []ResourceQuantity{{Key: steel, Count: 12 * units}}
		return c
	}
	plan, err := PlanSupply(SupplyPlanRequest{
		Demands:    domain.Known([]SupplyDemand{{Good: ResourceKey{Def: "A"}, Units: 3, Priority: 1}, {Good: ResourceKey{Def: "B"}, Units: 3, Priority: 1}}),
		Candidates: domain.Known([]SupplyCandidate{bill("A", 3), bill("B", 3)}), Labor: domain.Known(1e6),
		Usable: []ResourceQuantity{{Key: steel, Count: 48}},
	})
	if err != nil {
		t.Fatal(err)
	}
	if got := plan.Delivered(ResourceKey{Def: "A"}) + plan.Delivered(ResourceKey{Def: "B"}); got != 4 {
		t.Fatalf("48 steel is four crafts across both bills: %s", plan.Explain())
	}
}

func TestMaterialRunwayProjection(t *testing.T) {
	got, ok := PlanMaterialRunway(materialRunways(30, 2), CoreItemFacts()).Value()
	// Steel covers 3 days, components half a day: the larger shortfall is 4.5.
	if !ok || got.ShortfallDays != 4.5 || len(got.Resources) != 2 {
		t.Fatal(got, ok)
	}
	if shortfall, why := shadowShortfall(ProjectForward(ForwardInputs{Runways: materialRunways(30, 2), Items: CoreItemFacts()}), ShadowResources); why != "" || shortfall != 4.5 {
		t.Fatal(shortfall, why)
	}
	if _, ok := PlanMaterialRunway(materialRunways(30, 2)[:1], CoreItemFacts()).Value(); !ok {
		t.Fatal("steel alone is a projection")
	}
	if _, ok := PlanMaterialRunway(nil, CoreItemFacts()).Value(); ok {
		t.Fatal("no row is unknown")
	}
	unread := ForecastResourceRunway("Steel", domain.Known(int64(30)), domain.Known(int64(0)), 0, 0, domain.Unknown[ResourceConsumption]())
	if _, ok := PlanMaterialRunway([]ResourceRunway{unread}, CoreItemFacts()).Value(); ok {
		t.Fatal("unread consumption is unknown")
	}
	if _, why := shadowShortfall(ProjectForward(ForwardInputs{}), ShadowResources); why == "" {
		t.Fatal("no inputs is unranked")
	}
}
