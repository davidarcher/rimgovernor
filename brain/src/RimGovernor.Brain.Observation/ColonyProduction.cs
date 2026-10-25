using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Observation;

public static class ColonyProductionReads
{
    /// <summary>A bench bill's repeat mode. Native reads only vanilla's three supported modes.</summary>
    public static BillRepeat BillRepeatOf(Ops.RepeatMode mode)
    {
        switch (mode)
        {
            case Ops.RepeatMode.Forever:
                return BillRepeat.Forever;
            case Ops.RepeatMode.Count:
                return BillRepeat.Count;
            case Ops.RepeatMode.Target:
                return BillRepeat.TargetCount;
        }
        throw new DomainException($"undefined repeat mode {(int)mode}");
    }

    /// <summary>A bench bill as the production bill selectors read it. A butcher bench's bill is humanlike when its filter admits any corpse (native lists only humanlike ones).</summary>
    public static ExistingProductionBill ExistingBillOf(O.BillState b, RecipeRole role, bool butcher) => new()
    {
        Role = role,
        DefaultIngredients = b.DefaultIngredients,
        UnrestrictedWorker = b.UnrestrictedWorker,
        Worker = b.Worker ?? "",
        Repeat = BillRepeatOf(b.RepeatMode),
        Ingredients = b.IngredientFilter.AllowedDefNames,
        Id = b.Id,
        Active = !b.Suspended,
        Recipe = b.Recipe,
        TargetCount = b.TargetCount,
        Humanlike = butcher && b.IngredientFilter.AllowedDefNames.Count > 0,
    };

    /// <summary>The nutrition of the fresh humanlike corpses near a bench: each race's live MeatAmount sum times the Nutrition of that race's meat def; a race whose meat shows no nutrition adds none.</summary>
    private static double HumanCorpseNutrition(IEnumerable<O.HumanCorpseMeat> meat, DefinitionCatalog? catalog)
    {
        var defs = ColonyDecodeReads.RequireCatalog(catalog);
        var total = 0.0;
        foreach (var m in meat)
        {
            total += m.MeatAmount * (defs.RaceMeatNutrition(m.Race) ?? 0);
        }
        return total;
    }

    /// <summary>A growing zone's raw crop facts for policy: the planted plants' means are their totals over the planted count, and an unplanted zone has none.</summary>
    internal static CropGrowth FarmGrowth(O.ZoneState.Growing farm, IEnumerable<PlanningDefinition> definitions)
    {
        var plant = definitions.FirstOrDefault(d => d.Name == farm.Crop)?.Plant ?? throw new RequiredFactException("zone crop definition:" + farm.Crop);
        if (!double.IsFinite(farm.Temperature))
        {
            throw new RequiredFactException("zone temperature");
        }
        CropStand stand = new CropStand.Fallow();
        if (farm.PlantedCells > 0)
        {
            var growth = farm.GrowthTotal / farm.PlantedCells;
            var fertility = farm.FertilityFactorTotal / farm.PlantedCells;
            if (!double.IsFinite(growth) || growth < 0 || !double.IsFinite(fertility) || fertility < 0)
            {
                throw new RequiredFactException("zone plant means");
            }
            stand = new CropStand.Sown(farm.FertilePlantedCells, growth, fertility);
        }
        return new CropGrowth
        {
            Stand = stand,
            Temperature = farm.Temperature,
            MinGrowth = plant.MinGrowthTemperature,
            MinOptimal = plant.MinOptimalGrowthTemperature,
            MaxOptimal = plant.MaxOptimalGrowthTemperature,
            MaxGrowth = plant.MaxGrowthTemperature,
        };
    }

