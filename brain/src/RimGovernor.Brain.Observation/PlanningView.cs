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
        var thing = catalog.ThingDef(name);
        if (thing is not null)
        {
            if (!Buildable(thing) && (thing.Plant?.SowTags.Count ?? 0) == 0)
            {
                return null;
            }
            return ThingView(catalog, name, thing);
        }
        var terrain = catalog.TerrainDef(name);
        if (terrain is not null)
        {
            if (!TerrainBuildable(terrain))
            {
                return null;
            }
            return TerrainView(catalog, name, terrain);
        }
        return null;
    }

    internal static Fact<double> FiniteFact(double v) => double.IsNaN(v) || double.IsInfinity(v) ? Unknown<double>() : Known(v);

    internal static Fact<double> NonnegativeFact(double v) => double.IsNaN(v) || double.IsInfinity(v) || v < 0 ? Unknown<double>() : Known(v);

    internal static List<Amount> Amounts(IEnumerable<O.Quantity> rows)
    {
        var output = new List<Amount>();
        foreach (var q in rows)
        {
            output.Add(new Amount(new Resource(q.DefName), q.Units));
        }
        return output;
    }

    // The stats a stuff option carries for the stuff criteria and the work read.
    private static readonly string[] OptionStats =
    [
        BridgeConstants.StatMaxHitPoints, BridgeConstants.StatFlammability, BridgeConstants.StatBedRestEffectiveness,
        BridgeConstants.StatWorkToBuild, BridgeConstants.StatDoorOpenSpeed,
    ];

    private static PlanningDefinition ThingView(DefinitionCatalog catalog, string name, D.ThingDef row)
    {
        var v = new PlanningDefinition
        {
            Name = name,
            Research = [.. row.ResearchPrerequisites],
            ConstructionSkill = Known(row.ConstructionSkillPrerequisite),
        };
        v = v with { Size = Known(new Bounds(row.Size?.X ?? 0, row.Size?.Z ?? 0)) };
        var (watts, powered) = catalog.PowerDraw(row);
        v = v with { NeedsPower = Known(powered && watts > 0) };
        if (powered)
        {
            v = v with { PowerW = FiniteFact(watts) };
        }
        var glow = catalog.CompOf(row, BridgeConstants.ClassGlowerComp);
        if (glow is not null)
        {
            v = v with { GlowRadius = FiniteFact(CompFloat(glow, "glowRadius")) };
        }
        var explosive = catalog.CompOf(row, BridgeConstants.ClassExplosiveComp);
        if (explosive is not null)
        {
            v = v with { ExplosiveRadius = NonnegativeFact(CompFloat(explosive, "explosiveRadius")) };
        }
        if (catalog.ClassIsA(row.ThingClass, BridgeConstants.ClassMechChargerThng))
        {
            v = v with { MechCharger = Known(true) };
        }
        if (row.Building is not null)
        {
            var pollutes = false;
            foreach (var comp in new[] { BridgeConstants.ClassToxifierComp, BridgeConstants.ClassPolluteComp, BridgeConstants.ClassWasteComp })
            {
                pollutes = pollutes || catalog.HasComp(row, comp);
            }
            v = v with { Pollutes = Known(pollutes) };
        }
        var sowTag = row.Building?.SowTag ?? "";
        if (sowTag != "")
        {
            v = v with { SowTag = Known(sowTag) };
            if (row.Fertility >= 0)
            {
                v = v with { GrowerFertility = FiniteFact(row.Fertility) };
            }
        }
        v = v with { RoomRoles = RoomRoles(catalog, name, row) };
        if (row.Plant is { } plant)
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
    private static void StuffView(DefinitionCatalog catalog, ref PlanningDefinition v, string name, D.ThingDef row)
    {
        if (row.StuffCategories.Count == 0)
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
                Common = (catalog.ThingDef(stuff)?.StuffProps?.Commonality ?? 0) > 0,
            });
        }
        v = v with { Stuffed = true, StuffOptions = options };
    }

    /// <summary>
    /// The room-role furniture roles of a thing: the roles the game names for the def (carried per def by the
    /// catalog), a baby bed (a bed flagged bed_crib) and a deathrest-bindable building, a casket when it is a bed.
    /// </summary>
    private static List<string> RoomRoles(DefinitionCatalog catalog, string name, D.ThingDef row)
    {
        var roles = new List<string>(GameRoomRoles(name));
        if (row.Building?.BedCrib ?? false)
        {
            roles.Add(FurnitureRole.BabyBed.Value);
        }
        if (catalog.HasComp(row, BridgeConstants.ClassDeathrestComp))
        {
            roles.Add(catalog.ClassIsA(row.ThingClass, BridgeConstants.ClassBedThing) ? FurnitureRole.DeathrestCasket.Value : FurnitureRole.DeathrestAccelerator.Value);
        }
        roles.Sort(StringComparer.Ordinal);
        return roles;
    }

    private static void PlantView(DefinitionCatalog catalog, ref PlanningDefinition v, D.PlantProperties plant)
    {
        var biomes = new List<string>();
        foreach (var record in plant.WildBiomes)
        {
            biomes.Add(record.Value?.Biome ?? "");
        }
        v = v with
        {
            HarvestWork = FiniteFact(plant.HarvestWork),
            RequiresPollution = Known(plant.Pollution == D.Pollution.PollutedOnly),
            RequiresCleanSoil = Known(plant.Pollution == D.Pollution.CleanOnly),
            GrowDays = FiniteFact(plant.GrowDays),
            FertilityMin = FiniteFact(plant.FertilityMin),
            FertilitySensitivity = FiniteFact(plant.FertilitySensitivity),
            GrowMinGlow = FiniteFact(plant.GrowMinGlow),
            MinGrowthTemperature = FiniteFact(plant.MinGrowthTemperature),
            MinOptimalGrowthTemperature = FiniteFact(plant.MinOptimalGrowthTemperature),
            MaxOptimalGrowthTemperature = FiniteFact(plant.MaxOptimalGrowthTemperature),
            MaxGrowthTemperature = FiniteFact(plant.MaxGrowthTemperature),
            SowTags = Known(EquatableArray.From(plant.SowTags)),
            SowMinSkill = Known(plant.SowMinSkill),
            BlockAdjacentSow = Known(plant.BlockAdjacentSow),
            MustBeWildToSow = Known(plant.MustBeWildToSow),
            HarvestMinGrowth = FiniteFact(plant.HarvestMinGrowth),
            SowWork = FiniteFact(plant.SowWork),
            WildBiomes = Known(EquatableArray.From(biomes)),
            // Sowing waits on the plant's own research (TreeSowing, Devilstrand), which the thing's research
            // prerequisites do not list.
            Research = [.. v.Research, .. plant.SowResearchPrerequisites],
        };
        var product = plant.HarvestedThingDef;
        if (product == "")
        {
            return;
        }
        // The game's PlantProperties.HarvestDestroys is harvestAfterGrowth <= 0.
        v = v with { HarvestDestroysPlant = Known(plant.HarvestAfterGrowth <= 0) };
        var productRow = catalog.ThingDef(product)
            ?? throw new BridgeContractException($"plant harvests {product}, which the catalog has no def row for");
        v = v with
        {
            HarvestedThingDef = Known(product),
            HarvestYield = FiniteFact(plant.HarvestYield),
            RawPreferred = Known(productRow.Ingestible is not null && productRow.Ingestible.Preferability >= D.FoodPreferability.RawTasty),
            Edible = Known(catalog.IsFood(product)),
        };
        var (rot, perishable) = catalog.RotDays(product);
        v = v with { HarvestPerishable = Known(perishable) };
        if (perishable)
        {
            v = v with { HarvestRotDays = FiniteFact(rot) };
        }
        // A product the game shows no Nutrition stat for gives none.
        var (nutrition, _) = catalog.ShownStatValue(product, "", BridgeConstants.StatNutrition);
        v = v with { HarvestNutrition = FiniteFact(plant.HarvestYield * nutrition) };
    }

    private static PlanningDefinition TerrainView(DefinitionCatalog catalog, string name, D.TerrainDef row)
    {
        var v = new PlanningDefinition
        {
            Name = name,
            Terrain = Known(true),
            Research = [.. row.ResearchPrerequisites],
            ConstructionSkill = Known(row.ConstructionSkillPrerequisite),
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
        if (row.Tags.Count > 0)
        {
            v = v with { FloorTags = [.. row.Tags] };
        }
        return v;
    }
}
