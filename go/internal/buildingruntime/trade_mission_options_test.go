package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type nutritionMissionSource struct {
	*roundsNative
	missionPackFake
	world domain.GenerationSnapshot
	kind  string
}

func (n *nutritionMissionSource) ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error) {
	return bridge.WorldProgressionRead{Maps: []bridge.WorldMap{{ID: int32(n.world.Map), Home: true, Tile: 2}}}, bridge.Result{}, nil
}
func (n *nutritionMissionSource) ReadWorld(context.Context, *c.Identity, int32, float64) (bridge.WorldRead, bridge.Result, error) {
	return bridge.WorldRead{Settlements: []bridge.SettlementFact{{ID: "seller", Tile: 3, TraderKind: n.kind, CanTrade: domain.Known(true)}}}, bridge.Result{}, nil
}

func TestSettlementNutritionMissionReadsCatalogStat(t *testing.T) {
	catalog := recordedcatalog.Catalog(t)
	food, known, err := catalog.TradeFood("MealSimple")
	if err != nil || !known || !food.Prepared || food.Nutrition != 0 {
		t.Fatal("classification contract changed", food, known, err)
	}
	kind := "Base_Outlander_Standard"
	can, k := catalog.TraderKindCanSupply(kind, policy.NutritionKey).Value()
	if !k || !can {
		t.Fatal("recorded seller must supply nutrition", kind, k, can)
	}
	r, _, _, _, native := roundsFixture(t)
	world := r.player.session.State().Snapshot
	source := &nutritionMissionSource{roundsNative: native, world: world, kind: kind}
	r.native = source
	r.tradeAcquisition.missions = map[string]domain.TradeMission{}
	options := r.settlementAcquisitionOptions(context.Background(), world, "food", observation.ColonyProjection{Facts: missionPreparationFacts()}, []policy.SupplyDemandResult{{Demand: policy.SupplyDemand{Good: policy.NutritionKey}, Gap: 1}}, catalog)
	if len(options) != 1 || len(source.requests) != 2 {
		t.Fatalf("prepared-food mission omitted: options=%#v previews=%d", options, len(source.requests))
	}
	mission := r.tradeAcquisition.missions[options[0].ID]
	if len(mission.Demand) != 1 || mission.Demand[0].Count == 0 {
		t.Fatal("no actual food demand", mission)
	}
	nutrition, err := catalog.StatValue(mission.Demand[0].Definition, "", bridge.StatNutrition)
	if err != nil || nutrition <= 0 || float64(mission.Demand[0].Count)*float64(nutrition) < r.policy.FoodTargetDays {
		t.Fatal("nutrition target underfilled", mission.Demand, nutrition, err)
	}
}
