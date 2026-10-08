package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

// This fast snapshot matrix exercises the supply owner's retained proposals,
// independent of native mission dispatch. Away inventory is never a candidate.
func TestSupplyTradeAcquisitionOwnershipMatrix(t *testing.T) {
	for _, good := range []policy.ResourceKey{policy.NutritionKey, {Def: "Steel"}} {
		t.Run(string(good.Def), func(t *testing.T) {
			r := &Rounder{}
			p := observation.ColonyProjection{}
			p.Facts.Items.Currency = "Silver"
			p.Facts.Colonists = domain.Known(int64(2))
			p.Facts.Resources = domain.Known([]policy.Amount{{Resource: "Silver", Count: 500}})
			world := domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load"}
			o := policy.TradeAcquisitionOption{ID: "settlement/target/crew", Kind: policy.TradeAcquireSettlement, Participant: domain.TradeParticipant{Kind: domain.TradeParticipantSettlement, ID: "target", Caravan: "crew"}, Compatible: []policy.ResourceKey{good}, Eligible: domain.Known(true), LeadDays: domain.Known(1.0), LaborTicks: domain.Known(20000.0), SafeCrew: domain.Known(true), RoutesReachable: domain.Known(true), Packable: domain.Known(true), OutboundDays: domain.Known(.5), ReturnDays: domain.Known(.5), FoodDays: domain.Known(2.0), RotDays: domain.Known(2.0)}
			demands := []policy.SupplyDemandResult{{Demand: policy.SupplyDemand{Good: good, Units: 10, Priority: 100, HorizonDays: 2}, Gap: 10}}
			review := func(owner string) {
				r.planTradeAcquisition(context.Background(), world, owner, p, demands, []policy.TradeAcquisitionOption{o})
			}
			review("food")
			if p := r.TradeAcquisitionPlan("food").Proposal; p == nil || p.Option.Participant.ID != "target" {
				t.Fatal("supply review did not retain target")
			}
			if !r.ClaimTradeAcquisition("food", o.ID) || r.ClaimTradeAcquisition("food", o.ID) {
				t.Fatal("claim repeated active work")
			}
			review("food")
			review("resources")
			if r.TradeAcquisitionPlan("food").Proposal != nil || r.TradeAcquisitionPlan("resources").Proposal != nil {
				t.Fatal("active attempt duplicated across owners")
			}
			if demands[0].Delivered != 0 || demands[0].Gap != 10 {
				t.Fatal("away goods credited at home")
			}
			r.ReleaseTradeAcquisition("food", "wrong")
			review("food")
			if r.TradeAcquisitionPlan("food").Proposal != nil {
				t.Fatal("wrong attempt released owner")
			}
			r.ReleaseTradeAcquisition("food", o.ID)
			review("food")
			if r.TradeAcquisitionPlan("food").Proposal == nil {
				t.Fatal("completed attempt retained forever")
			}
			// A stockout live sheet supplies no purchase candidate: demand remains.
			stockout, err := policy.PlanSupply(policy.SupplyPlanRequest{Demands: domain.Known([]policy.SupplyDemand{demands[0].Demand}), Candidates: domain.Known([]policy.SupplyCandidate{}), Labor: domain.Known(20000.0)})
			if err != nil || stockout.Gap(good) != 10 {
				t.Fatalf("stockout credited goods %+v %v", stockout, err)
			}
			demands[0].Gap = 0
			review("food")
			if r.TradeAcquisitionPlan("food").Proposal != nil {
				t.Fatal("covered demand requested goods")
			}
		})
	}
}
