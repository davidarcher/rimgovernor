package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func missionFacts() (RoundsFacts, FoodSupply) {
	_, _, f, food := expeditionFixture()
	rows, _ := f.QuestDeparturePawns.Value()
	for i := range rows {
		rows[i].NegotiationAbility = domain.Known(float64(3 - i))
	}
	f.QuestDeparturePawns = domain.Known(rows)
	f.AnimalUpkeep.Food = domain.Known(food)
	return f, food
}

func TestTradeMissionCrewRetainsHomeOwnersAndUrgentClaims(t *testing.T) {
	f, _ := missionFacts()
	crew, reason := TradeMissionCrew(f, []domain.PawnID{"a"})
	if reason != "" || len(crew) != 1 || crew[0] != "b" {
		t.Fatal(crew, reason)
	}
	f.WorkRoster = domain.Known([]WorkCoverage{{Work: WorkType("Research"), Owners: 1}})
	if crew, reason = TradeMissionCrew(f, nil); len(crew) != 0 || reason == "" {
		t.Fatal("last owner departed", crew, reason)
	}
	f, _ = missionFacts()
	f.QuestColonyCalm = domain.Known(false)
	if crew, _ = TradeMissionCrew(f, nil); len(crew) != 0 {
		t.Fatal("busy colony departed")
	}
	f, _ = missionFacts()
	f.QuestColonistsAtHome = domain.Known(2)
	f.QuestHomeFloor = domain.Known(3)
	if crew, _ = TradeMissionCrew(f, nil); len(crew) != 0 {
		t.Fatal("home staffing depleted")
	}
}

func missionPack() *o.TradePackEstimate {
	return &o.TradePackEstimate{CanPack: proto.Bool(true), MassUsage: proto.Float64(10), MassCapacity: proto.Float64(30), FoodDays: proto.Float64(3), FoodRotDays: proto.Float64(3), Outbound: &o.WorldRoute{Destination: proto.Int32(3), Reachable: proto.Bool(true), EstimatedTicks: proto.Int64(int64(domain.TicksPerDay))}, Home: &o.WorldRoute{Destination: proto.Int32(2), Reachable: proto.Bool(true), EstimatedTicks: proto.Int64(2 * int64(domain.TicksPerDay))}}
}

func TestTradeMissionNativePackRequiresRoundTripFoodRotMassAndRoutes(t *testing.T) {
	if !SafeTradeMissionPack(missionPack(), 2, 3) {
		t.Fatal("safe pack refused")
	}
	for _, edit := range []func(*o.TradePackEstimate){func(p *o.TradePackEstimate) { p.Home.EstimatedTicks = nil }, func(p *o.TradePackEstimate) { p.Home.Reachable = proto.Bool(false) }, func(p *o.TradePackEstimate) { p.Home.Destination = proto.Int32(4) }, func(p *o.TradePackEstimate) { p.FoodDays = proto.Float64(2.9) }, func(p *o.TradePackEstimate) { p.FoodRotDays = proto.Float64(2.9) }, func(p *o.TradePackEstimate) { p.FoodDays = proto.Float64(0) }, func(p *o.TradePackEstimate) { p.MassUsage = proto.Float64(31) }, func(p *o.TradePackEstimate) { p.CanPack = nil }} {
		p := missionPack()
		edit(p)
		if SafeTradeMissionPack(p, 2, 3) {
			t.Fatal("unsafe pack admitted", p)
		}
	}
	f, food := missionFacts()
	if cargo, reason := TradeMissionFood(f, []domain.PawnID{"a"}, 3, 30, DefaultRoundsPolicy()); reason != "" || len(cargo) != 1 || cargo[0].Count != 3 {
		t.Fatal(cargo, reason)
	}
	food.Stocks[0].Perishable = domain.Known(true)
	food.Stocks[0].RotTicks = domain.Known(int64(domain.TicksPerDay))
	f.AnimalUpkeep.Food = domain.Known(food)
	if _, reason := TradeMissionFood(f, []domain.PawnID{"a"}, 3, 30, DefaultRoundsPolicy()); reason == "" {
		t.Fatal("spoiling food packed")
	}
}

func TestScoutingOptionDoesNotInventCaravanIdentity(t *testing.T) {
	r := acquisitionRequestFixture(NutritionKey)
	tripFixture(&r)
	r.Options[0].Participant.Caravan = ""
	if plan := PlanTradeAcquisition(r); plan.Proposal == nil {
		t.Fatal(plan)
	}
	if r.Options[0].Participant.Validate() == nil {
		t.Fatal("predeparture intent executable as trade")
	}
}
