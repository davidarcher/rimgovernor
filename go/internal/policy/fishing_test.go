package policy

import (
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func fishingRequest() FishingRequest {
	return FishingRequest{Researched: domain.Known(true), Regions: []FishingRegion{{
		ID: "coast", Population: domain.Known(300.0), MaxPopulation: domain.Known(300.0),
		NutritionPerFish: domain.Known(0.25), FishPerBatch: domain.Known(6.0), WorkTicksPerBatch: domain.Known(7500.0),
		Reachable: domain.Known(true), Frozen: domain.Known(false), Open: domain.Known(false),
	}}}
}

func TestFishingDraw(t *testing.T) {
	for _, tc := range []struct{ maximum, population, nutrition, work float64 }{
		{300, 300, 1.875, 9375}, {100, 100, 0.625, 3125}, {300, 2, 0.5, 2500}, {0, 0, 0, 0},
	} {
		r := fishingRequest()
		r.Regions[0].Population, r.Regions[0].MaxPopulation = domain.Known(tc.population), domain.Known(tc.maximum)
		rows, err := FishingChannels(r)
		if err != nil || len(rows) != 1 {
			t.Fatalf("rows=%v err=%v", rows, err)
		}
		if rows[0].NutritionPerDay != domain.Known(tc.nutrition) || rows[0].WorkPerDay != domain.Known(tc.work) {
			t.Fatalf("maximum=%v population=%v: %+v", tc.maximum, tc.population, rows[0])
		}
		if rows[0].LeadDays != domain.Known(0.0) {
			t.Fatal("researched fishing has a research delay")
		}
	}
	rows, err := FishingChannels(FishingRequest{})
	if err != nil || len(rows) != 0 {
		t.Fatalf("Core has a fishing channel: %v %v", rows, err)
	}
}

func TestFishingAvailability(t *testing.T) {
	for _, tc := range []struct {
		name    string
		change  func(*FishingRequest)
		unknown bool
		term    string
	}{
		{"frozen", func(r *FishingRequest) { r.Regions[0].Frozen = domain.Known(true) }, false, "fishing_frozen"},
		{"unreachable", func(r *FishingRequest) { r.Regions[0].Reachable = domain.Known(false) }, false, "fishing_unreachable"},
		{"population unknown", func(r *FishingRequest) { r.Regions[0].Population = domain.Unknown[float64]() }, true, ""},
		{"nutrition unknown", func(r *FishingRequest) { r.Regions[0].NutritionPerFish = domain.Unknown[float64]() }, true, ""},
		{"freeze unknown", func(r *FishingRequest) { r.Regions[0].Frozen = domain.Unknown[bool]() }, true, ""},
		{"research unknown", func(r *FishingRequest) { r.Researched = domain.Unknown[bool]() }, true, ""},
		{"research lead unknown", func(r *FishingRequest) { r.Researched = domain.Known(false) }, true, ""},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := fishingRequest()
			r.Regions[0].Open = domain.Known(true)
			tc.change(&r)
			rows, err := FishingChannels(r)
			if err != nil {
				t.Fatal(err)
			}
			plan, err := PlanFood(foodPlanRequest(rows...))
			if err != nil {
				t.Fatal(err)
			}
			if plan.DeliveredPerDay != 0 || (len(plan.Unknown) == 1) != tc.unknown {
				t.Fatal(plan.Explain())
			}
			if tc.term != "" && !strings.Contains(plan.Explain(), tc.term) {
				t.Fatalf("missing availability reason: %s", plan.Explain())
			}
		})
	}
}

func TestFishingResearchAdmission(t *testing.T) {
	r := fishingRequest()
	r.Researched, r.ResearchLeadDays = domain.Known(false), domain.Known(2.0)
	rows, err := FishingChannels(r)
	if err != nil {
		t.Fatal(err)
	}
	if rows[0].LeadDays != domain.Known(2.0) || rows[0].Open != domain.Known(false) {
		t.Fatal(rows)
	}
	for _, runway := range []float64{1, 4} {
		request := foodPlanRequest(rows...)
		request.Demand.RunwayDays = domain.Known(runway)
		plan, err := PlanFood(request)
		if err != nil {
			t.Fatal(err)
		}
		want := ""
		if runway == 4 {
			want = "Fishing"
		}
		if got := FishingResearchRequest(plan, r.Researched); got != want {
			t.Fatalf("got %q want %q: %s", got, want, plan.Explain())
		}
		if FishingResearchRequest(plan, domain.Known(true)) != "" || FishingResearchRequest(plan, domain.Unknown[bool]()) != "" {
			t.Fatal("known completion or unknown research requested a project")
		}
	}
}

func TestFishingRejectsInvalidFacts(t *testing.T) {
	for _, mutate := range []func(*FishingRequest){
		func(r *FishingRequest) { r.Regions = append(r.Regions, r.Regions[0]) },
		func(r *FishingRequest) { r.Regions[0].ID = "" },
		func(r *FishingRequest) { r.Regions[0].Population = domain.Known(301.0) },
		func(r *FishingRequest) { r.Regions[0].Population = domain.Known(-1.0) },
		func(r *FishingRequest) { r.Regions[0].MaxPopulation = domain.Known(math.NaN()) },
		func(r *FishingRequest) { r.Regions[0].NutritionPerFish = domain.Known(math.Inf(1)) },
		func(r *FishingRequest) { r.Regions[0].FishPerBatch = domain.Known(0.0) },
		func(r *FishingRequest) { r.Regions[0].WorkTicksPerBatch = domain.Known(0.0) },
		func(r *FishingRequest) { r.ResearchLeadDays = domain.Known(-1.0) },
		func(r *FishingRequest) { r.Regions[0].NutritionPerFish = domain.Known(math.MaxFloat64) },
	} {
		r := fishingRequest()
		mutate(&r)
		if _, err := FishingChannels(r); err == nil {
			t.Fatalf("accepted invalid facts: %+v", r)
		}
	}
}
