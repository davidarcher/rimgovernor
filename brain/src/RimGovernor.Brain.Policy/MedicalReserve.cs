namespace RimGovernor.Brain.Policy;

/// <summary>An observed medicine stack, not a recipe or promised output.</summary>
public sealed record MedicineStackRow
{
    public string Id { get; init; } = "";
    public Resource Definition { get; init; }
    public long Count { get; init; }
    public bool Forbidden { get; init; }
    public Fact<bool> Perishable { get; init; }
    public Fact<long> RotTicks { get; init; }
}

public sealed record MedicalReserveObservation
{
    public long Colonists { get; init; }
    public Fact<EquatableArray<MedicineStackRow>> Items { get; init; }

    /// <summary>The complete usable native stock census, excluding inaccessible and forbidden stock. It is capped again against the actual medicine stacks.</summary>
    public Fact<EquatableArray<Amount>> Resources { get; init; }

    /// <summary>Ranks the medicines: the best one is no reserve.</summary>
    public ItemFacts Catalog { get; init; } = new();
}

public sealed record MedicalReservePolicy(long MinimumPerColonist, long TargetPerColonist)
{
    public static MedicalReservePolicy Default() => new(1, 3);
}

public sealed record MedicalReserveReview
{
    public bool Active { get; init; }
    public Fact<long> Stock { get; init; }
    public Fact<long> Target { get; init; }
    public Fact<long> Replenish { get; init; }
}

public enum MedicineMethodKind
{
    [WireName("")]
    None = 0,
    Unknown,
    Recovered,
    Produce,

    [WireName("wait_for_existing_work")]
    Wait,

    [WireName("no_eligible_method")]
    Blocked,
}

public sealed record MedicineMethod
{
    public MedicineMethodKind Kind { get; init; }
    public string Bench { get; init; } = "";
    public string Recipe { get; init; } = "";
    public long Target { get; init; }
}

/// <summary>
/// Names the one resource medical/reserves replenishes (MedicineHerbal; the planner refuses to run without
/// native confirmation that definition exists) and reuses the exact bench/recipe census shape GearProduce
/// established (GearBench/GearRecipe): the native reads are fully generic, so no separate medical bench census
/// type is needed.
/// </summary>
public sealed record MedicinePlanningRequest
{
    public MedicalReserveReview Review { get; init; } = new();
    public Resource Resource { get; init; }
    public IReadOnlyList<MethodId> Seen { get; init; } = [];
    public Fact<EquatableArray<GearBench>> Benches { get; init; }
}

