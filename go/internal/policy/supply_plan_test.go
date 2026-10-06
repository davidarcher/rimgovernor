package policy

import (
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The PlanFood and resource ranking tests run against both the old
// functions and PlanSupply through the adapters below: PlanSupply reproduces
// them until the cut-overs (#2156, #2160) delete the old functions.

type rankResourceFunc func(domain.Fact[[]ResourceDemand], []AcquisitionCandidate, AcquisitionCompetition) ([]AcquisitionScore, error)
type scoreResourceFunc func(domain.Fact[[]ResourceDemand], AcquisitionCandidate, AcquisitionCompetition) (AcquisitionScore, error)

func eachFoodPlanner(t *testing.T, f func(*testing.T, func(FoodPlanRequest) (FoodPlan, error))) {
	for name, plan := range map[string]func(FoodPlanRequest) (FoodPlan, error){"PlanFood": PlanFood, "PlanSupply": planFoodBySupply} {
		t.Run(name, func(t *testing.T) { f(t, plan) })
	}
}

func eachResourceRanker(t *testing.T, f func(*testing.T, rankResourceFunc, scoreResourceFunc)) {
	t.Run("RankResourceCandidates", func(t *testing.T) { f(t, RankResourceCandidates, ScoreResourceCandidate) })
	t.Run("PlanSupply", func(t *testing.T) { f(t, rankBySupply, scoreBySupply) })
}

// planFoodBySupply is PlanFood's request and plan expressed through PlanSupply.
func planFoodBySupply(r FoodPlanRequest) (FoodPlan, error) {
	demand, err := NutritionDemand(NutritionDemandInput{Forecast: r.Demand, ReserveDays: r.ReserveDays, MinDays: r.MinDays, TargetDays: r.TargetDays, EmergencyDays: r.EmergencyDays})
	if err != nil {
		return FoodPlan{}, ErrFoodPlanFacts
	}
	req := SupplyPlanRequest{Demands: domain.Known([]SupplyDemand{demand}), Labor: r.Labor, Candidates: domain.Unknown[[]SupplyCandidate]()}
	if rows, known := r.Channels.Value(); known {
		var cands []SupplyCandidate
		for _, c := range rows {
			cands = append(cands, SupplyCandidateOfFood(c))
		}
		req.Candidates = domain.Known(cands)
	}
	plan, err := PlanSupply(req)
	if err != nil {
		return FoodPlan{}, ErrFoodPlanFacts
	}
	out := FoodPlan{Forecast: r.Demand, GapPerDay: plan.Gap(NutritionKey)}
	for _, c := range r.Demand.Consumers {
		out.DemandPerDay += c.NutritionPerDay
	}
	convert := func(rows []SupplyEntry) (entries []FoodPlanEntry) {
		for _, e := range rows {
			channel, ok := FoodChannelOfSupply(e.Candidate)
			if !ok {
				panic("not a food channel")
			}
			entry := FoodPlanEntry{Channel: channel, Decision: FoodPlanDecision(e.Decision), Reason: e.Reason}
			for _, t := range e.Terms {
				entry.Terms = append(entry.Terms, FoodPlanTerm{Name: t.Name, Value: t.Value})
			}
			for _, c := range e.Credit {
				entry.DeliveredPerDay += c.Amount
			}
			entries = append(entries, entry)
		}
		return entries
	}
	out.Portfolio, out.Unknown = convert(plan.Portfolio), convert(plan.Unknown)
	out.DeliveredPerDay = plan.Delivered(NutritionKey)
	return out, nil
}

func planAcquisitions(demand domain.Fact[[]ResourceDemand], cands []AcquisitionCandidate, c AcquisitionCompetition) (SupplyPlan, error) {
	req := SupplyPlanRequest{Demands: domain.Unknown[[]SupplyDemand](), Labor: domain.Known(1e12), UrgentPriority: c.UrgentPriority}
	if rows, known := demand.Value(); known {
		demands := []SupplyDemand{}
		for _, d := range rows {
			demands = append(demands, SupplyDemandOfResource(d))
		}
		req.Demands = domain.Known(demands)
	}
	var supply []SupplyCandidate
	for _, a := range cands {
		supply = append(supply, SupplyCandidateOfAcquisition(a))
	}
	req.Candidates = domain.Known(supply)
	return PlanSupply(req)
}

// scoresOfSupply reads a plan back as acquisition scores in rank order: the
// holds an acquisition scores by itself are score 0 with a Hold, the rest
// carry the independent score.
func scoresOfSupply(p SupplyPlan) []AcquisitionScore {
	var out []AcquisitionScore
	for _, e := range append(append([]SupplyEntry(nil), p.Portfolio...), p.Unknown...) {
		kind, _ := acquisitionKindOf(e.Candidate.Kind)
		s := AcquisitionScore{ID: e.Candidate.ID, Kind: kind, Wanted: e.Wanted, Trips: e.Trips, Value: e.Value}
		switch {
		case strings.HasPrefix(e.Reason, "unknown"):
			s.Hold = "unknown_demand_or_cost"
		case e.Reason == "no_demand", e.Reason == "no_storage_headroom", e.Reason == "competing_urgent_work":
			s.Hold = e.Reason
		default:
			s.Score = e.Score
		}
		out = append(out, s)
	}
	return out
}

func rankBySupply(demand domain.Fact[[]ResourceDemand], cands []AcquisitionCandidate, c AcquisitionCompetition) ([]AcquisitionScore, error) {
	p, err := planAcquisitions(demand, cands, c)
	if err != nil {
		return nil, err
	}
	var ranked []AcquisitionScore
	for _, s := range scoresOfSupply(p) {
		if s.Score > 0 {
			ranked = append(ranked, s)
		}
	}
	return ranked, nil
}

func scoreBySupply(demand domain.Fact[[]ResourceDemand], cand AcquisitionCandidate, c AcquisitionCompetition) (AcquisitionScore, error) {
	p, err := planAcquisitions(demand, []AcquisitionCandidate{cand}, c)
	if err != nil {
		return AcquisitionScore{}, err
	}
	return scoresOfSupply(p)[0], nil
}

// A deer is meat toward nutrition and leather toward a stock demand; its labor
// is charged once, so it beats a fish that only feeds nutrition.
func TestPlanSupplyMultiYieldChargesLaborOnce(t *testing.T) {
	leather := ResourceKey{Def: "Leather"}
	deer := SupplyCandidate{Kind: CandidateHunt, ID: "deer", State: domain.Known(CandidateClosed), LeadDays: domain.Known(0.0), LaborPerDay: domain.Known(8000.0),
		Yields: []CandidateYield{{Good: NutritionKey, PerDay: domain.Known(10.0)}, {Good: leather, PerDay: domain.Known(3.0)}}}
	fish := SupplyCandidate{Kind: CandidateFishing, ID: "fish", State: domain.Known(CandidateClosed), LeadDays: domain.Known(0.0), LaborPerDay: domain.Known(5000.0),
		Yields: []CandidateYield{{Good: NutritionKey, PerDay: domain.Known(10.0)}}}
	demands := []SupplyDemand{{Good: NutritionKey, PerDay: 10, Priority: NutritionPriority, HorizonDays: 4, Cover: 1}, {Good: leather, Units: 3, Priority: 2, HorizonDays: 4}}
	p, err := PlanSupply(SupplyPlanRequest{Demands: domain.Known(demands), Candidates: domain.Known([]SupplyCandidate{deer, fish}), Labor: domain.Known(8000.0)})
	if err != nil {
		t.Fatal(err)
	}
	by := map[string]SupplyEntry{}
	for _, e := range p.Portfolio {
		by[e.Candidate.ID] = e
	}
	if by["fish"].Decision != SupplyOpen || by["deer"].Reason != "labor budget" {
		t.Fatalf("cheaper fish opens first: %s", p.Explain())
	}
	p, err = PlanSupply(SupplyPlanRequest{Demands: domain.Known(demands), Candidates: domain.Known([]SupplyCandidate{deer}), Labor: domain.Known(8000.0)})
	if err != nil || p.Portfolio[0].Decision != SupplyOpen || p.Delivered(NutritionKey) != 10 || p.Delivered(leather) != 3 || len(p.Portfolio[0].Credit) != 2 {
		t.Fatalf("both yields count under one labor charge: %s %v", p.Explain(), err)
	}
}

// A stock cap bounds a flow's cumulative contribution; an upfront cost is
// amortised over the horizon.
func TestPlanSupplyStockCapAndUpfrontCost(t *testing.T) {
	wood := ResourceKey{Def: "WoodLog"}
	grove := SupplyCandidate{Kind: CandidateChop, ID: "grove", State: domain.Known(CandidateClosed), LeadDays: domain.Known(0.0), LaborPerDay: domain.Known(100.0),
		UpfrontCost: CandidateCost{LaborTicks: domain.Known(4000.0)},
		Yields:      []CandidateYield{{Good: wood, PerDay: domain.Known(50.0), StockCap: domain.Known(int64(80))}}}
	demand := SupplyDemand{Good: wood, PerDay: 50, Priority: 2, HorizonDays: 4}
	p, err := PlanSupply(SupplyPlanRequest{Demands: domain.Known([]SupplyDemand{demand}), Candidates: domain.Known([]SupplyCandidate{grove}), Labor: domain.Known(1100.0)})
	if err != nil || p.Portfolio[0].Decision != SupplyOpen || p.Delivered(wood) != 20 {
		t.Fatalf("cap 80 over a 4 day window is 20/day: %s %v", p.Explain(), err)
	}
	// 100 + 4000/4 = 1100 per day is exactly the budget; a day less does not fit.
	p, _ = PlanSupply(SupplyPlanRequest{Demands: domain.Known([]SupplyDemand{demand}), Candidates: domain.Known([]SupplyCandidate{grove}), Labor: domain.Known(1099.0)})
	if p.Portfolio[0].Reason != "labor budget" {
		t.Fatalf("%s", p.Explain())
	}
}

// Demand with no matching yield, and unknown demand, hold with their own words.
func TestPlanSupplyHoldWordsForDemand(t *testing.T) {
	steel := ResourceKey{Def: "Steel"}
	mine := SupplyCandidate{Kind: CandidateMining, ID: "ore", State: domain.Known(CandidateClosed), LeadDays: domain.Known(0.0), PathDistance: domain.Known(5.0),
		UpfrontCost: CandidateCost{LaborTicks: domain.Known(100.0)}, Yields: []CandidateYield{{Good: steel, StockCap: domain.Known(int64(10))}}}
	p, err := PlanSupply(SupplyPlanRequest{Demands: domain.Known([]SupplyDemand{{Good: ResourceKey{Def: "Gold"}, Units: 5, Priority: 1}}), Candidates: domain.Known([]SupplyCandidate{mine}), Labor: domain.Known(1000.0)})
	if err != nil || p.Portfolio[0].Reason != "no_demand" || p.Gap(ResourceKey{Def: "Gold"}) != 5 {
		t.Fatalf("%s %v", p.Explain(), err)
	}
	p, err = PlanSupply(SupplyPlanRequest{Demands: domain.Unknown[[]SupplyDemand](), Candidates: domain.Known([]SupplyCandidate{mine}), Labor: domain.Known(1000.0)})
	if err != nil || len(p.Portfolio) != 0 || len(p.Unknown) != 1 || p.Unknown[0].Reason != "unknown_demand_or_cost" {
		t.Fatalf("%s %v", p.Explain(), err)
	}
	for _, bad := range []SupplyDemand{{Good: steel, PerDay: 1, Units: 1, Priority: 1}, {Good: steel, Units: 1, Priority: 0}, {Good: steel, PerDay: math.NaN(), Priority: 1}} {
		if _, err := PlanSupply(SupplyPlanRequest{Demands: domain.Known([]SupplyDemand{bad}), Candidates: domain.Known([]SupplyCandidate{mine}), Labor: domain.Known(1000.0)}); err == nil {
			t.Fatalf("accepted %+v", bad)
		}
	}
}
