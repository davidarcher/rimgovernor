package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func caravanCargoRequest() CaravanCargoRequest {
	crew := []domain.PawnID{"alpha", "beta"}
	all := []domain.PawnID{"alpha", "beta", "gamma", "delta"}
	return CaravanCargoRequest{
		Crew:  crew,
		Cargo: []domain.CargoItem{{Definition: "WoodLog", Count: 50}},
		Groups: []CaravanCargoGroup{
			{GroupID: "g-wood", Definition: "WoodLog", Count: 75},
			// 0.05 nutrition per unit: never worth packing but part of the home runway.
			{GroupID: "g-rice", Definition: "RawRice", Count: 400, Nutrition: domain.Known(0.05), Perishable: true, RotDays: domain.Known(40.0), Eaters: all},
			{GroupID: "g-meals", Definition: "MealSimple", Count: 30, Nutrition: domain.Known(0.9), Perishable: true, RotDays: domain.Known(3.5), Eaters: all},
			{GroupID: "g-pemmican", Definition: "Pemmican", Count: 20, Nutrition: domain.Known(0.8), Perishable: true, RotDays: domain.Known(60.0), Reserve: true, Eaters: all},
			{GroupID: "g-survival", Definition: "MealSurvivalPack", Count: 12, Nutrition: domain.Known(0.9), Eaters: all},
			// Alpha's food restriction excludes kibble: no crew-wide eligibility.
			{GroupID: "g-kibble", Definition: "Kibble", Count: 100, Nutrition: domain.Known(0.05), Eaters: []domain.PawnID{"beta", "gamma", "delta"}},
		},
		Demand:          map[domain.PawnID]float64{"alpha": 1.6, "beta": 1.6, "gamma": 1.6, "delta": 1.6},
		JourneyDays:     7,
		HomeFoodMinDays: 5,
	}
}

// TestPlanCaravanCargoReserveFirst: the reserve is packed ahead of survival
// meals and simple meals never board a week-long journey (#464).
func TestPlanCaravanCargoReserveFirst(t *testing.T) {
	plan, reason := PlanCaravanCargo(caravanCargoRequest())
	if reason != "" {
		t.Fatal(reason)
	}
	// Demand 3.2/day over 7 days = 22.4 nutrition: 20 pemmican (16) then 8 survival meals (7.2).
	want := []CaravanFoodCargo{{"g-pemmican", 20}, {"g-survival", 8}}
	if len(plan.Food) != len(want) {
		t.Fatal(plan.Food)
	}
	for i := range want {
		if plan.Food[i] != want[i] {
			t.Fatal(plan.Food)
		}
	}
	wantCargo := []CaravanCargoSelection{{"g-wood", "WoodLog", 50}, {"g-pemmican", "Pemmican", 20}, {"g-survival", "MealSurvivalPack", 8}}
	if len(plan.Cargo) != len(wantCargo) {
		t.Fatal(plan.Cargo)
	}
	for i := range wantCargo {
		if plan.Cargo[i] != wantCargo[i] {
			t.Fatal(plan.Cargo)
		}
	}
	// Home keeps rice (20) + meals (27) + 4 survival meals (3.6) + kibble (5) = 55.6 over 3.2/day.
	if plan.HomeRunwayDays < 17.3 || plan.HomeRunwayDays > 17.4 {
		t.Fatal(plan.HomeRunwayDays)
	}
}

func TestPlanCaravanCargoRefusals(t *testing.T) {
	cases := []struct {
		name   string
		change func(*CaravanCargoRequest)
		reason Reason
	}{
		{"no crew", func(r *CaravanCargoRequest) { r.Crew = nil }, UnknownFacts},
		{"duplicate crew", func(r *CaravanCargoRequest) { r.Crew = []domain.PawnID{"alpha", "alpha"} }, UnknownFacts},
		{"crew without demand", func(r *CaravanCargoRequest) { delete(r.Demand, "alpha") }, UnknownFacts},
		{"zero journey", func(r *CaravanCargoRequest) { r.JourneyDays = 0 }, UnknownFacts},
		{"cargo not listed", func(r *CaravanCargoRequest) { r.Cargo = []domain.CargoItem{{Definition: "Steel", Count: 1}} }, UnknownFacts},
		{"cargo beyond stock", func(r *CaravanCargoRequest) { r.Cargo[0].Count = 76 }, UnknownFacts},
		{"duplicate definition", func(r *CaravanCargoRequest) { r.Groups[0].Definition = "RawRice" }, UnknownFacts},
		{"journey outlasts food", func(r *CaravanCargoRequest) { r.JourneyDays = 45 }, CaravanFoodInsufficient},
		{"home floor", func(r *CaravanCargoRequest) { r.HomeFoodMinDays = 18 }, CaravanHomeFoodInsufficient},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := caravanCargoRequest()
			c.change(&r)
			if _, reason := PlanCaravanCargo(r); reason != c.reason {
				t.Fatalf("got %q want %q", reason, c.reason)
			}
		})
	}
}

// TestPlanCaravanCargoReserveOutsideRunway: the forbidden reserve never
// counted toward the home runway, so packing it costs the floor nothing while
// packing unreserved food does.
func TestPlanCaravanCargoReserveOutsideRunway(t *testing.T) {
	r := caravanCargoRequest()
	r.Groups[3].Reserve = false
	r.HomeFoodMinDays = 18
	if _, reason := PlanCaravanCargo(r); reason != CaravanHomeFoodInsufficient {
		t.Fatal(reason)
	}
	r = caravanCargoRequest()
	r.Groups[3].Reserve = false
	plan, reason := PlanCaravanCargo(r)
	if reason != "" {
		t.Fatal(reason)
	}
	// Unreserved pemmican sorts behind the non-perishable survival meals.
	if len(plan.Food) != 2 || plan.Food[0].GroupID != "g-survival" || plan.Food[1].GroupID != "g-pemmican" {
		t.Fatal(plan.Food)
	}
}

// TestPlanCaravanCargoWholeCrewEligibility: a group one crew member cannot
// eat is never packed even when it would cover the journey alone.
func TestPlanCaravanCargoWholeCrewEligibility(t *testing.T) {
	r := caravanCargoRequest()
	r.Groups[5].Nutrition = domain.Known(5.0)
	plan, reason := PlanCaravanCargo(r)
	if reason != "" {
		t.Fatal(reason)
	}
	for _, line := range plan.Food {
		if line.GroupID == "g-kibble" {
			t.Fatal(plan.Food)
		}
	}
}

// TestPlanCaravanCargoNoHomeColonists: an empty home map has no floor to keep.
func TestPlanCaravanCargoNoHomeColonists(t *testing.T) {
	r := caravanCargoRequest()
	r.Demand = map[domain.PawnID]float64{"alpha": 1.6, "beta": 1.6}
	r.HomeFoodMinDays = 1000
	plan, reason := PlanCaravanCargo(r)
	if reason != "" || plan.HomeRunwayDays < 1e300 {
		t.Fatal(reason, plan.HomeRunwayDays)
	}
}
