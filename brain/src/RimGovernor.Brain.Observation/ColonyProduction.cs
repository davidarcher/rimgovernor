namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    /// <summary>The bill's allowed defs; a butcher bill's filter is the corpse selection, not an ingredient list, so it stays unknown.</summary>
    private static Fact<EquatableArray<string>> BillIngredients(O.BillState b, RecipeRole role) =>
        b.IngredientFilter is null || role == RecipeRole.ButcherFlesh
            ? Unknown<EquatableArray<string>>()
            : Known<EquatableArray<string>>([.. b.IngredientFilter.AllowedDefNames]);

    private static Fact<bool> BillForever(bool has, Ops.RepeatMode mode) => has ? Known(mode == Ops.RepeatMode.Forever) : Unknown<bool>();

    private static Fact<string> RepeatModeFact(bool has, Ops.RepeatMode mode) => has ? Known(RepeatModeName(mode)) : Unknown<string>();

    private static Fact<bool> BillActive(bool has, bool suspended) => has ? Known(!suspended) : Unknown<bool>();

    /// <summary>The nutrition of the fresh humanlike corpses near a bench: each race's live MeatAmount sum times the Nutrition of that race's meat def; unknown when a race's meat shows no nutrition.</summary>
    private static Fact<double> HumanCorpseNutrition(IEnumerable<O.HumanCorpseMeat> meat, DefinitionCatalog? catalog)
    {
        var total = 0.0;
        foreach (var m in meat)
        {
            double? per;
            try
            {
                per = RequireCatalog(catalog).RaceMeatNutrition((m.Race ?? ""));
            }
            catch (DomainException)
            {
                return Unknown<double>();
            }
            if (per is not { } nutrition)
            {
                return Unknown<double>();
            }
            total += m.MeatAmount.GetValueOrDefault() * nutrition;
        }
        return Known(total);
    }

    /// <summary>A growing zone's raw crop facts for policy.</summary>
    internal static CropGrowth FarmGrowth(O.FarmFacts farm, IEnumerable<PlanningDefinition> definitions)
    {
        Fact<double> lo = default, loOptimal = default, hiOptimal = default, hi = default;
        foreach (var d in definitions)
        {
            if (d.Name == (farm.Crop ?? ""))
            {
                (lo, loOptimal, hiOptimal, hi) = (d.MinGrowthTemperature, d.MinOptimalGrowthTemperature, d.MaxOptimalGrowthTemperature, d.MaxGrowthTemperature);
            }
        }
        return new CropGrowth
        {
            Planted = CountFact(farm.PlantedCells is not null, farm.PlantedCells.GetValueOrDefault()),
            FertilePlanted = CountFact(farm.FertilePlantedCells is not null, farm.FertilePlantedCells.GetValueOrDefault()),
            GrowthMean = Optional(farm.GrowthMean is not null, farm.GrowthMean.GetValueOrDefault()),
            FertilityMean = Optional(farm.FertilityFactorMean is not null, farm.FertilityFactorMean.GetValueOrDefault()),
            Temperature = Optional(farm.Temperature is not null, farm.Temperature.GetValueOrDefault()),
            MinGrowth = lo,
            MinOptimal = loOptimal,
            MaxOptimal = hiOptimal,
            MaxGrowth = hi,
        };
    }

    /// <summary>
    /// Computes each field's harvest lead in policy from the raw growth facts, the current temperature and the
    /// calendar; an unknown fact leaves the lead unknown. A planted field is future capacity, not an observed
    /// delivery of edible stock. Exposure reads each zone's roofs from the planning window's cells (unknown
    /// without the window or with an unread roof) and its blight from the zone's planted and blighted counts.
    /// </summary>
    internal static Fact<EquatableArray<FoodField>> ColonyFoodFields(IReadOnlyList<O.FarmFacts>? farms, IReadOnlyList<PlanningDefinition> definitions, Fact<Calendar> calendar, IReadOnlyList<SiteCell> cells)
    {
        if (farms is null)
        {
            return Unknown<EquatableArray<FoodField>>();
        }
        var zones = ZoneRoofs(cells);
        var rows = new List<FoodField>();
        foreach (var farm in farms)
        {
            var growth = FarmGrowth(farm, definitions);
            if (farm.Zone is null || !growth.GrowingCells().TryGetValue(out var growing))
            {
                return Unknown<EquatableArray<FoodField>>();
            }
            var crop = new CropChoice { Name = farm.Crop ?? "", Edible = Optional(farm.EdibleCrop is not null, farm.EdibleCrop.GetValueOrDefault()) };
            var work = Unknown<double>();
            foreach (var d in definitions)
            {
                if (d.Name != (farm.Crop ?? ""))
                {
                    continue;
                }
                crop = crop with
                {
                    GrowDays = d.GrowDays,
                    HarvestNutrition = d.HarvestNutrition,
                    RotDays = d.HarvestRotDays,
                    Perishable = d.HarvestPerishable,
                    RawPreferred = d.RawPreferred,
                };
                if (d.GrowDays.TryGetValue(out var days) && days > 0 && d.HarvestWork.TryGetValue(out var harvest))
                {
                    work = Known(harvest * growing / days);
                }
            }
            zones.TryGetValue((farm.Zone.Id ?? ""), out var roof);
            rows.Add(new FoodField
            {
                Id = (farm.Zone.Id ?? ""),
                Plan = new FieldPlan { Crop = crop, Sites = new FarmSitePlan { Cells = (int)growing } },
                RemainingGrowDays = HarvestLeadDays(growth, crop.GrowDays, calendar),
                WorkPerDay = work,
                Exposure = new FieldExposure
                {
                    ZoneCells = roof.Cells,
                    UnroofedCells = roof.Unroofed,
                    Planted = CountFact(farm.PlantedCells is not null, farm.PlantedCells.GetValueOrDefault()),
                    Blighted = CountFact(farm.BlightedPlants is not null, farm.BlightedPlants.GetValueOrDefault()),
                },
            });
        }
        return Known(EquatableArray.From(rows));
    }

    /// <summary>Counts a zone's held cells and those under open sky.</summary>
    internal readonly record struct ZoneRoof(Fact<long> Cells, Fact<long> Unroofed);

    /// <summary>
    /// Counts each growing zone's cells in the planning window and the ones with no roof. A zone with a cell
    /// whose roof is unread is unknown: a partial count would pass for the field's exposure. Without the window
    /// no zone has a row, so every exposure reads unknown.
    /// </summary>
    internal static Dictionary<string, ZoneRoof> ZoneRoofs(IEnumerable<SiteCell> cells)
    {
        var counts = new Dictionary<string, (long Cells, long Unroofed, bool Unknown)>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            if (!cell.Zone.TryGetValue(out var zoned) || !zoned)
            {
                continue;
            }
            if (!cell.ZoneId.TryGetValue(out var id))
            {
                continue;
            }
            var t = counts.GetValueOrDefault(id);
            if (!cell.Roofed.TryGetValue(out var roofed))
            {
                t.Unknown = true;
            }
            else if (!roofed)
            {
                t.Unroofed++;
            }
            t.Cells++;
            counts[id] = t;
        }
        var output = new Dictionary<string, ZoneRoof>(counts.Count, StringComparer.Ordinal);
        foreach (var (id, t) in counts)
        {
            if (!t.Unknown)
            {
                output[id] = new ZoneRoof(Known(t.Cells), Known(t.Unroofed));
            }
        }
        return output;
    }

    internal static Fact<EquatableArray<FieldCrop>> ColonyFieldCrops(IReadOnlyList<O.FarmFacts>? farms, IReadOnlyList<PlanningDefinition> definitions, bool usable = false)
    {
        if (farms is null)
        {
            return Unknown<EquatableArray<FieldCrop>>();
        }
        var rows = new List<FieldCrop>(farms.Count);
        foreach (var farm in farms)
        {
            var row = new FieldCrop { Edible = Optional(farm.EdibleCrop is not null, farm.EdibleCrop.GetValueOrDefault()), GrowingCells = FarmGrowth(farm, definitions).GrowingCells() };
            if (usable)
            {
                row = row with { GrowingCells = CountFact(farm.UsableCells is not null, farm.UsableCells.GetValueOrDefault()) };
            }
            foreach (var definition in definitions)
            {
                if (farm.Crop is not null && definition.Name == (farm.Crop ?? ""))
                {
                    row = row with { GrowDays = definition.GrowDays, HarvestNutrition = definition.HarvestNutrition, Demand = definition.NutritionDemandPerDay };
                    break;
                }
            }
            rows.Add(row);
        }
        return Known(EquatableArray.From(rows));
    }

    internal static RoundsFacts ColonyProduction(O.ColonyFactsSnapshot v, RoundsFacts facts)
    {
        if (HasIssue(v.Issues, "cooking"))
        {
            return facts;
        }
        bool ready = false, known = true;
        foreach (var bench in v.Cooking)
        {
            if (bench.Usable is null)
            {
                known = false;
                continue;
            }
            if (!bench.Usable.GetValueOrDefault())
            {
                continue;
            }
            var recipes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in bench.Recipes)
            {
                recipes.Add(recipe.Recipe?.DefName ?? "");
            }
            foreach (var bill in bench.Bills)
            {
                if (!recipes.Contains(bill.Recipe?.DefName ?? ""))
                {
                    continue;
                }
                if (bill.Suspended is null)
                {
                    known = false;
                }
                else if (!bill.Suspended.GetValueOrDefault())
                {
                    ready = true;
                }
            }
        }
        return ready || known ? facts with { Cooking = Known(ready) } : facts;
    }

    /// <summary>
    /// A recipe product: the frame's varying facts (count, demand, storage room) joined to what its def row says
    /// (nutrition from the stat table, rot days, baby edibility). Without a catalog the static facts stay
    /// unknown. The game shows no Nutrition stat for a def that gives none: that nutrition stays unknown, it is
    /// not zero.
    /// </summary>
    private static ProductionProduct ProductionProductOf(O.FoodProduct product, DefinitionCatalog? catalog)
    {
        var name = (product.DefName ?? "");
        var row = new ProductionProduct
        {
            Name = name,
            Demand = Optional(product.NutritionDemandPerDay is not null, product.NutritionDemandPerDay.GetValueOrDefault()),
            Edible = Optional(product.Edible is not null, product.Edible.GetValueOrDefault()),
            Storable = Optional(product.Storable is not null, product.Storable.GetValueOrDefault()),
        };
        if (catalog is null)
        {
            return row;
        }
        var rot = catalog.RotDays(name);
        row = row with { Perishable = Known(rot is not null) };
        if (rot is { } days)
        {
            row = row with { RotDays = Known(days) };
        }
        row = row with { BabyEdible = Known(catalog.BabyEdible(name)), Kind = catalog.FoodKindOf(name) ?? FoodKind.None };
        try
        {
            row = row with { Nutrition = Known((double)catalog.StatValue(name, "", BridgeConstants.StatNutrition)) };
        }
        catch (DomainException)
        {
            // The game shows no Nutrition stat for the def: the nutrition stays unknown.
        }
        return row;
    }

    /// <summary>The pawn's own ideoligion and precepts from its row's policy inputs; unknown while the table lacks the row or the inputs.</summary>
    private static Fact<HeldPrecepts> HeldPreceptsOf(Pawns pawns, string id)
    {
        if (!pawns.TryGetValue(id, out var row) || row?.Settings?.PolicyInputs is not { } p)
        {
            return Unknown<HeldPrecepts>();
        }
        return Known(new HeldPrecepts { Ideo = p.IdeoId ?? "", Defs = [.. p.Precepts] });
    }

    internal static Fact<EquatableArray<ProductionBench>> ColonyProductionBenches(O.ColonyFactsSnapshot v, Buildings buildings, Pawns pawns, DefinitionCatalog? catalog)
    {
        if (HasIssue(v.Issues, "cooking") || HasIssue(v.Issues, "butchering") || !Headed(buildings, v.Cooking, b => b.Bench) || !Headed(buildings, v.Butchering, b => b.Bench))
        {
            return Unknown<EquatableArray<ProductionBench>>();
        }
        var rows = new List<ProductionBench>();
        ProductionBench Add(C.Ref? bench, O.SnapshotRef? snapshot, Fact<bool> usable, IEnumerable<O.RecipeState> recipes, IEnumerable<O.BillState> bills, IEnumerable<O.FoodProduction> production, bool butcher, C.Ref? room)
        {
            var row = new ProductionBench { Id = bench?.Id ?? "", Definition = buildings.Entity(bench)?.DefName ?? "", Usable = usable, Butcher = butcher, Room = OptionalRef(room) };
            if (snapshot is not null)
            {
                row = row with { Token = Optional(snapshot.Token is not null, (snapshot.Token ?? "")) };
            }
            var recipeRows = new List<ProductionRecipe>();
            foreach (var r in recipes)
            {
                var name = r.Recipe?.DefName ?? "";
                var recipe = new ProductionRecipe { Name = name, Role = RequireCatalog(catalog).RecipeRoleOf(name), Bulk = RequireCatalog(catalog).RecipeBulk(name) };
                var meal = RequireCatalog(catalog).RecipeMealFacts(name, row.Definition);
                recipe = recipe with
                {
                    Mood = meal.Mood,
                    NutrientEfficiency = meal.NutrientEfficiency,
                    WorkPerNutrition = meal.WorkPerNutrition,
                    NeedsPower = meal.NeedsPower,
                    IngredientClasses = meal.IngredientClasses,
                    CookSkillFloor = meal.CookSkillFloor,
                };
                if (r.AvailableNow is not null && r.AvailableOnBench is not null)
                {
                    recipe = recipe with { Available = Known(r.AvailableNow.GetValueOrDefault() && r.AvailableOnBench.GetValueOrDefault()) };
                }
                var products = new List<ProductionProduct>();
                foreach (var p in production)
                {
                    if ((p.Recipe ?? "") != name)
                    {
                        continue;
                    }
                    if (p.Available is not null)
                    {
                        recipe = recipe with { Available = Known(p.Available.GetValueOrDefault() && r.AvailableNow.GetValueOrDefault() && r.AvailableOnBench.GetValueOrDefault()) };
                    }
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
                var role = RequireCatalog(catalog).RecipeRoleOf(b.Recipe?.DefName ?? "");
                billRows.Add(new ExistingProductionBill
                {
                    Role = role,
                    DefaultIngredients = Optional(b.DefaultIngredients is not null, b.DefaultIngredients.GetValueOrDefault()),
                    UnrestrictedWorker = Optional(b.UnrestrictedWorker is not null, b.UnrestrictedWorker.GetValueOrDefault()),
                    Worker = Known(b.Worker?.Id ?? ""),
                    RepeatMode = RepeatModeFact(b.RepeatMode is not null, b.RepeatMode.GetValueOrDefault()),
                    Ingredients = BillIngredients(b, role),
                    Id = b.Id ?? "",
                    Managed = Optional(b.ManagedUnchanged is not null, b.ManagedUnchanged.GetValueOrDefault()),
                    Active = BillActive(b.Suspended is not null, b.Suspended.GetValueOrDefault()),
                    Recipe = b.Recipe?.DefName ?? "",
                    TargetCount = Optional(b.TargetCount is not null, b.TargetCount.GetValueOrDefault()),
                    Forever = BillForever(b.RepeatMode is not null, b.RepeatMode.GetValueOrDefault()),
                    Humanlike = butcher && (b.IngredientFilter?.AllowedDefNames.Count ?? 0) > 0,
                });
            }
            return row with { Recipes = recipeRows, Bills = billRows };
        }
        foreach (var b in v.Cooking)
        {
            rows.Add(Add(b.Bench, b.BenchSnapshot, Optional(b.Usable is not null, b.Usable.GetValueOrDefault()), b.Recipes, b.Bills, b.Production, false, b.Room));
        }
        foreach (var b in v.Butchering)
        {
            var row = Add(b.Bench, b.BenchSnapshot, Optional(b.Usable is not null, b.Usable.GetValueOrDefault()), b.Recipes, b.Bills, [], true, b.Room);
            var butchers = new List<HumanButcherCandidate>();
            foreach (var candidate in b.HumanButchers)
            {
                var traits = new List<PawnTrait>();
                foreach (var t in candidate.PawnTraits)
                {
                    traits.Add(ResolveTrait(catalog, new PawnTrait { Name = t.DefName ?? "", Degree = (int)t.Degree.GetValueOrDefault() }));
                }
                butchers.Add(new HumanButcherCandidate
                {
                    ID = new PawnId(candidate.PawnId),
                    Traits = Known(EquatableArray.From(traits)),
                    Held = HeldPreceptsOf(pawns, candidate.PawnId),
                    CanWork = Optional(candidate.CanWork is not null, candidate.CanWork.GetValueOrDefault()),
                });
            }
            rows.Add(row with { HumanCorpseNutrition = HumanCorpseNutrition(b.HumanCorpseMeat, catalog), HumanButchers = butchers });
        }
        return Known(EquatableArray.From(rows));
    }

    /// <summary>
    /// Projects the native food climate into the routine calendar fact. Every day count and the sowing flag
    /// must be present; the season name and day of year are informational and default when absent.
    /// </summary>
    internal static Fact<Calendar> ColonyCalendar(O.FoodClimate climate)
    {
        if (climate.GrowingDays is null || climate.GrowingDaysRemaining is null || climate.GrowingDaysUntil is null || climate.NonGrowingDays is null || climate.SowingNow is null)
        {
            return Unknown<Calendar>();
        }
        var c = new Calendar
        {
            Season = climate.Season ?? "",
            DayOfYear = climate.DayOfYear.GetValueOrDefault(),
            GrowingDays = climate.GrowingDays.GetValueOrDefault(),
            GrowingDaysRemaining = climate.GrowingDaysRemaining.GetValueOrDefault(),
            GrowingDaysUntil = climate.GrowingDaysUntil.GetValueOrDefault(),
            NonGrowingDays = climate.NonGrowingDays.GetValueOrDefault(),
            Sowing = climate.SowingNow.GetValueOrDefault(),
        };
        return c.Valid() ? Known(c) : Unknown<Calendar>();
    }

    internal static RoundsFacts ZoneProduction(IReadOnlyList<O.FarmFacts>? farms, IReadOnlyList<PlanningDefinition> definitions, RoundsFacts facts)
    {
        if (farms is null)
        {
            return facts;
        }
        long growing = 0;
        var known = true;
        foreach (var farm in farms)
        {
            if (farm.EdibleCrop is null)
            {
                known = false;
                continue;
            }
            if (!farm.EdibleCrop.GetValueOrDefault())
            {
                continue;
            }
            if (!FarmGrowth(farm, definitions).GrowingCells().TryGetValue(out var cells))
            {
                known = false;
                continue;
            }
            growing += cells;
        }
        return known ? facts with { GrowingCells = Known(growing) } : facts;
    }
}

public sealed partial record ColonyProjection
{
    /// <summary>Uses the configured reserve target at the review boundary.</summary>
    public ColonyProjection ApplyFieldBudget(double reserveDays) =>
        this with { Facts = Facts with { FieldCoverage = PolicyFunctions.FieldCoverage(Facts.Colonists, FieldCrops, reserveDays) } };
}
