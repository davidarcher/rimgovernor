namespace RimGovernor.Brain.Observation;

// The planning rows are views over the catalog's generated def rows and stat table: nothing here is read from a
// native planning writer. A row a view needs and the catalog lacks is an error, never a default.

public static partial class ObservationPackage
{
    /// <summary>
    /// Name's planning row, null when the catalog has no player-catalog def of that name: a buildable ThingDef,
    /// a sowable plant or a buildable TerrainDef (the game's BuildableByPlayer and PlantProperties.Sowable).
    /// </summary>
    internal static PlanningDefinition? PlanningView(DefinitionCatalog catalog, string name)
    {
        var thing = catalog.FindThing(name);
        if (thing is not null)
        {
            if (!DefinitionCatalog.Buildable(thing) && (thing.plant?.sowTags?.Count ?? 0) == 0)
            {
                return null;
            }
            return ThingView(catalog, name, thing);
        }
        var terrain = catalog.FindTerrain(name);
        if (terrain is not null)
        {
            if (!DefinitionCatalog.TerrainBuildable(terrain))
            {
                return null;
            }
            return TerrainView(catalog, name, terrain);
        }
        return null;
    }

    internal static Fact<double> FiniteFact(double v) => double.IsNaN(v) || double.IsInfinity(v) ? Unknown<double>() : Known(v);

    internal static Fact<double> NonnegativeFact(double v) => double.IsNaN(v) || double.IsInfinity(v) || v < 0 ? Unknown<double>() : Known(v);

    internal static List<Amount> Amounts(IEnumerable<StatCost> rows)
    {
        var output = new List<Amount>();
        foreach (var q in rows)
        {
            output.Add(new Amount(new Resource(q.Def), q.Units));
        }
        return output;
    }

    // The stats a stuff option carries for the stuff criteria and the work read.
    private static readonly string[] OptionStats =
    [
        BridgeConstants.StatMaxHitPoints, BridgeConstants.StatFlammability, BridgeConstants.StatBedRestEffectiveness,
        BridgeConstants.StatWorkToBuild, BridgeConstants.StatDoorOpenSpeed,
    ];

    private static PlanningDefinition ThingView(DefinitionCatalog catalog, string name, Verse.ThingDef row)
    {
        var v = new PlanningDefinition
        {
            Name = name,
            Research = [.. (row.researchPrerequisites ?? []).Select(project => project.defName)],
            ConstructionSkill = Known(row.constructionSkillPrerequisite),
        };
        v = v with { Size = Known(new Bounds(row.size.x, row.size.z)) };
        var draw = DefinitionCatalog.PowerDraw(row);
        v = v with { NeedsPower = Known(draw > 0) };
        if (draw is { } watts)
        {
            v = v with { PowerW = FiniteFact(watts) };
        }
        if (row.GetCompProperties<RimWorld.CompProperties_Glower>() is { } glow)
        {
            v = v with { GlowRadius = FiniteFact(glow.glowRadius) };
        }
        if (row.GetCompProperties<RimWorld.CompProperties_Explosive>() is { } explosive)
        {
            v = v with { ExplosiveRadius = NonnegativeFact(explosive.explosiveRadius) };
        }
        if (typeof(RimWorld.Building_MechCharger).IsAssignableFrom(row.thingClass))
        {
            v = v with { MechCharger = Known(true) };
        }
        if (row.building is not null)
        {
            var pollutes = DefinitionCatalog.HasComp<RimWorld.CompProperties_Toxifier>(row)
                || DefinitionCatalog.HasComp<RimWorld.CompProperties_PolluteOverTime>(row)
                || DefinitionCatalog.HasComp<RimWorld.CompProperties_WasteProducer>(row);
            v = v with { Pollutes = Known(pollutes) };
        }
        var sowTag = row.building?.sowTag ?? "";
        if (sowTag != "")
        {
            v = v with { SowTag = Known(sowTag) };
            if (row.fertility >= 0)
            {
                v = v with { GrowerFertility = FiniteFact(row.fertility) };
            }
        }
        v = v with { RoomRoles = RoomRoles(name, row) };
        if (row.plant is { } plant)
        {
            PlantView(catalog, ref v, plant);
        }
        StuffView(catalog, ref v, name, row);
        return v;
    }

