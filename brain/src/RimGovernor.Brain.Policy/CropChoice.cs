using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Refuses a decision that needs the biome's darkness when it was not read: lit is never assumed.
/// </summary>
public sealed class OutdoorsDarkUnknownException()
    : DomainException("cannot tell whether the biome keeps the sky dark: the colony frame carried no biome or no definition catalog was loaded");

public sealed record CropClimate
{
    public bool Sowing { get; init; }

    public double DaysRemaining { get; init; }

    /// <summary>
    /// The biome's permanent darkness: the sky never lights the ground, so no crop that needs light grows
    /// outdoors whatever the season says. The native biome read is required.
    /// </summary>
    public bool OutdoorsDark { get; init; }

    /// <summary>The colony map's biome defName: a species the game only lets a player sow wild (MustBeWildToSow) is planted only where it is native.</summary>
    public string Biome { get; init; } = "";

    /// <summary>
    /// Whether a crop that needs light can be sown outdoors now: the native season read, false in a
    /// permanently dark biome.
    /// </summary>
    public bool SowingOutdoors()
    {
        var dark = OutdoorsDark;
        return dark ? false : Sowing;
    }
}

public sealed record CropChoice
{
    /// <summary>The native resource one harvest yields.</summary>
    public Resource Harvests { get; init; }

    /// <summary>The native units per cell per harvest.</summary>
    public double UnitsPerCell { get; init; }

    /// <summary>The sowing skill floor.</summary>
    public int SowMinSkill { get; init; }

    /// <summary>True when a harvest removes the plant (a tree is felled), so the cell is empty after.</summary>
    public bool HarvestDestroys { get; init; }

    // BlockAdjacentSow, MustBeWildToSow, HarvestMinGrowth, SowWork and WildBiomes price a tree plantation.
    public bool BlockAdjacentSow { get; init; }

    public bool MustBeWildToSow { get; init; }

    public double HarvestMinGrowth { get; init; }

    public double SowWork { get; init; }

    public EquatableArray<string> WildBiomes { get; init; }

    public string Name { get; init; } = "";

    public bool Available { get; init; }

    /// <summary>The research the crop's sowing waits on (the plant's own prerequisites, Devilstrand and TreeSowing); finished ones are still listed.</summary>
    public EquatableArray<ResearchProjectId> Research { get; init; }

    public bool Edible { get; init; }

    public double GrowDays { get; init; }

    public double FertilityMin { get; init; }

    public double FertilitySensitivity { get; init; }

    public double HarvestNutrition { get; init; }

    public double Demand { get; init; }

    /// <summary>The native sow tags ("Ground", "Hydroponic").</summary>
    public EquatableArray<string> SowTags { get; init; }

    /// <summary>The native minimum light for growth, zero for cave crops.</summary>
    public double MinGlow { get; init; }

    public double HarvestWork { get; init; }

    public bool RawPreferred { get; init; }

    public bool DietAllowed { get; init; }

    public bool RequiresPollution { get; init; }

    public bool RequiresCleanSoil { get; init; }

    /// <summary>The harvested item's rot days; counts only for a perishable item.</summary>
    public HarvestDecay? Decay { get; init; }

    /// <summary>The crop's growth temperature band (PlantProperties): no growth below the first or above the last, full rate between the two optimals.</summary>
    public double MinGrowthTemperature { get; init; }

    public double MinOptimalGrowthTemperature { get; init; }

    public double MaxOptimalGrowthTemperature { get; init; }

    public double MaxGrowthTemperature { get; init; }

    /// <summary>The fraction of every day the plant rests and does not grow (<see cref="PlantResting"/>).</summary>
    public double RestingDayFraction { get; init; }
}

/// <summary>One expansion decision: which edible crop to sow, and where, so that observed coverage reaches the reserve target.</summary>
public sealed record FieldRequest
{
    public EquatableArray<CropChoice> Choices { get; init; }

    public CropClimate Climate { get; init; } = new();

    public FoodRunway? Runway { get; init; }

    public long Colonists { get; init; }

    public int Growers { get; init; }

    public int Cooks { get; init; }

    public Calendar Calendar { get; init; }

    public EquatableArray<DisasterCondition> Conditions { get; init; }

