package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func acquisitionRequestFixture(good ResourceKey) TradeAcquisitionRequest {
	return TradeAcquisitionRequest{Demands: []SupplyDemandResult{{Demand: SupplyDemand{Good: good, Units: 10, Priority: 100, HorizonDays: 3}, Gap: 10}}, Silver: domain.Known(int64(500)), Reserve: domain.Known(int64(100)), Options: []TradeAcquisitionOption{{ID: "request", Kind: TradeAcquireOrbital, Faction: "faction", TraderKind: "bulk", Console: "console", Negotiator: "pawn", Eligible: domain.Known(true), LeadDays: domain.Known(1.0), LaborTicks: domain.Known(0.0), Compatible: []ResourceKey{good}, GoodwillCost: domain.Known(int32(30)), RelationAfterPayment: domain.Known("Ally"), CooldownTicks: domain.Known(int64(0)), OrbitalAvailable: domain.Known(true)}}}
}

func TestTradeAcquisitionFoodResourceMatrix(t *testing.T) {
	for _, good := range []ResourceKey{NutritionKey, {Def: Resource("Steel")}} {
		t.Run(string(good.Def), func(t *testing.T) {
			rows := []struct {
				name, reason string
				edit         func(*TradeAcquisitionRequest)
			}{
				{"eligible", "selected", func(*TradeAcquisitionRequest) {}},
				{"late arrival", "late_arrival", func(r *TradeAcquisitionRequest) { r.Options[0].LeadDays = domain.Known(4.0) }},
				{"no selling kind", "no_compatible_demand", func(r *TradeAcquisitionRequest) { r.Options[0].Compatible = nil }},
				{"cooldown", "cooldown", func(r *TradeAcquisitionRequest) { r.Options[0].CooldownTicks = domain.Known(int64(1)) }},
				{"alliance loss", "alliance_loss", func(r *TradeAcquisitionRequest) { r.Options[0].RelationAfterPayment = domain.Known("Neutral") }},
				{"no comms", "unknown_request_facts", func(r *TradeAcquisitionRequest) { r.Options[0].Console = "" }},
				{"no DLC", "orbital_unavailable", func(r *TradeAcquisitionRequest) { r.Options[0].OrbitalAvailable = domain.Known(false) }},
				{"insufficient silver", "insufficient_silver", func(r *TradeAcquisitionRequest) { r.Silver = r.Reserve }},
				{"unknown eligibility", "unknown_eligibility_or_cost", func(r *TradeAcquisitionRequest) { r.Options[0].Eligible = domain.Unknown[bool]() }},
				{"passing nontrader ship", "passing_ships", func(r *TradeAcquisitionRequest) { r.Options[0].PassingShips = 1 }},
				{"pending attempt", "active_attempt", func(r *TradeAcquisitionRequest) { r.ActiveID = "other" }},
				{"covered locally", "no_compatible_demand", func(r *TradeAcquisitionRequest) { r.Demands[0].Gap = 0 }},
				{"no safe crew", "unsafe_crew", func(r *TradeAcquisitionRequest) { tripFixture(r); r.Options[0].SafeCrew = domain.Known(false) }},
				{"round trip too late", "late_arrival", func(r *TradeAcquisitionRequest) { tripFixture(r); r.Options[0].LeadDays = domain.Known(4.0) }},
				{"spoiling cargo", "spoiling_cargo", func(r *TradeAcquisitionRequest) { tripFixture(r); r.Options[0].RotDays = domain.Known(.5) }},
				{"interrupted trip", "active_attempt", func(r *TradeAcquisitionRequest) { tripFixture(r); r.ActiveID = "mission" }},
				{"unknown return", "unknown_trip_facts", func(r *TradeAcquisitionRequest) { tripFixture(r); r.Options[0].ReturnDays = domain.Unknown[float64]() }},
			}
			for _, row := range rows {
				t.Run(row.name, func(t *testing.T) {
					r := acquisitionRequestFixture(good)
					row.edit(&r)
					plan := PlanTradeAcquisition(r)
					if len(plan.Decisions) != 1 || plan.Decisions[0].Reason != row.reason {
						t.Fatalf("got %+v want %s", plan, row.reason)
					}
					if (plan.Proposal != nil) != (row.reason == "selected") {
						t.Fatal("proposal admission mismatch")
					}
					if r.Demands[0].Delivered != 0 {
						t.Fatal("attempt credited supply")
					}
				})
			}
		})
	}
}
func tripFixture(r *TradeAcquisitionRequest) {
	o := &r.Options[0]
	o.Kind = TradeAcquireSettlement
	o.Participant = domain.TradeParticipant{Kind: domain.TradeParticipantSettlement, ID: "settlement", Caravan: "crew"}
	o.SafeCrew = domain.Known(true)
	o.RoutesReachable = domain.Known(true)
	o.Packable = domain.Known(true)
	o.OutboundDays = domain.Known(.5)
	o.ReturnDays = domain.Known(.5)
	o.FoodDays = domain.Known(5.0)
	o.RotDays = domain.Known(5.0)
}
func TestTradeAcquisitionStableOrder(t *testing.T) {
	r := acquisitionRequestFixture(NutritionKey)
	base := r.Options[0]
	base.ID = "b"
	a := base
	a.ID = "a"
	slow := a
	slow.ID = "slow"
	slow.LeadDays = domain.Known(2.0)
	costly := a
	costly.ID = "costly"
	costly.LaborTicks = domain.Known(1.0)
	for _, opts := range [][]TradeAcquisitionOption{{slow, costly, base, a}, {a, base, costly, slow}} {
		r.Options = opts
		if p := PlanTradeAcquisition(r).Proposal; p == nil || p.Option.ID != "a" {
			t.Fatalf("unstable result %+v", p)
		}
	}
}
func TestUnpricedAttemptLeavesLocalSupplyActive(t *testing.T) {
	r := acquisitionRequestFixture(NutritionKey)
	r.Demands[0].Demand.Units = 0
	r.Demands[0].Demand.PerDay = 10
	local := FoodCandidate(CandidateForage, "local", domain.Known(3.0))
	local.LeadDays = domain.Known(0.0)
	local.LaborPerDay = domain.Known(1.0)
	local.State = domain.Known(CandidateClosed)
	for _, active := range []string{"", "pending"} {
		plan, err := PlanSupply(SupplyPlanRequest{Demands: domain.Known([]SupplyDemand{r.Demands[0].Demand}), Candidates: domain.Known([]SupplyCandidate{local}), Labor: domain.Known(20000.0)})
		if err != nil {
			t.Fatal(err)
		}
		r.Demands = plan.Demands
		r.ActiveID = active
		p := PlanTradeAcquisition(r)
		if len(plan.Portfolio) != 1 || plan.Portfolio[0].Decision != SupplyOpen || plan.Gap(NutritionKey) <= 0 {
			t.Fatalf("local supply suppressed %+v", plan)
		}
		if active == "" && p.Proposal == nil {
			t.Fatal("remaining gap never offered attempt")
		}
	}
}
