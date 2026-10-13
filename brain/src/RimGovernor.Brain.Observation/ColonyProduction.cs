namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    /// <summary>The bill's allowed defs; a butcher bill's filter is the corpse selection, not an ingredient list, so it stays unknown.</summary>
    private static Fact<IReadOnlyList<string>> BillIngredients(O.BillState b, RecipeRole role) =>
        b.IngredientFilter is null || role == RecipeRole.ButcherFlesh
            ? Unknown<IReadOnlyList<string>>()
            : Known<IReadOnlyList<string>>([.. b.IngredientFilter.AllowedDefNames]);

    private static Fact<bool> BillForever(bool has, Ops.RepeatMode mode) => has ? Known(mode == Ops.RepeatMode.Forever) : Unknown<bool>();

    private static Fact<string> RepeatModeFact(bool has, Ops.RepeatMode mode) => has ? Known(RepeatModeName(mode)) : Unknown<string>();

    private static Fact<bool> BillActive(bool has, bool suspended) => has ? Known(!suspended) : Unknown<bool>();

    /// <summary>The nutrition of the fresh humanlike corpses near a bench: each race's live MeatAmount sum times the Nutrition of that race's meat def; unknown when a race's meat shows no nutrition.</summary>
    private static Fact<double> HumanCorpseNutrition(IEnumerable<O.HumanCorpseMeat> meat, DefinitionCatalog? catalog)
    {
        var total = 0.0;
        foreach (var m in meat)
        {
            double per;
            bool ok;
            try
            {
                (per, ok) = RequireCatalog(catalog).RaceMeatNutrition(m.Race);
            }
            catch (DomainException)
            {
                return Unknown<double>();
            }
            if (!ok)
            {
                return Unknown<double>();
            }
            total += m.MeatAmount * per;
        }
        return Known(total);
    }

    /// <summary>A growing zone's raw crop facts for policy.</summary>
    internal static CropGrowth FarmGrowth(O.FarmFacts farm, IEnumerable<PlanningDefinition> definitions)
    {
        Fact<double> lo = default, loOptimal = default, hiOptimal = default, hi = default;
        foreach (var d in definitions)
        {
            if (d.Name == farm.Crop)
            {
                (lo, loOptimal, hiOptimal, hi) = (d.MinGrowthTemperature, d.MinOptimalGrowthTemperature, d.MaxOptimalGrowthTemperature, d.MaxGrowthTemperature);
            }
        }
        return new CropGrowth
        {
            Planted = CountFact(farm.HasPlantedCells, farm.PlantedCells),
            FertilePlanted = CountFact(farm.HasFertilePlantedCells, farm.FertilePlantedCells),
            GrowthMean = Optional(farm.HasGrowthMean, farm.GrowthMean),
            FertilityMean = Optional(farm.HasFertilityFactorMean, farm.FertilityFactorMean),
            Temperature = Optional(farm.HasTemperature, farm.Temperature),
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
    internal static Fact<IReadOnlyList<FoodField>> ColonyFoodFields(IReadOnlyList<O.FarmFacts>? farms, IReadOnlyList<PlanningDefinition> definitions, Fact<Calendar> calendar, IReadOnlyList<SiteCell> cells)
    {
        if (farms is null)
        {
            return Unknown<IReadOnlyList<FoodField>>();
        }
        var zones = ZoneRoofs(cells);
        var rows = new List<FoodField>();
        foreach (var farm in farms)
        {
            var growth = FarmGrowth(farm, definitions);
            if (farm.Zone is null || !growth.GrowingCells().TryGetValue(out var growing))
            {
                return Unknown<IReadOnlyList<FoodField>>();
            }
            var crop = new CropChoice { Name = farm.Crop, Edible = Optional(farm.HasEdibleCrop, farm.EdibleCrop) };
            var work = Unknown<double>();
            foreach (var d in definitions)
            {
                if (d.Name != farm.Crop)
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
            zones.TryGetValue(farm.Zone.Id, out var roof);
            rows.Add(new FoodField
            {
                Id = farm.Zone.Id,
                Plan = new FieldPlan { Crop = crop, Sites = new FarmSitePlan { Cells = (int)growing } },
                RemainingGrowDays = HarvestLeadDays(growth, crop.GrowDays, calendar),
                WorkPerDay = work,
                Exposure = new FieldExposure
                {
                    ZoneCells = roof.Cells,
                    UnroofedCells = roof.Unroofed,
                    Planted = CountFact(farm.HasPlantedCells, farm.PlantedCells),
                    Blighted = CountFact(farm.HasBlightedPlants, farm.BlightedPlants),
                },
            });
        }
        return Known<IReadOnlyList<FoodField>>(rows);
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
            if (!cell.ZoneID.TryGetValue(out var id))
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

    internal static Fact<IReadOnlyList<FieldCrop>> ColonyFieldCrops(IReadOnlyList<O.FarmFacts>? farms, IReadOnlyList<PlanningDefinition> definitions, bool usable = false)
    {
        if (farms is null)
        {
            return Unknown<IReadOnlyList<FieldCrop>>();
        }
        var rows = new List<FieldCrop>(farms.Count);
        foreach (var farm in farms)
        {
            var row = new FieldCrop { Edible = Optional(farm.HasEdibleCrop, farm.EdibleCrop), GrowingCells = FarmGrowth(farm, definitions).GrowingCells() };
            if (usable)
            {
                row = row with { GrowingCells = CountFact(farm.HasUsableCells, farm.UsableCells) };
            }
            foreach (var definition in definitions)
            {
                if (farm.HasCrop && definition.Name == farm.Crop)
                {
                    row = row with { GrowDays = definition.GrowDays, HarvestNutrition = definition.HarvestNutrition, Demand = definition.NutritionDemandPerDay };
                    break;
                }
            }
            rows.Add(row);
        }
        return Known<IReadOnlyList<FieldCrop>>(rows);
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
            if (!bench.HasUsable)
            {
                known = false;
                continue;
            }
            if (!bench.Usable)
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
                if (!bill.HasSuspended)
                {
                    known = false;
                }
                else if (!bill.Suspended)
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
        var name = product.DefName;
        var row = new ProductionProduct
        {
            Name = name,
            Demand = Optional(product.HasNutritionDemandPerDay, product.NutritionDemandPerDay),
            Edible = Optional(product.HasEdible, product.Edible),
            Storable = Optional(product.HasStorable, product.Storable),
        };
        if (catalog is null)
        {
            return row;
        }
        var (days, perishable) = catalog.RotDays(name);
        row = row with { Perishable = Known(perishable) };
        if (perishable)
        {
            row = row with { RotDays = Known(days) };
        }
        row = row with { BabyEdible = Known(catalog.BabyEdible(name)), Kind = catalog.FoodKindOf(name) };
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
        if (!pawns.TryGet(id, out var row) || row?.Settings?.PolicyInputs is not { } p)
        {
            return Unknown<HeldPrecepts>();
        }
        return Known(new HeldPrecepts { Ideo = p.IdeoId, Defs = [.. p.Precepts] });
    }

    internal static Fact<IReadOnlyList<ProductionBench>> ColonyProductionBenches(O.ColonyFactsSnapshot v, Buildings buildings, Pawns pawns, DefinitionCatalog? catalog)
    {
        if (HasIssue(v.Issues, "cooking") || HasIssue(v.Issues, "butchering") || !Headed(buildings, v.Cooking, b => b.Bench) || !Headed(buildings, v.Butchering, b => b.Bench))
        {
            return Unknown<IReadOnlyList<ProductionBench>>();
        }
        var rows = new List<ProductionBench>();
        ProductionBench Add(C.Ref? bench, O.SnapshotRef? snapshot, Fact<bool> usable, IEnumerable<O.RecipeState> recipes, IEnumerable<O.BillState> bills, IEnumerable<O.FoodProduction> production, bool butcher, C.Ref? room)
        {
            var row = new ProductionBench { Id = bench?.Id ?? "", Definition = buildings.Entity(bench)?.DefName ?? "", Usable = usable, Butcher = butcher, Room = OptionalRef(room) };
            if (snapshot is not null)
            {
                row = row with { Token = Optional(snapshot.HasToken, snapshot.Token) };
            }
            var recipeRows = new List<ProductionRecipe>();
            foreach (var r in recipes)
            {
                var name = r.Recipe?.DefName ?? "";
                var recipe = new ProductionRecipe { Name = name, Role = RequireCatalog(catalog).RecipeRole(name), Bulk = catalog!.RecipeBulk(name) };
                var meal = catalog.RecipeMealFacts(name, row.Definition);
                recipe = recipe with
                {
                    Mood = meal.Mood,
                    NutrientEfficiency = meal.NutrientEfficiency,
                    WorkPerNutrition = meal.WorkPerNutrition,
                    NeedsPower = meal.NeedsPower,
                    IngredientClasses = meal.IngredientClasses,
                    CookSkillFloor = meal.CookSkillFloor,
                };
                if (r.HasAvailableNow && r.HasAvailableOnBench)
                {
                    recipe = recipe with { Available = Known(r.AvailableNow && r.AvailableOnBench) };
                }
                var products = new List<ProductionProduct>();
                foreach (var p in production)
                {
                    if (p.Recipe != name)
                    {
                        continue;
                    }
                    if (p.HasAvailable)
                    {
                        recipe = recipe with { Available = Known(p.Available && r.AvailableNow && r.AvailableOnBench) };
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
                var role = RequireCatalog(catalog).RecipeRole(b.Recipe?.DefName ?? "");
                billRows.Add(new ExistingProductionBill
                {
                    Role = role,
                    DefaultIngredients = Optional(b.HasDefaultIngredients, b.DefaultIngredients),
                    UnrestrictedWorker = Optional(b.HasUnrestrictedWorker, b.UnrestrictedWorker),
                    Worker = Known(b.Worker?.Id ?? ""),
                    RepeatMode = RepeatModeFact(b.HasRepeatMode, b.RepeatMode),
                    Ingredients = BillIngredients(b, role),
                    Id = b.Id,
                    Managed = Optional(b.HasManagedUnchanged, b.ManagedUnchanged),
                    Active = BillActive(b.HasSuspended, b.Suspended),
                    Recipe = b.Recipe?.DefName ?? "",
                    TargetCount = Optional(b.HasTargetCount, b.TargetCount),
                    Forever = BillForever(b.HasRepeatMode, b.RepeatMode),
                    Humanlike = butcher && (b.IngredientFilter?.AllowedDefNames.Count ?? 0) > 0,
                });
            }
            return row with { Recipes = recipeRows, Bills = billRows };
        }
        foreach (var b in v.Cooking)
        {
            rows.Add(Add(b.Bench, b.BenchSnapshot, Optional(b.HasUsable, b.Usable), b.Recipes, b.Bills, b.Production, false, b.Room));
        }
        foreach (var b in v.Butchering)
        {
            var row = Add(b.Bench, b.BenchSnapshot, Optional(b.HasUsable, b.Usable), b.Recipes, b.Bills, [], true, b.Room);
            var butchers = new List<HumanButcherCandidate>();
            foreach (var candidate in b.HumanButchers)
            {
                var traits = new List<PawnTrait>();
                foreach (var t in candidate.PawnTraits)
                {
                    traits.Add(ResolveTrait(catalog, new PawnTrait { Name = t.DefName, Degree = (int)t.Degree }));
                }
                butchers.Add(new HumanButcherCandidate
                {
                    Id = new PawnId(candidate.PawnId),
                    Traits = Known<IReadOnlyList<PawnTrait>>(traits),
                    Held = HeldPreceptsOf(pawns, candidate.PawnId),
                    CanWork = Optional(candidate.HasCanWork, candidate.CanWork),
                });
            }
            rows.Add(row with { HumanCorpseNutrition = HumanCorpseNutrition(b.HumanCorpseMeat, catalog), HumanButchers = butchers });
        }
        return Known<IReadOnlyList<ProductionBench>>(rows);
    }

    /// <summary>
    /// Projects the native food climate into the routine calendar fact. Every day count and the sowing flag
    /// must be present; the season name and day of year are informational and default when absent.
    /// </summary>
    internal static Fact<Calendar> ColonyCalendar(O.FoodClimate climate)
    {
        if (!climate.HasGrowingDays || !climate.HasGrowingDaysRemaining || !climate.HasGrowingDaysUntil || !climate.HasNonGrowingDays || !climate.HasSowingNow)
        {
            return Unknown<Calendar>();
        }
        var c = new Calendar
        {
            Season = climate.Season,
            DayOfYear = climate.DayOfYear,
            GrowingDays = climate.GrowingDays,
            GrowingDaysRemaining = climate.GrowingDaysRemaining,
            GrowingDaysUntil = climate.GrowingDaysUntil,
            NonGrowingDays = climate.NonGrowingDays,
            Sowing = climate.SowingNow,
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
            if (!farm.HasEdibleCrop)
            {
                known = false;
                continue;
            }
            if (!farm.EdibleCrop)
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
