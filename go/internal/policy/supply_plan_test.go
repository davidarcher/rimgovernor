package policy

import (
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The resource ranking tests run through PlanSupply: supplyScore reads a plan
// entry back as a candidate's score, hold and demanded units.
type supplyScore struct {
	ID     string
	Kind   CandidateKind
	Score  float64
	Wanted int64
	Trips  int64
	Value  float64
	Hold   string
}

func planAcquisitions(demand domain.Fact[[]ResourceDemand], cands []SupplyCandidate, c AcquisitionCompetition) (SupplyPlan, error) {
	req := SupplyPlanRequest{Demands: domain.Unknown[[]SupplyDemand](), Labor: domain.Known(1e12), UrgentPriority: c.UrgentPriority}
	if rows, known := demand.Value(); known {
		demands := []SupplyDemand{}
		for _, d := range rows {
			demands = append(demands, SupplyDemandOfResource(d))
		}
		req.Demands = domain.Known(demands)
	}
	req.Candidates = domain.Known(cands)
	return PlanSupply(req)
}

// scoresOfSupply reads a plan back as acquisition scores in rank order: the
// holds an acquisition scores by itself are score 0 with a Hold, the rest
// carry the independent score.
func scoresOfSupply(p SupplyPlan) []supplyScore {
	var out []supplyScore
	for _, e := range append(append([]SupplyEntry(nil), p.Portfolio...), p.Unknown...) {
		s := supplyScore{ID: e.Candidate.ID, Kind: e.Candidate.Kind, Wanted: e.Wanted, Trips: e.Trips, Value: e.Value}
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

func rankBySupply(demand domain.Fact[[]ResourceDemand], cands []SupplyCandidate, c AcquisitionCompetition) ([]supplyScore, error) {
	p, err := planAcquisitions(demand, cands, c)
	if err != nil {
		return nil, err
	}
	var ranked []supplyScore
	for _, s := range scoresOfSupply(p) {
		if s.Score > 0 {
			ranked = append(ranked, s)
		}
	}
	return ranked, nil
}

func scoreBySupply(demand domain.Fact[[]ResourceDemand], cand SupplyCandidate, c AcquisitionCompetition) (supplyScore, error) {
	p, err := planAcquisitions(demand, []SupplyCandidate{cand}, c)
	if err != nil {
		return supplyScore{}, err
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