public static partial class MedicalRules
{
    /// <summary>
    /// Preserves the latch through unavailable reads. Equal entry stock does not activate it; equal recovery stock
    /// clears it. Future production and expired stacks never contribute to the current reserve.
    /// </summary>
    public static MedicalReserveReview ReviewMedicalReserve(MedicalReserveObservation v, bool active, MedicalReservePolicy p)
    {
        var r = new MedicalReserveReview { Active = active };
        const string Invalid = "invalid medicine reserve facts or thresholds";
        if (p.MinimumPerColonist < 0 || p.TargetPerColonist <= p.MinimumPerColonist)
        {
            throw new DomainException(Invalid);
        }
        var people = v.Colonists;
        var ik = v.Items.TryGetValue(out var items);
        var rk = v.Resources.TryGetValue(out var resources);
        if (people < 0 || (people > 0 && p.TargetPerColonist > long.MaxValue / people))
        {
            throw new DomainException(Invalid);
        }
        // The best medicine is no reserve; without the catalog's medicines the reserve is unknown like any unread fact.
        Resource best;
        try
        {
            best = v.Catalog.BestMedicine();
        }
        catch (DomainException)
        {
            return r;
        }
        if (!ik || !rk)
        {
            return r;
        }
        var stockByDefinition = new Dictionary<Resource, long>();
        foreach (var q in resources!)
        {
            if (!AdmissionRules.ValidResource(q.Resource) || q.Count < 0)
            {
                throw new DomainException(Invalid);
            }
            if (!stockByDefinition.TryAdd(q.Resource, q.Count))
            {
                throw new DomainException(Invalid);
            }
        }
        var usable = new Dictionary<Resource, long>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items!)
        {
            if (!FoodForecastRules.FoodId(item.Id) || seen.Contains(item.Id) || !AdmissionRules.ValidResource(item.Definition) || item.Count < 0)
            {
                throw new DomainException(Invalid);
            }
            seen.Add(item.Id);
            var known = item.Perishable.TryGetValue(out var perishable);
            var tk = item.RotTicks.TryGetValue(out var ticks);
            if (tk && ticks < 0)
            {
                throw new DomainException(Invalid);
            }
            if (item.Definition == best || item.Forbidden || !((known && !perishable) || (tk && ticks > 0)))
            {
                continue;
            }
            if (item.Count > long.MaxValue - usable.GetValueOrDefault(item.Definition))
            {
                throw new DomainException(Invalid);
            }
            usable[item.Definition] = usable.GetValueOrDefault(item.Definition) + item.Count;
        }
        long stock = 0;
        foreach (var (definition, count) in usable)
        {
            var add = Math.Min(count, stockByDefinition.GetValueOrDefault(definition));
            if (add > long.MaxValue - stock)
            {
                throw new DomainException(Invalid);
            }
            stock += add;
        }
        var entry = people * p.MinimumPerColonist;
        var target = people * p.TargetPerColonist;
        var threshold = active ? target : entry;
        var nowActive = people > 0 && stock < threshold;
        var replenishment = nowActive ? Math.Max(0, target - stock) : 0;
        return r with { Active = nowActive, Stock = Known(stock), Target = Known(target), Replenish = Known(replenishment) };
    }

    private static MethodId MedicineMethodId(Resource resource, string bench, string recipe) =>
        new($"medicine-produce-{resource.Value}-{bench}-{recipe}");

    /// <summary>
    /// Proposes one repeat-count StockTarget bill that keeps at least the reviewed recovery Target units of
    /// Resource in stock, the same pause-when-satisfied bill GearProduce dispatches through ProductionBillAction.
    /// It issues no game orders and does not reserve resources; the shared method admission must recheck these
    /// costs against concurrent plans before committing. With the reserve active, the single Resource target is
    /// either already covered by an observed active bill (Wait), fundable at some bench/recipe (Produce) or blocked.
    /// </summary>
    public static MedicineMethod SelectMedicineMethod(MedicinePlanningRequest r)
    {
        if (!r.Review.Active)
        {
            return new MedicineMethod { Kind = MedicineMethodKind.Recovered };
        }
        if (!r.Review.Target.TryGetValue(out var target) || !AdmissionRules.ValidResource(r.Resource))
        {
            return new MedicineMethod { Kind = MedicineMethodKind.Unknown };
        }
        if (target <= 0)
        {
            return new MedicineMethod { Kind = MedicineMethodKind.Recovered };
        }
        if (target > int.MaxValue)
        {
            return new MedicineMethod { Kind = MedicineMethodKind.Blocked };
        }
        var seen = new HashSet<MethodId>();
        foreach (var id in r.Seen)
        {
            if (!FoodForecastRules.FoodId(id.Value) || seen.Contains(id))
            {
                throw new DomainException("invalid medicine method history");
            }
            seen.Add(id);
        }
        if (!r.Benches.TryGetValue(out var benchRows))
        {
            return new MedicineMethod { Kind = MedicineMethodKind.Unknown };
        }
        GearRules.ValidateGearProduction(benchRows);
        var benches = benchRows.OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
        foreach (var b in benches)
        {
            foreach (var bill in b.Bills)
            {
                if (!GearRules.ContainsResource(bill.Products, r.Resource))
                {
                    continue;
                }
                if (bill.Active)
                {
                    return new MedicineMethod { Kind = MedicineMethodKind.Wait };
                }
            }
        }
        foreach (var b in benches)
        {
            foreach (var recipe in b.Recipes.OrderBy(x => x.Definition, StringComparer.Ordinal))
            {
                if (!GearRules.ContainsResource(recipe.Products, r.Resource) || !recipe.Available || !recipe.AvailableOn)
                {
                    continue;
                }
                var id = MedicineMethodId(r.Resource, b.Id, recipe.Definition);
                if (seen.Contains(id))
                {
                    return new MedicineMethod { Kind = MedicineMethodKind.Wait };
                }
                return new MedicineMethod
                {
                    Kind = MedicineMethodKind.Produce,
                    Bench = b.Id,
                    Recipe = recipe.Definition,
                    Target = target,
                };
            }
        }
        return new MedicineMethod { Kind = MedicineMethodKind.Blocked };
    }
}
