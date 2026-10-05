package observation

import (
	"fmt"
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// The planning rows are views over the catalog's generated def rows and stat
// table (#1731): nothing here is read from a native planning writer. A row a
// view needs and the catalog lacks is an error, never a default.

// planningView is name's planning row, ok false when the catalog has no
// player-catalog def of that name: a buildable ThingDef, a sowable plant or a
// buildable TerrainDef (the game's BuildableByPlayer and PlantProperties.Sowable).
func planningView(catalog *bridge.DefinitionCatalog, name string) (view PlanningDefinition, ok bool, err error) {
	if row := catalog.ThingDef(name); row != nil {
		if !bridge.Buildable(row) && len(row.GetPlant().GetSowTags()) == 0 {
			return PlanningDefinition{}, false, nil
		}
		view, err = thingView(catalog, name, row)
		return view, err == nil, err
	}
	if row := catalog.TerrainDef(name); row != nil {
		if !bridge.TerrainBuildable(row) {
			return PlanningDefinition{}, false, nil
		}
		view, err = terrainView(catalog, name, row)
		return view, err == nil, err
	}
	return PlanningDefinition{}, false, nil
}

func finiteFact(v float64) domain.Fact[float64] {
	if math.IsNaN(v) || math.IsInf(v, 0) {
		return domain.Unknown[float64]()
	}
	return domain.Known(v)
}

func nonnegativeFact(v float64) domain.Fact[float64] {
	if math.IsNaN(v) || math.IsInf(v, 0) || v < 0 {
		return domain.Unknown[float64]()
	}
	return domain.Known(v)
}

func amounts(rows []*o.Quantity) []policy.Amount {
	out := make([]policy.Amount, 0, len(rows))
	for _, q := range rows {
		out = append(out, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
	}
	return out
}

// The stats a stuff option carries for the stuff criteria and the work read.
var optionStats = []string{bridge.StatMaxHitPoints, bridge.StatFlammability, bridge.StatBedRestEffectiveness, bridge.StatWorkToBuild, bridge.StatDoorOpenSpeed}

func thingView(catalog *bridge.DefinitionCatalog, name string, row *d.ThingDef) (PlanningDefinition, error) {
	v := PlanningDefinition{Name: name, Research: append([]string{}, row.GetResearchPrerequisites()...), ConstructionSkill: domain.Known(row.GetConstructionSkillPrerequisite())}
	v.Size = domain.Known(policy.Bounds{Width: row.GetSize().GetX(), Height: row.GetSize().GetZ()})
	watts, powered, err := catalog.PowerDraw(row)
	if err != nil {
		return v, err
	}
	v.NeedsPower = domain.Known(powered && watts > 0)
	if powered {
		v.PowerW = finiteFact(watts)
	}
	if glow, err := catalog.CompOf(row, bridge.ClassGlowerComp); err != nil {
		return v, err
	} else if glow != nil {
		radius, err := bridge.CompFloat(glow, "glowRadius")
		if err != nil {
			return v, err
		}
		v.GlowRadius = finiteFact(radius)
	}
	if explosive, err := catalog.CompOf(row, bridge.ClassExplosiveComp); err != nil {
		return v, err
	} else if explosive != nil {
		radius, err := bridge.CompFloat(explosive, "explosiveRadius")
		if err != nil {
			return v, err
		}
		v.ExplosiveRadius = nonnegativeFact(radius)
	}
	charger, err := catalog.ClassIsA(row.GetThingClass(), bridge.ClassMechChargerThng)
	if err != nil {
		return v, err
	}
	if charger {
		v.MechCharger = domain.Known(true)
	}
	if row.GetBuilding() != nil {
		pollutes := false
		for _, class := range []string{bridge.ClassToxifierComp, bridge.ClassPolluteComp, bridge.ClassWasteComp} {
			has, err := catalog.HasComp(row, class)
			if err != nil {
				return v, err
			}
			pollutes = pollutes || has
		}
		v.Pollutes = domain.Known(pollutes)
	}
	if tag := row.GetBuilding().GetSowTag(); tag != "" {
		v.SowTag = domain.Known(tag)
		if row.GetFertility() >= 0 {
			v.GrowerFertility = finiteFact(float64(row.GetFertility()))
		}
	}
	if v.RoomRoles, err = roomRoles(catalog, name, row); err != nil {
		return v, err
	}
	if plant := row.GetPlant(); plant != nil {
		if err := plantView(catalog, &v, plant); err != nil {
			return v, err
		}
	}
	return v, stuffView(catalog, &v, name, row)
}

// stuffView sets the def's cost list and work to build, or its stuff options
// when it is made from stuff: the per-stuff values live on the options, and a
// stuffed def has no single cost list (StuffChoice prices it).
func stuffView(catalog *bridge.DefinitionCatalog, v *PlanningDefinition, name string, row *d.ThingDef) error {
	if len(row.GetStuffCategories()) == 0 {
		costs, err := catalog.AdjustedCosts(name, "")
		if err != nil {
			return err
		}
		v.Costs = domain.Known(amounts(costs))
		if work, shown, err := catalog.ShownStatValue(name, "", bridge.StatWorkToBuild); err != nil {
			return err
		} else if shown {
			v.WorkToBuild = nonnegativeFact(float64(work))
		}
		return nil
	}
	v.Stuffed = true
	stuffs, err := catalog.AllowedStuffs(name)
	if err != nil {
		return err
	}
	for _, stuff := range stuffs {
		costs, err := catalog.AdjustedCosts(name, stuff)
		if err != nil {
			return err
		}
		value, err := catalog.CostValue(name, stuff)
		if err != nil {
			return err
		}
		option := StuffOption{Stuff: stuff, Costs: amounts(costs), Value: value, Stats: map[string]float64{},
			Common: catalog.ThingDef(stuff).GetStuffProps().GetCommonality() > 0}
		for _, stat := range optionStats {
			shown, ok, err := catalog.ShownStatValue(name, stuff, stat)
			if err != nil {
				return err
			}
			if ok {
				option.Stats[stat] = float64(shown)
			}
		}
		v.StuffOptions = append(v.StuffOptions, option)
	}
	return nil
}

// roomRoles are the room-role furniture roles of a thing: the roles the game
// names for the def (carried per def by the catalog), a baby bed (a bed flagged
// bed_crib) and a deathrest-bindable building, a casket when it is a bed.
func roomRoles(catalog *bridge.DefinitionCatalog, name string, row *d.ThingDef) ([]string, error) {
	game, err := catalog.GameRoomRoles(name)
	if err != nil {
		return nil, err
	}
	roles := append([]string{}, game...)
	if row.GetBuilding().GetBedCrib() {
		roles = append(roles, string(policy.RoleBabyBed))
	}
	rest, err := catalog.HasComp(row, bridge.ClassDeathrestComp)
	if err != nil {
		return nil, err
	}
	if rest {
		bed, err := catalog.ClassIsA(row.GetThingClass(), bridge.ClassBedThing)
		if err != nil {
			return nil, err
		}
		if bed {
			roles = append(roles, string(policy.RoleDeathrestCasket))
		} else {
			roles = append(roles, string(policy.RoleDeathrestAccelerator))
		}
	}
	slices.Sort(roles)
	return roles, nil
}

func plantView(catalog *bridge.DefinitionCatalog, v *PlanningDefinition, plant *d.PlantProperties) error {
	v.HarvestWork = finiteFact(float64(plant.GetHarvestWork()))
	v.RequiresPollution = domain.Known(plant.GetPollution() == d.Pollution_POLLUTION_POLLUTED_ONLY)
	v.RequiresCleanSoil = domain.Known(plant.GetPollution() == d.Pollution_POLLUTION_CLEAN_ONLY)
	v.GrowDays = finiteFact(float64(plant.GetGrowDays()))
	v.FertilityMin = finiteFact(float64(plant.GetFertilityMin()))
	v.FertilitySensitivity = finiteFact(float64(plant.GetFertilitySensitivity()))
	v.GrowMinGlow = finiteFact(float64(plant.GetGrowMinGlow()))
	v.SowTags = domain.Known(append([]string{}, plant.GetSowTags()...))
	product := plant.GetHarvestedThingDef()
	if product == "" {
		return nil
	}
	productRow := catalog.ThingDef(product)
	if productRow == nil {
		return fmt.Errorf("%w: plant harvests %s, which the catalog has no def row for", bridge.ErrContract, product)
	}
	v.RawPreferred = domain.Known(productRow.GetIngestible() != nil && productRow.GetIngestible().GetPreferability() >= d.FoodPreferability_FOOD_PREFERABILITY_RAW_TASTY)
	edible, err := catalog.IsFood(product)
	if err != nil {
		return err
	}
	v.Edible = domain.Known(edible)
	// A product the game shows no Nutrition stat for gives none.
	nutrition, _, err := catalog.ShownStatValue(product, "", bridge.StatNutrition)
	if err != nil {
		return err
	}
	v.HarvestNutrition = finiteFact(float64(plant.GetHarvestYield()) * float64(nutrition))
	return nil
}

func terrainView(catalog *bridge.DefinitionCatalog, name string, row *d.TerrainDef) (PlanningDefinition, error) {
	v := PlanningDefinition{Name: name, Terrain: domain.Known(true), Research: append([]string{}, row.GetResearchPrerequisites()...), ConstructionSkill: domain.Known(row.GetConstructionSkillPrerequisite()), Size: domain.Known(policy.Bounds{Width: 1, Height: 1})}
	costs, err := catalog.TerrainAdjustedCosts(name)
	if err != nil {
		return v, err
	}
	v.Costs = domain.Known(amounts(costs))
	floor, err := catalog.FloorTerrain(name)
	if err != nil {
		return v, err
	}
	v.Cleanliness, v.Beauty, v.Flammability = finiteFact(floor.Cleanliness), finiteFact(floor.Beauty), finiteFact(floor.Flammability)
	v.PathCost = domain.Known(floor.PathCost)
	work, err := catalog.TerrainWorkToBuild(name)
	if err != nil {
		return v, err
	}
	v.WorkToBuild = nonnegativeFact(float64(work))
	if tags := row.GetTags(); len(tags) > 0 {
		v.FloorTags = slices.Clone(tags)
	}
	return v, nil
}