    /// <summary>The stored-food buffer FieldTarget budgets beyond one cycle, spread over the harvests left in the season.</summary>
    public double ReserveDays { get; init; }

    /// <summary>The fraction of the target already growing (FieldCoverage).</summary>
    public double Coverage { get; init; }

    public FarmSiteRequest Site { get; init; } = new();
}

/// <summary>
/// One crop's plan over the soil it would actually use. Score is the plan's net nutrition per day divided by
/// the cells the crop needs, so a crop that cannot fill its own target is penalized.
/// </summary>
public sealed record FieldCandidate
{
    public CropChoice Crop { get; init; } = new();

    public int Needed { get; init; }

    public FarmSitePlan Sites { get; init; } = new();

    public double Score { get; init; }

    public string Reason { get; init; } = "";

    public EquatableArray<FarmSiteTerm> Terms { get; init; }

    /// <summary>A tree species' wood per labor tick over its land and work, zero for every other crop.</summary>
    public double Price { get; init; }
}

public sealed record FieldPlan
{
    public CropChoice Crop { get; init; } = new();

    public int Needed { get; init; }

    public FarmSitePlan Sites { get; init; } = new();

    public bool Urgent { get; init; }

    public EquatableArray<FieldCandidate> Candidates { get; init; }

    public string Explain()
    {
        var b = new StringBuilder();
        b.Append(FormattableString.Invariant($"{Crop.Name} needed={Needed} urgent={(Urgent ? "true" : "false")}"));
        foreach (var c in Candidates)
        {
            b.Append(FormattableString.Invariant($"\n {c.Crop.Name} needed={c.Needed} cells={c.Sites.Cells} score={c.Score:F4} {c.Reason}"));
        }
        return b.ToString();
    }
}

/// <summary>One viable crop with its screening results.</summary>
public sealed record ViableCrop
{
    public CropChoice Crop { get; init; } = new();

    public double Days { get; init; }

    public int Needed { get; init; }

    /// <summary>The resource units one cell yields per harvest when the crop is planned for a resource deficit; zero plans it for nutrition.</summary>
    public double Units { get; init; }

    /// <summary>The tree species' wood per labor tick, zero otherwise.</summary>
    public double Price { get; init; }
}

/// <summary>One viable crop for a new outdoor field block and the cells it still needs.</summary>
public sealed record FieldBlockOption(CropChoice Crop, int Needed);

public static class CropChoiceRules
{
    /// <summary>How near (cells) a growing zone must be to a new block for its crop to count as a neighbour.</summary>
    public const int FieldBlockNeighbourRadius = 11;

    /// <summary>A RimWorld year: an indoor field's reserve spreads over it.</summary>
    internal const double FieldYearDays = GameTime.DaysPerYear;

    /// <summary>
    /// Chooses the crop and patches jointly. Every available edible crop with complete native facts is a
    /// candidate; its patches come from sitePick over unroofed free soil (the plan field blocks when set) at the
    /// crop's own fertility floor, so a fertility-tolerant crop wins on poor soil where a richer crop would find
    /// no land. A remaining season shorter than 2.5 grow cycles excludes a crop.
    /// A stored-food runway shorter than 2.5 cycles of the fastest crop is urgent and also prefers the
    /// fastest crop. Existing zones are never re-cropped: the plan only adds patches.
    /// </summary>
    public static (FieldPlan Plan, bool Ok) PlanField(FieldRequest r)
    {
        var sowing = r.Climate.SowingOutdoors();
        if (!sowing)
        {
            return (new FieldPlan(), false);
        }
        return PlanFieldIn(r, false);
    }