    /// <summary>
    /// Sets the def's cost list and work to build, or its stuff options when it is made from stuff: the
    /// per-stuff values live on the options, and a stuffed def has no single cost list (StuffChoice prices it).
    /// </summary>
    private static void StuffView(DefinitionCatalog catalog, ref PlanningDefinition v, string name, Verse.ThingDef row)
    {
        if ((row.stuffCategories?.Count ?? 0) == 0)
        {
            var costs = catalog.AdjustedCosts(name, "");
            v = v with { Costs = Known(EquatableArray.From(Amounts(costs))) };
            var (work, shown) = catalog.ShownStatValue(name, "", BridgeConstants.StatWorkToBuild);
            if (shown)
            {
                v = v with { WorkToBuild = NonnegativeFact(work) };
            }
            return;
        }
        var options = new List<StuffOption>();
        foreach (var stuff in catalog.AllowedStuffs(name))
        {
            var costs = catalog.AdjustedCosts(name, stuff);
            var value = catalog.CostValue(name, stuff);
            var stats = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var stat in OptionStats)
            {
                var (shownValue, ok) = catalog.ShownStatValue(name, stuff, stat);
                if (ok)
                {
                    stats[stat] = shownValue;
                }
            }
            options.Add(new StuffOption
            {
                Stuff = stuff,
                Costs = Amounts(costs),
                Value = value,
                Stats = stats,
                Common = (catalog.FindThing(stuff)?.stuffProps?.commonality ?? 0) > 0,
            });
        }
        v = v with { Stuffed = true, StuffOptions = options };
    }

    /// <summary>
    /// The room-role furniture roles of a thing: the roles the game names for the def (carried per def by the
    /// catalog), a baby bed (a bed flagged bed_crib) and a deathrest-bindable building, a casket when it is a bed.
    /// </summary>
    private static List<string> RoomRoles(string name, Verse.ThingDef row)
    {
        var roles = new List<string>(DefinitionCatalog.GameRoomRoles(name));
        if (row.building?.bed_crib ?? false)
        {
            roles.Add(FurnitureRole.BabyBed.Value);
        }
        if (DefinitionCatalog.HasComp<RimWorld.CompProperties_DeathrestBindable>(row))
        {
            roles.Add(typeof(RimWorld.Building_Bed).IsAssignableFrom(row.thingClass) ? FurnitureRole.DeathrestCasket.Value : FurnitureRole.DeathrestAccelerator.Value);
        }
        roles.Sort(StringComparer.Ordinal);
        return roles;
    }

    private static void PlantView(DefinitionCatalog catalog, ref PlanningDefinition v, RimWorld.PlantProperties plant)
    {
        var biomes = new List<string>();
        foreach (var record in plant.wildBiomes ?? [])
        {
            biomes.Add(record.biome?.defName ?? "");
        }
        v = v with
        {
            HarvestWork = FiniteFact(plant.harvestWork),
            RequiresPollution = Known(plant.RequiresPollution),
            RequiresCleanSoil = Known(plant.RequiresNoPollution),
            GrowDays = FiniteFact(plant.growDays),
            FertilityMin = FiniteFact(plant.fertilityMin),
            FertilitySensitivity = FiniteFact(plant.fertilitySensitivity),
            GrowMinGlow = FiniteFact(plant.growMinGlow),
            MinGrowthTemperature = FiniteFact(plant.minGrowthTemperature),
            MinOptimalGrowthTemperature = FiniteFact(plant.minOptimalGrowthTemperature),
            MaxOptimalGrowthTemperature = FiniteFact(plant.maxOptimalGrowthTemperature),
            MaxGrowthTemperature = FiniteFact(plant.maxGrowthTemperature),
            SowTags = Known(EquatableArray.From(plant.sowTags ?? [])),
            SowMinSkill = Known(plant.sowMinSkill),
            BlockAdjacentSow = Known(plant.blockAdjacentSow),
            MustBeWildToSow = Known(plant.mustBeWildToSow),
            HarvestMinGrowth = FiniteFact(plant.harvestMinGrowth),
            SowWork = FiniteFact(plant.sowWork),
            WildBiomes = Known(EquatableArray.From(biomes)),
            // Sowing waits on the plant's own research (TreeSowing, Devilstrand), which the thing's research
            // prerequisites do not list.
            Research = [.. v.Research, .. (plant.sowResearchPrerequisites ?? []).Select(project => project.defName)],
        };
        var product = plant.harvestedThingDef?.defName ?? "";
        if (product == "")
        {
            return;
        }
        // The game's PlantProperties.HarvestDestroys is harvestAfterGrowth <= 0.
        v = v with { HarvestDestroysPlant = Known(plant.harvestAfterGrowth <= 0) };
        var productRow = catalog.FindThing(product)
            ?? throw new BridgeContractException($"plant harvests {product}, which the catalog has no def row for");
        v = v with
        {
            HarvestedThingDef = Known(product),
            HarvestYield = FiniteFact(plant.harvestYield),
            RawPreferred = Known(productRow.ingestible is not null && productRow.ingestible.preferability >= RimWorld.FoodPreferability.RawTasty),
            Edible = Known(catalog.IsFood(product)),
        };
        var rot = catalog.RotDays(product);
        v = v with { HarvestPerishable = Known(rot is not null) };
        if (rot is { } days)
        {
            v = v with { HarvestRotDays = FiniteFact(days) };
        }
        // A product the game shows no Nutrition stat for gives none.
        var (nutrition, _) = catalog.ShownStatValue(product, "", BridgeConstants.StatNutrition);
        v = v with { HarvestNutrition = FiniteFact(plant.harvestYield * nutrition) };
    }

    private static PlanningDefinition TerrainView(DefinitionCatalog catalog, string name, Verse.TerrainDef row)
    {
        var v = new PlanningDefinition
        {
            Name = name,
            Terrain = Known(true),
            Research = [.. (row.researchPrerequisites ?? []).Select(project => project.defName)],
            ConstructionSkill = Known(row.constructionSkillPrerequisite),
            Size = Known(new Bounds(1, 1)),
        };
        v = v with { Costs = Known(EquatableArray.From(Amounts(catalog.TerrainAdjustedCosts(name)))) };
        var floor = catalog.FloorTerrain(name);
        v = v with
        {
            Cleanliness = FiniteFact(floor.Cleanliness),
            Beauty = FiniteFact(floor.Beauty),
            Flammability = FiniteFact(floor.Flammability),
            PathCost = Known(floor.PathCost),
            WorkToBuild = NonnegativeFact(catalog.TerrainWorkToBuild(name)),
        };
        if (row.tags is { Count: > 0 } floorTags)
        {
            v = v with { FloorTags = [.. floorTags] };
        }
        return v;
    }
}
