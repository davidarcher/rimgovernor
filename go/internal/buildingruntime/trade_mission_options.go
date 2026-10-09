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
	"slices"
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
	silver, sk := p.Facts.Silver().Value()
	reserve, rk := policy.TradeSilverReserve(p.Facts.Colonists)
	if !sk || !rk || silver <= reserve {
		return nil
	}
	budget := min(silver-reserve, int64(math.MaxInt32))
	var out []policy.TradeAcquisitionOption
	for _, settlement := range destinations.Settlements {
		var cargo []domain.CargoItem
		var compatible []policy.ResourceKey
		for _, d := range demands {
			if d.Gap <= 0 {
				continue
			}
			can, known := catalog.TraderKindCanSupply(settlement.TraderKind, d.Demand.Good).Value()
			if !known || !can {
				continue
			}
			def := string(d.Demand.Good.Def)
			count := int64(math.Ceil(d.Gap))
			if d.Demand.Good == policy.NutritionKey {
				defs := make([]string, 0, len(catalog.ThingDefs))
				for name := range catalog.ThingDefs {
					defs = append(defs, name)
				}
				slices.Sort(defs)
				def = ""
				for _, name := range defs {
					food, known, e := catalog.TradeFood(name)
					compatible, knownSupply := catalog.TraderKindCanSupply(settlement.TraderKind, policy.ResourceKey{Def: policy.Resource(name)}).Value()
					if e == nil && known && food.Prepared && food.Nutrition > 0 && knownSupply && compatible {
						def = name
						count = int64(math.Ceil(d.Gap * math.Max(1, r.policy.FoodTargetDays) / food.Nutrition))
						break
					}
				}
			}
			if def == "" || count <= 0 || count > math.MaxInt32 {
				continue
			}
			if slices.ContainsFunc(cargo, func(item domain.CargoItem) bool { return item.Definition == def }) {
				continue
			}
			cargo = append(cargo, domain.CargoItem{Definition: def, Count: uint64(count)})
			compatible = append(compatible, d.Demand.Good)
		}
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