    /// <summary>
    /// Screens the request's crops. With <paramref name="indoor"/> set the remaining outdoor season is ignored:
    /// a roofed, lit and heated site grows all year.
    /// </summary>
    internal static (List<ViableCrop> Viables, List<FieldCandidate> Excluded, bool Ok) ViableCrops(FieldRequest r, bool indoor)
    {
        var season = r.Climate.DaysRemaining;
        var fraction = r.Coverage;
        if (!FoodForecastRules.FoodNumber(fraction) || (!indoor && !FieldCapacityRules.FieldPositive(season)))
        {
            return ([], [], false);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var crop in r.Choices)
        {
            if (!seen.Add(crop.Name))
            {
                return ([], [], false);
            }
        }
        var viables = new List<ViableCrop>();
        var excluded = new List<FieldCandidate>();
        void Exclude(CropChoice crop, string reason) => excluded.Add(new FieldCandidate { Crop = crop, Reason = reason });
        foreach (var crop in r.Choices)
        {
            var allowed = crop.DietAllowed;
            if ((!allowed))
            {
                Exclude(crop, "crop diet unavailable or penalised");
                continue;
            }
            var available = crop.Available;
            var edible = crop.Edible;
            var days = crop.GrowDays;
            if (!FieldCapacityRules.FieldPositive(days))
            {
                throw new RequiredFactException("crop growth:" + crop.Name);
            }
            if (!available || !edible)
            {
                Exclude(crop, "not an available edible crop");
                continue;
            }
            if (!indoor && ControlledEnvironmentRules.GrowsInDark(crop))
            {
                Exclude(crop, "crop needs a dark room");
                continue;
            }
            if (!indoor && days * 2.5 > season)
            {
                Exclude(crop, "season too short");
                continue;
            }
            var left = indoor ? FieldYearDays : r.Climate.DaysRemaining;
            var count = FieldTarget(r.Colonists, crop, r.ReserveDays, left);
            var needed = (int)Math.Ceiling((count * (1 - fraction)) - 1e-9);
            if (needed <= 0)
            {
                return ([], [], false);
            }
            viables.Add(new ViableCrop { Crop = crop, Days = days, Needed = needed });
        }
        return (viables, excluded, true);
    }

    /// <summary>
    /// True when the stored-food runway is shorter than 2.5 cycles of the fastest viable crop.
    /// </summary>
    internal static bool FieldUrgency(FieldRequest r, IReadOnlyList<ViableCrop> viables)
    {
        if (viables.Count == 0)
        {
            return false;
        }
        var fastest = viables[0].Days;
        foreach (var v in viables)
        {
            fastest = Math.Min(fastest, v.Days);
        }
        return (r.Runway is FoodRunway.Days { Value: var foodDays } && FoodForecastRules.FoodNumber(foodDays) && foodDays < fastest * 2.5);
    }

    /// <summary>PlanField with the indoor switch.</summary>
    internal static (FieldPlan Plan, bool Ok) PlanFieldIn(FieldRequest r, bool indoor)
    {
        var (viables, excluded, ok) = ViableCrops(r, indoor);
        if (!ok)
        {
            return (new FieldPlan(), false);
        }
        var plan = new FieldPlan { Candidates = EquatableArray.From(excluded) };
        if (viables.Count == 0)
        {
            return (plan, false);
        }
        var urgent = FieldUrgency(r, viables);
        var candidates = new List<FieldCandidate>(excluded);
        foreach (var v in viables)
        {
            candidates.Add(FieldCandidateFor(r, v));
        }
        return RankField(plan with { Urgent = urgent, Candidates = EquatableArray.From(candidates) }, urgent);
    }

    /// <summary>Sites one viable crop on unroofed free soil (the plan field blocks when set) and scores it per needed cell.</summary>
    internal static FieldCandidate FieldCandidateFor(FieldRequest r, ViableCrop v)
    {
        var site = r.Site;
        var sites = SiteRules.SitePick(site, v, s => !s.Roofed && (site.Fields is null || site.Fields.Contains(s.Cell)), null, false);
        var c = new FieldCandidate { Crop = v.Crop, Needed = v.Needed, Sites = sites, Price = v.Price };
        if (sites.Cells == 0)
        {
            return c with { Reason = "no plantable soil" };
        }
        var terms = new List<FarmSiteTerm>(SiteRules.SiteTerms(sites));
        if (v.Units == 0)
        {
            terms.AddRange(CropResilienceRules.CropChoiceTerms(r, v.Crop, sites.Cells));
        }
        var total = 0.0;
        foreach (var term in terms)
        {
            total += term.Value;
        }
        return c with
        {
            Terms = EquatableArray.From(terms),
            Score = total / v.Needed,
            Reason = FormattableString.Invariant($"net {total:F4}/day over {sites.Patches.Count} patches"),
        };
    }

