package policy

import (
	"fmt"
	"math"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func foodPlanRequest(rows ...SupplyCandidate) FoodPlanRequest {
	return FoodPlanRequest{Demand: FoodForecast{RunwayDays: domain.Known(1.0), Consumers: []ConsumerFoodForecast{{ID: "pawn", NutritionPerDay: 10}}}, MinDays: 2, TargetDays: 4, Channels: domain.Known(rows), Labor: domain.Known(100000.0)}
}

func foodPlanChannel(id string, nutrition, work, lead float64, open bool) SupplyCandidate {
	c := FoodCandidate(CandidateForage, id, domain.Known(nutrition))
	c.LaborPerDay, c.LeadDays, c.State = domain.Known(work), domain.Known(lead), FoodState(domain.Known(open), domain.Unknown[bool]())
	return c
}

func TestFoodPlanTribalBridge(t *testing.T) {
	var sources []AcquisitionSource
	for i := 0; i < 5; i++ {
		sources = append(sources, AcquisitionSource{ID: fmt.Sprintf("berry-%d", i), Food: true, NutritionYield: 1})
	}
	for i := 0; i < 3; i++ {
		sources = append(sources, AcquisitionSource{ID: fmt.Sprintf("deer-%d", i), Food: true, Hunt: true, NutritionYield: 4})
	}
	var fields []FoodField
	for i := 0; i < 3; i++ {
		fields = append(fields, FoodField{ID: fmt.Sprintf("rice-%d", i), Plan: FieldPlan{Crop: CropChoice{Edible: domain.Known(true), HarvestNutrition: domain.Known(0.3), GrowDays: domain.Known(4.5)}, Sites: FarmSitePlan{Cells: 25}}, RemainingGrowDays: domain.Known(4.5), WorkPerDay: domain.Known(1000.0)})
	}
	for _, hunt := range []bool{true, false} {
		t.Run(fmt.Sprintf("hunt=%t", hunt), func(t *testing.T) {
			rows := ForageChannels(sources)
			if hunt {
				rows = append(rows, HuntCandidates(sources, SquadHuntMinGunners, domain.Fact[float64]{})...)
			}
			rows = append(rows, CropChannels(fields, CropKitchen{}, CropSeason{})...)
			r := foodPlanRequest(rows...)
			r.Demand.Consumers = nil
			for i := 0; i < 8; i++ {
				r.Demand.Consumers = append(r.Demand.Consumers, ConsumerFoodForecast{ID: PawnID(fmt.Sprintf("tribal-%d", i)), NutritionPerDay: 1.2})
			}
			p, err := SupplyFoodPlan(r)
			if err != nil {
				t.Fatal(err)
			}
			for _, e := range p.Portfolio {
				want := FoodPlanOpen
				if e.Channel.Kind == CandidateCrop {
					want = FoodPlanHold
					if lead, _ := e.Channel.LeadDays.Value(); lead != 4.5 {
						t.Fatal(lead)
					}
				}
				if e.Decision != want {
					t.Fatalf("%s", p.Explain())
				}
			}
			if hunt && p.GapPerDay > 0 || !hunt && p.GapPerDay <= 0 {
				t.Fatal(p.Explain())
			}
			if len(p.Unknown) != 0 {
				t.Fatal(p.Explain())
			}
		})
	}
}

func TestFoodPlanRankingAndBudget(t *testing.T) {
	for _, tc := range []struct {
		name      string
		rows      []SupplyCandidate
		labor     float64
		want      []FoodPlanDecision
		delivered float64
	}{
		{"lead before efficiency", []SupplyCandidate{foodPlanChannel("late", 20, 1, 1, false), foodPlanChannel("early", 20, 100, 0, false)}, 1000, []FoodPlanDecision{FoodPlanOpen, FoodPlanHold}, 20},
		{"cheaper equal lead", []SupplyCandidate{foodPlanChannel("costly", 20, 100, 0, false), foodPlanChannel("cheap", 20, 1, 0, false)}, 1000, []FoodPlanDecision{FoodPlanOpen, FoodPlanHold}, 20},
		{"excess labor held", []SupplyCandidate{foodPlanChannel("early", 10, 20, 0, false), foodPlanChannel("late", 10, 30, 1, false)}, 20, []FoodPlanDecision{FoodPlanOpen, FoodPlanHold}, 10},
		{"existing over budget stays", []SupplyCandidate{foodPlanChannel("open", 20, 30, 0, true), foodPlanChannel("new", 10, 1, 1, false)}, 20, []FoodPlanDecision{FoodPlanHold, FoodPlanHold}, 20},
		{"free delivery", []SupplyCandidate{foodPlanChannel("free", 20, 0, 0, false)}, 0, []FoodPlanDecision{FoodPlanOpen}, 20},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := foodPlanRequest(tc.rows...)
			r.Labor = domain.Known(tc.labor)
			p, err := SupplyFoodPlan(r)
			if err != nil {
				t.Fatal(err)
			}
			for i, e := range p.Portfolio {
				if e.Decision != tc.want[i] {
					t.Fatal(p.Explain())
				}
			}
			if p.DeliveredPerDay != tc.delivered {
				t.Fatal(p.Explain())
			}
			if strings.Contains(tc.name, "budget") || strings.Contains(tc.name, "labor") {
				if !strings.Contains(p.Explain(), "labor_excess") {
					t.Fatal(p.Explain())
				}
			}
		})
	}
}