    /// <summary>
    /// Computes each field's harvest lead in policy from the raw growth facts, the current temperature and the
    /// calendar. Fallow and dormant fields offer no arrival. A planted field is future capacity, not an observed
    /// delivery of edible stock. Exposure uses the native whole-zone roof and plant counts.
    /// </summary>
    internal static EquatableArray<FoodField> ColonyFoodFields(IReadOnlyList<O.ZoneState.Growing> farms, IReadOnlyList<PlanningDefinition> definitions, Calendar calendar)
    {
        var rows = new List<FoodField>();
        foreach (var farm in farms)
        {
            if (farm.Crop.Length == 0) continue;
            var definition = definitions.FirstOrDefault(d => d.Name == farm.Crop) ?? throw new RequiredFactException("zone crop definition:" + farm.Crop);
            var growth = FarmGrowth(farm, definitions);
            if (definition.Harvest is null) continue;
            var crop = definition.CropChoice();
            if (CalendarRules.HarvestLeadDays(growth, crop.GrowDays, calendar) is not HarvestTiming.Expected lead) continue;
            var growing = growth.GrowingCells();
            rows.Add(new FoodField
            {
                Id = farm.Id,
                Plan = new FieldPlan { Crop = crop, Sites = new FarmSitePlan { Cells = checked((int)growing) } },
                RemainingGrowDays = lead.Days,
                WorkPerDay = crop.HarvestWork * growing / crop.GrowDays,
                Exposure = new FieldExposure { ZoneCells = farm.ZoneCells, UnroofedCells = farm.UnroofedCells, Planted = farm.PlantedCells, Blighted = farm.BlightedPlants },
            });
        }
        return EquatableArray.From(rows);
    }
    internal static EquatableArray<FieldCrop> ColonyFieldCrops(IReadOnlyList<O.ZoneState.Growing> farms, IReadOnlyList<PlanningDefinition> definitions, bool usable = false)
    {
        var rows = new List<FieldCrop>();
        foreach (var farm in farms)
        {
            if (farm.Crop.Length == 0) continue;
            var definition = definitions.FirstOrDefault(d => d.Name == farm.Crop) ?? throw new RequiredFactException("zone crop definition:" + farm.Crop);
            if (definition.Plant is null) throw new RequiredFactException("zone plant definition:" + farm.Crop);
            if (definition.Harvest is null) continue;
            var crop = definition.CropChoice();
            rows.Add(new FieldCrop { Edible = crop.Edible, GrowingCells = usable ? farm.UsableCells : FarmGrowth(farm, definitions).GrowingCells(), GrowDays = crop.GrowDays, HarvestNutrition = crop.HarvestNutrition, Demand = crop.Demand });
        }
        return EquatableArray.From(rows);
    }
    internal static RoundsFacts ColonyProduction(O.ColonyFactsSnapshot v, RoundsFacts facts)
    {
        var ready = false;
        foreach (var bench in v.Cooking)
        {
            if (!bench.Usable)
            {
                continue;
            }
            var recipes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in bench.Recipes)
            {
                recipes.Add(recipe.Recipe);
            }
            foreach (var bill in bench.Bills)
            {
                if (recipes.Contains(bill.Recipe) && !bill.Suspended)
                {
                    ready = true;
                }
            }
        }
        return facts with { Cooking = ready };
    }

    /// <summary>
    /// A recipe product: the frame's varying facts (whether a human eats it, storage room) joined to what its def row
    /// says (food kind, the Nutrition the game shows, rot, baby edibility). A def the catalog gives no food kind is
    /// no food; the game shows no Nutrition stat for a def that gives none, so its nutrition is 0.
    /// </summary>
    private static ProductionProduct ProductionProductOf(O.FoodProduct product, DefinitionCatalog? catalog)
    {
        var name = product.DefName;
        var defs = ColonyDecodeReads.RequireCatalog(catalog);
        if (defs.FoodKindOf(name) is not { } kind)
        {
            return new ProductionProduct { Name = name, Food = new ProductFood.NotFood() };
        }
        var (nutrition, shown) = defs.ShownStatValue(name, "", CatalogStats.Nutrition);
        return new ProductionProduct
        {
            Name = name,
            Food = new ProductFood.Food(kind, shown ? nutrition : 0, product.Edible, defs.RotDays(name) is not null, defs.BabyEdible(name), product.Storable),
        };
    }

    /// <summary>The pawn's own ideoligion and precepts from its required pawn row.</summary>
    internal static HeldPrecepts HeldPreceptsOf(Pawns pawns, string id)
    {
        if (!pawns.TryGetValue(id, out var row) || row is null)
        {
            throw new ObservationContractException($"human butcher {id} has no pawn row");
        }
        return row.Settings.PolicyInputs.Ideo is O.PawnIdeo.Member m ? new HeldPrecepts { Ideo = m.IdeoId, Defs = [.. m.Precepts] } : new HeldPrecepts();
    }

    /// <summary>The definition of the building a bench row names; a bench the frame's building table does not hold breaks the frame's contract.</summary>
    internal static string BenchDefinition(Buildings buildings, C.Ref bench) =>
        buildings.Entity(bench)?.DefName ?? throw new ObservationContractException($"bench {bench.Id} has no building row");

    /// <summary>The frame's cooking benches, each with the room it stands in (none outdoors).</summary>
    internal static EquatableArray<CookingBench> ColonyCookingBenches(O.ColonyFactsSnapshot v, Buildings buildings)
    {
        var benches = new List<CookingBench>(v.Cooking.Count);
        foreach (var bench in v.Cooking)
        {
            benches.Add(new CookingBench
            {
                Id = bench.Bench.Id,
                Definition = BenchDefinition(buildings, bench.Bench),
                Usable = bench.Usable,
                Room = bench.Room?.Id,
                AutoRefuel = bench.AutoRefuel,
            });
        }
        return EquatableArray.From(benches);
    }

    /// <summary>The frame's butchering benches, each with the room it stands in (none outdoors).</summary>
    internal static EquatableArray<CookingBench> ColonyButcheringBenches(O.ColonyFactsSnapshot v, Buildings buildings)
    {
        var benches = new List<CookingBench>(v.Butchering.Count);
        foreach (var bench in v.Butchering)
        {
            benches.Add(new CookingBench
            {
                Id = bench.Bench.Id,
                Definition = BenchDefinition(buildings, bench.Bench),
                Usable = bench.Usable,
                Room = bench.Room?.Id,
            });
        }
        return EquatableArray.From(benches);
    }

    /// <summary>The standing butcher benches and the rooms they stand in.</summary>
    internal static EquatableArray<ButcherBench> ColonyButcherBenches(EquatableArray<CookingBench> butchering)
    {
        var standing = new List<ButcherBench>(butchering.Count);
        foreach (var bench in butchering)
        {
            standing.Add(new ButcherBench { Definition = bench.Definition, Room = bench.Room });
        }
        return EquatableArray.From(standing);
    }

    internal static EquatableArray<ProductionBench> ColonyProductionBenches(O.ColonyFactsSnapshot v, Buildings buildings, Pawns pawns, DefinitionCatalog? catalog)
    {
        var rows = new List<ProductionBench>();
        ProductionBench Add(C.Ref bench, O.SnapshotRef snapshot, bool usable, IEnumerable<O.RecipeState> recipes, IEnumerable<O.BillState> bills, IEnumerable<O.FoodProduction> production, bool butcher, C.Ref? room)
        {
            var row = new ProductionBench
            {
                Id = bench.Id,
                Definition = BenchDefinition(buildings, bench),
                Token = snapshot.Token ?? throw new RequiredFactException("bench_snapshot.token:" + bench.Id),
                Usable = usable,
                Butcher = butcher,
                Room = room?.Id,
            };
            var recipeRows = new List<ProductionRecipe>();
            foreach (var r in recipes)
            {
                var name = r.Recipe;
                var recipeCatalog = ColonyDecodeReads.RequireCatalog(catalog);
                var recipe = new ProductionRecipe { Name = name, Role = recipeCatalog.RecipeRoleOf(name), Bulk = recipeCatalog.RecipeBulk(name), Food = recipeCatalog.RecipeFood(name), Available = r.AvailableNow && r.AvailableOnBench };
                var products = new List<ProductionProduct>();
                foreach (var p in production)
                {
                    if (p.Recipe != name)
                    {
                        continue;
                    }
                    recipe = recipe with { Available = p.Available && r.AvailableNow && r.AvailableOnBench };
                    foreach (var product in p.Products)
                    {
                        products.Add(ProductionProductOf(product, catalog));
                    }
                }
                recipeRows.Add(recipe with { Products = products });
            }
            var billRows = new List<ExistingProductionBill>();
            foreach (var b in bills)
            {
                billRows.Add(ExistingBillOf(b, ColonyDecodeReads.RequireCatalog(catalog).RecipeRoleOf(b.Recipe), butcher));
            }
            return row with { Recipes = recipeRows, Bills = billRows };
        }
        foreach (var b in v.Cooking)
        {
            rows.Add(Add(b.Bench, b.BenchSnapshot, b.Usable, b.Recipes, b.Bills, b.Production, false, b.Room));
        }
        foreach (var b in v.Butchering)
        {
            var row = Add(b.Bench, b.BenchSnapshot, b.Usable, b.Recipes, b.Bills, [], true, b.Room);
            var butchers = new List<HumanButcherCandidate>();
            foreach (var candidate in b.HumanButchers)
            {
                var traits = new List<PawnTrait>();
                foreach (var t in candidate.PawnTraits)
                {
                    traits.Add(RoundsWorkReads.ResolveTrait(catalog, new PawnTrait { Name = t.DefName, Degree = (int)t.Degree }));
                }
                butchers.Add(new HumanButcherCandidate
                {
                    ID = new PawnId(candidate.PawnId),
                    Traits = EquatableArray.From(traits),
                    Held = HeldPreceptsOf(pawns, candidate.PawnId),
                    CanWork = candidate.CanWork,
                });
            }
            rows.Add(row with { HumanCorpseNutrition = HumanCorpseNutrition(b.HumanCorpseMeat, catalog), HumanButchers = butchers });
        }
        return EquatableArray.From(rows);
    }

    /// <summary>
    /// Projects the native food climate into the routine calendar fact. Every count is a required wire value.
    /// </summary>
    internal static Calendar ColonyCalendar(O.FoodClimate climate)
    {
        var c = new Calendar
        {
            GrowingYear = climate.GrowingYear,
            Season = climate.Season,
            DayOfYear = climate.DayOfYear,
            GrowingDays = climate.GrowingDays,
            GrowingDaysRemaining = climate.GrowingDaysRemaining,
            GrowingDaysUntil = climate.GrowingDaysUntil,
            NonGrowingDays = climate.NonGrowingDays,
        };
        return c.Valid() ? c : throw new BridgeContractException("invalid total food climate census");
    }

    internal static RoundsFacts ZoneProduction(IReadOnlyList<O.ZoneState.Growing> farms, IReadOnlyList<PlanningDefinition> definitions, RoundsFacts facts)
    {
        var growing = farms.Where(farm => farm.EdibleCrop).Sum(farm => FarmGrowth(farm, definitions).GrowingCells());
        return facts with { GrowingCells = growing };
    }
}

public sealed partial record ColonyProjection
{
    /// <summary>Uses the configured reserve target at the review boundary.</summary>
    public ColonyProjection ApplyFieldBudget(double reserveDays) =>
        this with { Facts = Facts with { FieldCoverage = FieldCapacityRules.FieldCoverage(Facts.Colonists, FieldCrops, reserveDays) } };
}