    /// <summary>Orders the plan's candidates and adopts the best plantable one.</summary>
    internal static (FieldPlan Plan, bool Ok) RankField(FieldPlan plan, bool urgent)
    {
        var ordered = plan.Candidates.OrderBy(c => c, Comparer<FieldCandidate>.Create((a, b) =>
            FieldCandidateLess(a, b, urgent) ? -1 : FieldCandidateLess(b, a, urgent) ? 1 : 0)).ToList();
        plan = plan with { Candidates = EquatableArray.From(ordered) };
        var best = ordered[0];
        if (best.Sites.Cells == 0)
        {
            return (plan, false);
        }
        return (plan with { Crop = best.Crop, Needed = best.Needed, Sites = best.Sites }, true);
    }

    /// <summary>
    /// Orders plantable candidates first, then the fastest crop under urgency, then the best-priced tree
    /// species, then the highest score, then the fastest crop, then by name.
    /// </summary>
    internal static bool FieldCandidateLess(FieldCandidate a, FieldCandidate b, bool urgent)
    {
        if ((a.Sites.Cells > 0) != (b.Sites.Cells > 0))
        {
            return a.Sites.Cells > 0;
        }
        var ad = a.Crop.GrowDays;
        var bd = b.Crop.GrowDays;
        if (urgent && ad != bd)
        {
            return ad < bd;
        }
        if (a.Price != b.Price)
        {
            return a.Price > b.Price;
        }
        if (a.Score != b.Score)
        {
            return a.Score > b.Score;
        }
        if (ad != bd)
        {
            return ad < bd;
        }
        return string.CompareOrdinal(a.Crop.Name, b.Crop.Name) < 0;
    }

    internal static (double Soil, bool Ok) FreeCropSoil(SiteCell cell, double minimum, IReadOnlySet<Cell>? blocked)
    {
        var soil = cell.Fertility;
        return (soil, (blocked is null || !blocked.Contains(cell.Cell)) && cell.Walkable && !cell.Occupied() && !cell.Zone && !cell.Roofed && FoodForecastRules.FoodNumber(soil) && soil >= minimum);
    }

    /// <summary>
    /// Orders the viable crops for a new field block: options keep their preference order, so urgency's
    /// fastest crop still leads the first block, but a crop growing in a neighbouring zone moves behind every
    /// other crop. A sole viable crop is still allowed.
    /// </summary>
    public static IReadOnlyList<FieldBlockOption> BlockCropOrder(IReadOnlyList<FieldBlockOption> options, IReadOnlySet<string> neighbours)
    {
        var result = new List<FieldBlockOption>(options.Count);
        result.AddRange(options.Where(o => !neighbours.Contains(o.Crop.Name)));
        result.AddRange(options.Where(o => neighbours.Contains(o.Crop.Name)));
        return result;
    }

    /// <summary>
    /// The cells of crop that feed the colony: each cell harvests yield every GrowDays, so steady state is
    /// demand*days/yield cells, and the reserve stock is spread over the harvests left in the season (one
    /// harvest when the season is shorter than a cycle).
    /// </summary>
    public static int FieldTarget(long colonists,CropChoice crop,double reserve,double season)
    {
        var demand = crop.Demand;
        if (demand == 0) return 0;
        var days = crop.GrowDays;
        var yield = crop.HarvestNutrition;
        if (colonists < 0 || !FieldCapacityRules.FieldPositive(demand) || !FieldCapacityRules.FieldPositive(days) || !FieldCapacityRules.FieldPositive(yield) || !FieldCapacityRules.FieldPositive(reserve))
        {
            throw new RequiredFactException("field target budget");
        }
        var harvests = 1.0;
        var left = season;
        if (FieldCapacityRules.FieldPositive(left))
        {
            harvests = Math.Max(1, Math.Floor(left / days));
        }
        var target = Math.Max(colonists * 10.0, Math.Ceiling(demand * (days + (reserve / harvests)) / yield));
        if (!FieldCapacityRules.FieldPositive(target) || target > 65536)
        {
            throw new RequiredFactException("field target budget");
        }
        return (int)target;
    }
}

/// <summary>A harvest product with a native rot component, measured in days.</summary>
public sealed record HarvestDecay(double Days);