func TestFoodPlanRiskAndStableOrder(t *testing.T) {
	risky := foodPlanChannel("a-risky", 20, 10, 0, false)
	risky.Risk = []CandidateRisk{{CandidateRevenge, 0.5}, {CandidateFrost, 0.25}}
	safe := foodPlanChannel("z-safe", 20, 20, 0, false)
	rows := []SupplyCandidate{risky, safe}
	p, err := SupplyFoodPlan(foodPlanRequest(rows...))
	if err != nil {
		t.Fatal(err)
	}
	if p.Portfolio[0].Channel.ID != "z-safe" || p.DeliveredPerDay != 20 {
		t.Fatal(p.Explain())
	}
	rows[0], rows[1] = rows[1], rows[0]
	reversed, err := SupplyFoodPlan(foodPlanRequest(rows...))
	if err != nil || !reflect.DeepEqual(p, reversed) {
		t.Fatalf("unstable: %v", err)
	}
	r := foodPlanRequest(risky)
	p, err = SupplyFoodPlan(r)
	if err != nil || p.DeliveredPerDay != 5 {
		t.Fatalf("%s %v", p.Explain(), err)
	}
	risky.Risk = append(risky.Risk, CandidateRisk{CandidateFallout, 1})
	p, err = SupplyFoodPlan(foodPlanRequest(risky))
	if err != nil || p.DeliveredPerDay != 0 || p.Portfolio[0].Decision != FoodPlanHold {
		t.Fatalf("%s %v", p.Explain(), err)
	}
}

func TestFoodPlanClosesLeastEfficientStrictSurplus(t *testing.T) {
	r := foodPlanRequest(foodPlanChannel("efficient", 20, 1, 0, true), foodPlanChannel("inefficient", 5, 100, 0, true))
	p, err := SupplyFoodPlan(r)
	if err != nil {
		t.Fatal(err)
	}
	if p.Portfolio[0].Decision != FoodPlanHold || p.Portfolio[1].Decision != FoodPlanClose || p.DeliveredPerDay != 20 {
		t.Fatal(p.Explain())
	}
	// Exact replacement is held: closure needs surplus strictly greater than
	// the channel contribution, preventing churn at the boundary.
	r.Demand.RunwayDays = domain.Known(0.0)
	p, err = SupplyFoodPlan(r)
	if err != nil || p.Portfolio[1].Decision != FoodPlanHold {
		t.Fatalf("%s %v", p.Explain(), err)
	}
}

