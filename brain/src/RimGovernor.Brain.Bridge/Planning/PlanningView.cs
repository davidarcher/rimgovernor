using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using CatalogStat = RimGovernor.Brain.Bridge.CatalogStat;
using CatalogStats = RimGovernor.Brain.Bridge.CatalogStats;
using GrowerFertility = RimGovernor.Brain.GameFacts.Observations.GrowerFertility;
namespace RimGovernor.Brain.Bridge;
// The planning rows are views over the catalog's generated def rows and stat table: nothing here is read from a
// native planning writer. A row a view needs and the catalog lacks is an error, never a default.
public static class PlanningViewReads
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

    internal static double Finite(double v) => double.IsFinite(v) ? v : throw new RequiredFactException("finite definition value");
    internal static double Nonnegative(double v) => double.IsFinite(v) && v >= 0 ? v : throw new RequiredFactException("nonnegative definition value");
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
    private static readonly CatalogStat[] OptionStats = [CatalogStats.MaxHitPoints, CatalogStats.Flammability, CatalogStats.BedRestEffectiveness, CatalogStats.WorkToBuild, CatalogStats.DoorOpenSpeed, ];
    private static PlanningDefinition ThingView(DefinitionCatalog catalog, string name, Verse.ThingDef row)
    {
        var v = new PlanningDefinition
        {
            Name = name,
            PasteDispenser = typeof(RimWorld.Building_NutrientPasteDispenser).IsAssignableFrom(row.thingClass),
            Research = [..(row.researchPrerequisites ?? []).Select(project => project.defName)],
            ConstructionSkill = row.constructionSkillPrerequisite,
            PathCost = row.pathCost,
        };
        v = v with
        {
            Size = new Bounds(row.size.x, row.size.z)
        };
        var draw = DefinitionCatalog.PowerDraw(row);
        v = v with
        {
            NeedsPower = draw > 0
        };
        if (draw is { } watts)
        {
            v = v with
            {
                Power = (v.Power ?? new PlanningPower())with
                {
                    PowerW = Finite(watts)
                }
            };
        }

        if (row.GetCompProperties<RimWorld.CompProperties_Explosive>()is { } explosive)
        {
            v = v with
            {
                Explosive = (v.Explosive ?? new PlanningExplosive())with
                {
                    ExplosiveRadius = Nonnegative(explosive.explosiveRadius)
                }
            };
        }

        if (typeof(RimWorld.Building_MechCharger).IsAssignableFrom(row.thingClass))
        {
            v = v with
            {
                MechCharger = true
            };
        }

        // sowTag also permits plants beneath ordinary buildings (for example ButcherSpot); it is not a grower type.
        if (typeof(RimWorld.Building_PlantGrower).IsAssignableFrom(row.thingClass))
        {
            v = v with
            {
                Grower = Finite(row.fertility) >= 0 ? new GrowerFertility.Fixed(row.fertility) : new GrowerFertility.Ground()
            };
        }

        v = v with
        {
            RoomRoles = RoomRoles(catalog, name, row)
        };
        if (row.plant is { } plant)
        {
            PlantView(catalog, ref v, plant);
        }

        if (DefinitionCatalog.Buildable(row))
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
            v = v with
            {
                FixedConstruction = (v.FixedConstruction ?? new PlanningFixedConstruction())with
                {
                    Costs = EquatableArray.From(Amounts(costs))
                }
            };
            v = v with
            {
                FixedConstruction = v.FixedConstruction!with
                {
                    WorkToBuild = Nonnegative(catalog.PlannerStatValue(name, "", CatalogStats.WorkToBuild))
                }
            };
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
                var(shownValue, ok) = catalog.ShownStatValue(name, stuff, stat);
                if (ok)
                {
                    stats[stat.Name] = shownValue;
                }
            }

            stats[CatalogStats.WorkToBuild.Name] = Nonnegative(catalog.PlannerStatValue(name, stuff, CatalogStats.WorkToBuild));
            options.Add(new StuffOption { Stuff = stuff, Costs = Amounts(costs), Value = value, Stats = stats, Common = (catalog.FindThing(stuff)?.stuffProps ?? throw new RequiredFactException("stuff properties:" + stuff)).commonality > 0, });
        }

        v = v with
        {
            Stuffed = true,
            StuffOptions = options
        };
    }

    /// <summary>
    /// The room-role furniture roles of a thing: the roles the game names for the def (carried per def by the
    /// catalog), a baby bed (a bed flagged bed_crib) and a deathrest-bindable building, a casket when it is a bed.
    /// </summary>
    private static List<string> RoomRoles(DefinitionCatalog catalog, string name, Verse.ThingDef row)
    {
        var roles = new List<string>(catalog.GameRoomRoles(name));
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
            biomes.Add(record.biome?.defName ?? throw new RequiredFactException("wild biome definition"));
        }

        v = v with
        {
            // Sowing waits on the plant's own research (TreeSowing, Devilstrand), which the thing's research
            // prerequisites do not list.
            Research = [..v.Research, ..(plant.sowResearchPrerequisites ?? []).Select(project => project.defName)],
            Plant = (v.Plant ?? new PlanningPlant())with
            {
                HarvestWork = Finite(plant.harvestWork),
                RequiresPollution = plant.RequiresPollution,
                RequiresCleanSoil = plant.RequiresNoPollution,
                GrowDays = Finite(plant.growDays),
                FertilityMin = Finite(plant.fertilityMin),
                FertilitySensitivity = Finite(plant.fertilitySensitivity),
                GrowMinGlow = Finite(plant.growMinGlow),
                MinGrowthTemperature = Finite(plant.minGrowthTemperature),
                MinOptimalGrowthTemperature = Finite(plant.minOptimalGrowthTemperature),
                MaxOptimalGrowthTemperature = Finite(plant.maxOptimalGrowthTemperature),
                MaxGrowthTemperature = Finite(plant.maxGrowthTemperature),
                RestingDayFraction = PlantResting.RestingDayFraction,
                SowTags = EquatableArray.From(plant.sowTags ?? []),
                SowMinSkill = plant.sowMinSkill,
                BlockAdjacentSow = plant.blockAdjacentSow,
                MustBeWildToSow = plant.mustBeWildToSow,
                HarvestMinGrowth = Finite(plant.harvestMinGrowth),
                SowWork = Finite(plant.sowWork),
                WildBiomes = EquatableArray.From(biomes)
            }
        };
        var product = plant.harvestedThingDef?.defName ?? "";
        if (product == "")
        {
            return;
        }

        // The game's PlantProperties.HarvestDestroys is harvestAfterGrowth <= 0.
        v = v with
        {
            Harvest = (v.Harvest ?? new PlanningHarvest())with
            {
                HarvestDestroysPlant = plant.harvestAfterGrowth <= 0
            }
        };
        var productRow = catalog.FindThing(product) ?? throw new BridgeContractException($"plant harvests {product}, which the catalog has no def row for");
        v = v with
        {
            Harvest = (v.Harvest ?? new PlanningHarvest())with
            {
                HarvestedThingDef = product,
                HarvestYield = Finite(plant.harvestYield),
                RawPreferred = productRow.ingestible is not null && productRow.ingestible.preferability >= RimWorld.FoodPreferability.RawTasty,
                Edible = catalog.IsFood(product)
            }
        };
        v = v with
        {
            Harvest = v.Harvest!with
            {
                Decay = catalog.RotDays(product)is { } days ? new HarvestDecay(Nonnegative(days)) : null,
            },
        };
        // A product the game shows no Nutrition stat for gives none.
        var(nutrition, _) = catalog.ShownStatValue(product, "", CatalogStats.Nutrition);
        v = v with
        {
            Harvest = (v.Harvest ?? new PlanningHarvest())with
            {
                HarvestNutrition = Finite(plant.harvestYield * nutrition)
            }
        };
    }

    private static PlanningDefinition TerrainView(DefinitionCatalog catalog, string name, Verse.TerrainDef row)
    {
        var v = new PlanningDefinition
        {
            Name = name,
            Terrain = true,
            Research = [..(row.researchPrerequisites ?? []).Select(project => project.defName)],
            ConstructionSkill = row.constructionSkillPrerequisite,
            Size = new Bounds(1, 1),
        };
        v = v with
        {
            FixedConstruction = (v.FixedConstruction ?? new PlanningFixedConstruction())with
            {
                Costs = EquatableArray.From(Amounts(catalog.TerrainAdjustedCosts(name)))
            }
        };
        var floor = catalog.FloorTerrain(name);
        v = v with
        {
            PathCost = floor.PathCost,
            Floor = (v.Floor ?? new PlanningFloor())with
            {
                Cleanliness = Finite(floor.Cleanliness),
                Beauty = Finite(floor.Beauty),
                Flammability = Finite(floor.Flammability)
            },
            FixedConstruction = (v.FixedConstruction ?? new PlanningFixedConstruction())with
            {
                WorkToBuild = Nonnegative(catalog.TerrainWorkToBuild(name))
            }
        };
        if (row.tags is { Count: > 0 } floorTags)
        {
            v = v with
            {
                Floor = (v.Floor ?? new PlanningFloor())with
                {
                    FloorTags = [..floorTags]
                }
            };
        }

        return v;
    }
}
