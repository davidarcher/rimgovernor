package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"math"
)

func (r *Rounder) settlementAcquisitionOptions(ctx context.Context, world domain.GenerationSnapshot, owner string, p observation.ColonyProjection, demands []policy.SupplyDemandResult, catalog *bridge.DefinitionCatalog) []policy.TradeAcquisitionOption {
	r.census.mu.Lock()
	if latest := r.census.latest; latest != nil && latest.reading.Projection.Identity.SameContext(p.Identity) && latest.reading.Projection.Identity.Tick == p.Identity.Tick {
		f := latest.reading.Projection.Facts
		p.Facts.QuestColonyCalm = f.QuestColonyCalm
		p.Facts.QuestSparePawns = f.QuestSparePawns
		p.Facts.QuestDepartureWork = f.QuestDepartureWork
		p.Facts.WorkRoster = f.WorkRoster
	}
	r.census.mu.Unlock()
	source, ok := r.native.(interface {
		TradeMissionPackSource
		ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error)
		ReadWorld(context.Context, *c.Identity, int32, float64) (bridge.WorldRead, bridge.Result, error)
	})
	if !ok {
		return nil
	}
	projects, err := r.player.journal.OpenProjects(ctx, domain.TradeMissionConcern)
	if err != nil || len(projects) != 0 {
		return nil
	}
	id := boundary.Identity(world)
	progress, _, err := source.ReadWorldProgression(ctx, id, false)
	if err != nil {
		return nil
	}
	home := int32(-1)
	for _, row := range progress.Maps {
		if row.Home && domain.MapID(row.ID) == world.Map {
			home = row.Tile
		}
	}
	if home < 0 {
		return nil
	}
	// Read the complete native settlement census; routes, not radius, determine
	// which exact pack can safely make the round trip.
	destinations, _, err := source.ReadWorld(ctx, id, home, math.MaxFloat64)
	if err != nil {
		return nil
	}
	budget, known := policy.TradeMissionBudget(p.Facts.Silver(), p.Facts.Colonists).Value()
	if !known || budget <= 0 {
		return nil
	}
	var out []policy.TradeAcquisitionOption
	for _, settlement := range destinations.Settlements {
		compatibleFacts := map[policy.ResourceKey]domain.Fact[bool]{}
		for _, d := range demands {
			compatibleFacts[d.Demand.Good] = catalog.TraderKindCanSupply(settlement.TraderKind, d.Demand.Good)
		}
		var definitions []policy.MissionSupplyDefinition
		if _, requested := compatibleFacts[policy.NutritionKey]; requested {
			for name := range catalog.ThingDefs {
				food, known, err := catalog.TradeFood(name)
				definitions = append(definitions, policy.MissionSupplyDefinition{Definition: name, Prepared: missionOptional(food.Prepared, known && err == nil), Nutrition: missionCatalogStat(catalog, name, bridge.StatNutrition), CanSupply: catalog.TraderKindCanSupply(settlement.TraderKind, policy.ResourceKey{Def: policy.Resource(name)})})
			}
		}
		cargo, compatible := policy.TradeMissionCargo(demands, compatibleFacts, definitions, r.policy.FoodTargetDays)
		if len(cargo) == 0 {
			continue
		}
		mission, option, err := PrepareTradeMission(ctx, source, world, home, settlement, p.Facts, r.seasonal(p.Facts), budget, cargo, nil)
		if err != nil {
			continue
		}
		option.Compatible = compatible
		option.ID = owner + ":" + option.ID
		r.tradeAcquisition.mu.Lock()
		r.tradeAcquisition.missions[option.ID] = mission
		r.tradeAcquisition.mu.Unlock()
		out = append(out, option)
	}
	return out
}