// The live game (Foothold, 0.7 days of food): one moose hunt "covered" the
// gap and every other channel sat "target covered" while hunts kept failing.
// Under the starvation line an undelivered hunt covers nothing and every
// kind opens in parallel.
func TestFoodPlanEmergencyOpensChannelsInParallel(t *testing.T) {
	hunt := func(id string, n float64) SupplyCandidate {
		c := foodPlanChannel(id, n, 3900, 0, false)
		c.Kind = CandidateHunt
		return c
	}
	fish := foodPlanChannel("fish", 3, 5000, 0, false)
	fish.Kind = CandidateFishing
	rows := []SupplyCandidate{hunt("moose", 40), hunt("turkey", 4), foodPlanChannel("berries", 2, 2500, 0, false), fish}
	r := foodPlanRequest(rows...)
	p, err := SupplyFoodPlan(r)
	if err != nil || p.Portfolio[0].Channel.ID != "moose" || p.Portfolio[0].Decision != FoodPlanOpen || p.GapPerDay > 0 {
		t.Fatalf("baseline: the moose alone covers: %s %v", p.Explain(), err)
	}
	for _, e := range p.Portfolio[1:] {
		if e.Decision != FoodPlanHold {
			t.Fatalf("baseline: %s", p.Explain())
		}
	}
	r.EmergencyDays = 3
	p, err = SupplyFoodPlan(r)
	if err != nil {
		t.Fatal(err)
	}
	kinds := map[CandidateKind]bool{}
	for _, e := range p.Portfolio {
		if e.Decision != FoodPlanOpen {
			t.Fatalf("emergency: every channel opens: %s", p.Explain())
		}
		kinds[e.Channel.Kind] = true
	}
	if len(kinds) != 3 || p.DeliveredPerDay != 0 || p.GapPerDay <= 0 {
		t.Fatalf("emergency: nothing is credited until delivering: %s", p.Explain())
	}
	// A channel observed delivering keeps its credit.
	delivering := append([]SupplyCandidate(nil), rows...)
	delivering[3].State = domain.Known(CandidateDelivering)
	dr := r
	dr.Channels = domain.Known(delivering)
	if p, err = SupplyFoodPlan(dr); err != nil || p.DeliveredPerDay != 3 {
		t.Fatalf("emergency: only the delivering fishing is credited: %s %v", p.Explain(), err)
	}
	// The labor budget still binds, breadth first: one row per kind.
	r.Labor = domain.Known(3900 + 2500 + 5000.0)
	p, err = SupplyFoodPlan(r)
	if err != nil {
		t.Fatal(err)
	}
	open := map[string]bool{}
	for _, e := range p.Portfolio {
		open[e.Channel.ID] = e.Decision == FoodPlanOpen
	}
	if !open["moose"] || !open["berries"] || !open["fish"] || open["turkey"] {
		t.Fatalf("emergency budget: %s", p.Explain())
	}
	// Above the line the old accounting holds.
	r.Demand.RunwayDays = domain.Known(3.0)
	if p, err = SupplyFoodPlan(r); err != nil || p.Portfolio[0].Channel.ID != "moose" || p.GapPerDay > 0 {
		t.Fatalf("above the line: %s %v", p.Explain(), err)
	}
}

func TestFoodPlanReserveAndLeadHorizon(t *testing.T) {
	for _, tc := range []struct {
		runway, reserve, lead, wantGap float64
		decision                       FoodPlanDecision
	}{
		{4, 0, 4, 0, FoodPlanOpen},
		{4, 3, 4, 17.5, FoodPlanHold},
		{1, 2, 0, 10, FoodPlanOpen},
		{1, 0, 1, 7.5, FoodPlanOpen},
		{1, 0, 1.01, 17.5, FoodPlanHold},
	} {
		r := foodPlanRequest(foodPlanChannel("crop", 10, 1, tc.lead, false))
		r.Demand.RunwayDays = domain.Known(tc.runway)
		r.ReserveDays = tc.reserve
		p, err := SupplyFoodPlan(r)
		if err != nil || p.GapPerDay != tc.wantGap || p.Portfolio[0].Decision != tc.decision {
			t.Fatalf("%+v: %s %v", tc, p.Explain(), err)
		}
	}
}

func TestFoodPlanUnknownAndInvalid(t *testing.T) {
	unknown := foodPlanChannel("unknown", 20, 10, 0, false)
	unknown.Yields[0].PerDay = domain.Unknown[float64]()
	p, err := SupplyFoodPlan(foodPlanRequest(unknown))
	if err != nil || len(p.Portfolio) != 0 || len(p.Unknown) != 1 || !strings.Contains(p.Unknown[0].Reason, "nutrition_per_day") || p.DeliveredPerDay != 0 {
		t.Fatalf("%s %v", p.Explain(), err)
	}
	for _, tc := range []struct {
		name   string
		change func(*FoodPlanRequest)
	}{
		{"channels unknown", func(r *FoodPlanRequest) { r.Channels = domain.Unknown[[]SupplyCandidate]() }},
		{"runway unknown", func(r *FoodPlanRequest) { r.Demand.RunwayDays = domain.Unknown[float64]() }},
		{"negative labor", func(r *FoodPlanRequest) { r.Labor = domain.Known(-1.0) }},
		{"invalid thresholds", func(r *FoodPlanRequest) { r.TargetDays = r.MinDays }},
		{"nan reserve", func(r *FoodPlanRequest) { r.ReserveDays = math.NaN() }},
		{"zero demand", func(r *FoodPlanRequest) { r.Demand.Consumers[0].NutritionPerDay = 0 }},
		{"duplicate consumer", func(r *FoodPlanRequest) { r.Demand.Consumers = append(r.Demand.Consumers, r.Demand.Consumers[0]) }},
		{"demand overflow", func(r *FoodPlanRequest) { r.Demand.Consumers[0].NutritionPerDay = math.MaxFloat64 }},
		{"duplicate channel", func(r *FoodPlanRequest) {
			rows, _ := r.Channels.Value()
			r.Channels = domain.Known(append(rows, rows[0]))
		}},
		{"invalid id", func(r *FoodPlanRequest) { rows, _ := r.Channels.Value(); rows[0].ID = " " }},
		{"invalid kind", func(r *FoodPlanRequest) { rows, _ := r.Channels.Value(); rows[0].Kind = "bad" }},
		{"invalid known beside unknown", func(r *FoodPlanRequest) {
			rows, _ := r.Channels.Value()
			rows[0].LaborPerDay = domain.Known(math.NaN())
		}},
		{"negative lead", func(r *FoodPlanRequest) { rows, _ := r.Channels.Value(); rows[0].LeadDays = domain.Known(-1.0) }},
		{"infinite nutrition", func(r *FoodPlanRequest) {
			rows, _ := r.Channels.Value()
			rows[0].Yields[0].PerDay = domain.Known(math.Inf(1))
		}},
		{"invalid risk", func(r *FoodPlanRequest) {
			rows, _ := r.Channels.Value()
			rows[0].Risk = []CandidateRisk{{CandidateBlight, 1.1}}
		}},
		{"duplicate risk", func(r *FoodPlanRequest) {
			rows, _ := r.Channels.Value()
			rows[0].Risk = []CandidateRisk{{CandidateBlight, 0.1}, {CandidateBlight, 0.2}}
		}},
		{"invalid term", func(r *FoodPlanRequest) {
			rows, _ := r.Channels.Value()
			rows[0].Terms = []CandidateTerm{{"work", math.Inf(1)}}
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := foodPlanRequest(unknown)
			tc.change(&r)
			if _, err := SupplyFoodPlan(r); err == nil {
				t.Fatal("accepted invalid request")
			}
		})
	}
}

func TestFoodPlanAdaptersAndInputOwnership(t *testing.T) {
	sources := []AcquisitionSource{{ID: "plant", Food: true, NutritionYield: 2, Designated: true}, {ID: "deer", Food: true, Hunt: true, NutritionYield: 8}, {ID: "pest", Hunt: true}, {ID: "tree", Tree: true}}
	f, h := ForageChannels(sources), HuntCandidates(sources, SquadHuntMinGunners, domain.Fact[float64]{})
	if len(f) != 1 || len(h) != 1 {
		t.Fatalf("%+v %+v", f, h)
	}
	if open, _ := f[0].Open().Value(); open || f[0].State != domain.Known(CandidateDesignated) {
		t.Fatal("designation counted as delivery")
	}
	field := FoodField{ID: "rice", Plan: FieldPlan{Crop: CropChoice{Edible: domain.Known(true), GrowDays: domain.Known(4.5), HarvestNutrition: domain.Known(0.3)}, Sites: FarmSitePlan{Cells: 30}}, RemainingGrowDays: domain.Known(2.0), WorkPerDay: domain.Known(100.0)}
	c := CropChannels([]FoodField{field}, CropKitchen{}, CropSeason{})[0]
	if n, _ := c.Nutrition().PerDay.Value(); n != 2 {
		t.Fatal(n)
	}
	if lead, _ := c.LeadDays.Value(); lead != 2 {
		t.Fatal(lead)
	}
	field.Plan.Crop.GrowDays = domain.Unknown[float64]()
	if _, known := CropChannels([]FoodField{field}, CropKitchen{}, CropSeason{})[0].Nutrition().PerDay.Value(); known {
		t.Fatal("invented growth facts")
	}
	field.Plan.Crop.GrowDays = domain.Known(0.0)
	if _, err := SupplyFoodPlan(foodPlanRequest(CropChannels([]FoodField{field}, CropKitchen{}, CropSeason{})...)); err == nil {
		t.Fatal("invalid crop accepted")
	}
	p, err := SupplyFoodPlan(foodPlanRequest(h...))
	if err != nil {
		t.Fatal(err)
	}
	p.Portfolio[0].Channel.Risk[0].Weight = 1
	p.Portfolio[0].Channel.Terms[0].Value = 99
	if h[0].Risk[0].Weight != 0 || h[0].Terms[0].Value == 99 {
		t.Fatal("plan aliases caller data")
	}
}
