using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Refuses a decision that needs the biome's darkness when it was not read: lit is never assumed
/// (Go <c>ErrOutdoorsDarkUnknown</c>).
/// </summary>
public sealed class OutdoorsDarkUnknownException()
    : DomainException("cannot tell whether the biome keeps the sky dark: the colony frame carried no biome or no definition catalog was loaded");

public sealed record CropClimate
{
    public Fact<bool> Sowing { get; init; }

    public Fact<double> DaysRemaining { get; init; }

    /// <summary>
    /// The biome's permanent darkness: the sky never lights the ground, so no crop that needs light grows
    /// outdoors whatever the season says. Unknown refuses: nothing is sown outdoors on a guess.
    /// </summary>
    public Fact<bool> OutdoorsDark { get; init; }

    /// <summary>The colony map's biome defName: a species the game only lets a player sow wild (MustBeWildToSow) is planted only where it is native.</summary>
    public Fact<string> Biome { get; init; }

    /// <summary>
    /// Whether a crop that needs light can be sown outdoors now: the native season read, false in a
    /// permanently dark biome, unknown while the darkness is unknown.
    /// </summary>
    public Fact<bool> SowingOutdoors()
    {
        if (!OutdoorsDark.TryGetValue(out var dark))
        {
            return Unknown<bool>();
        }
        return dark ? Known(false) : Sowing;
    }
}

public sealed record CropChoice
{
    /// <summary>The resource one harvest yields; unknown stays unknown, never zero.</summary>
    public Fact<Resource> Harvests { get; init; }

    /// <summary>The units per cell per harvest; unknown stays unknown, never zero.</summary>
    public Fact<double> UnitsPerCell { get; init; }

    /// <summary>The sowing skill floor.</summary>
    public Fact<int> SowMinSkill { get; init; }

    /// <summary>True when a harvest removes the plant (a tree is felled), so the cell is empty after.</summary>
    public Fact<bool> HarvestDestroys { get; init; }

    // BlockAdjacentSow, MustBeWildToSow, HarvestMinGrowth, SowWork and WildBiomes price a tree plantation.
    public Fact<bool> BlockAdjacentSow { get; init; }

    public Fact<bool> MustBeWildToSow { get; init; }

    public Fact<double> HarvestMinGrowth { get; init; }

    public Fact<double> SowWork { get; init; }

    public Fact<EquatableArray<string>> WildBiomes { get; init; }

    public string Name { get; init; } = "";

    public Fact<bool> Available { get; init; }

    public Fact<bool> Edible { get; init; }

    public Fact<double> GrowDays { get; init; }

    public Fact<double> FertilityMin { get; init; }

    public Fact<double> FertilitySensitivity { get; init; }

    public Fact<double> HarvestNutrition { get; init; }

    public Fact<double> Demand { get; init; }

    /// <summary>The native sow tags ("Ground", "Hydroponic").</summary>
    public Fact<EquatableArray<string>> SowTags { get; init; }

    /// <summary>The native minimum light for growth, zero for cave crops.</summary>
    public Fact<double> MinGlow { get; init; }

    public Fact<double> HarvestWork { get; init; }

    public Fact<bool> RawPreferred { get; init; }

    public Fact<bool> DietAllowed { get; init; }

    public Fact<bool> RequiresPollution { get; init; }

    public Fact<bool> RequiresCleanSoil { get; init; }

    /// <summary>The harvested item's rot days; counts only for a perishable item.</summary>
    public Fact<double> RotDays { get; init; }

    public Fact<bool> Perishable { get; init; }
}

/// <summary>One expansion decision: which edible crop to sow, and where, so that observed coverage reaches the reserve target.</summary>
public sealed record FieldRequest
{
    public EquatableArray<CropChoice> Choices { get; init; }

    public CropClimate Climate { get; init; } = new();

    public Fact<double> Runway { get; init; }

    public Fact<long> Colonists { get; init; }

    public Fact<int> Growers { get; init; }

    public Fact<int> Cooks { get; init; }

    public Fact<Calendar> Calendar { get; init; }

    public Fact<EquatableArray<DisasterCondition>> Conditions { get; init; }

    /// <summary>The stored-food buffer FieldTarget budgets beyond one cycle, spread over the harvests left in the season.</summary>
    public double ReserveDays { get; init; }

    /// <summary>The fraction of the target already growing (FieldCoverage).</summary>
    public Fact<double> Coverage { get; init; }

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

    public bool Urgent { get; init; }

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

public static partial class PolicyFunctions
{
    /// <summary>How near (cells) a growing zone must be to a new block for its crop to count as a neighbour.</summary>
    public const int FieldBlockNeighbourRadius = 11;

    /// <summary>A RimWorld year: an indoor field's reserve spreads over it.</summary>
    internal const double FieldYearDays = GameTime.DaysPerYear;

    /// <summary>
    /// Chooses the crop and patches jointly. Every available edible crop with complete native facts is a
    /// candidate; its patches come from sitePick over unroofed free soil (the plan field blocks when set) at the
    /// crop's own fertility floor, so a fertility-tolerant crop wins on poor soil where a richer crop would find
    /// no land. A remaining season shorter than 2.5 grow cycles excludes a crop; an unknown remaining season
    /// while sowing is possible is treated as short, so the fastest crop that can plant is chosen rather than
    /// nothing. A stored-food runway shorter than 2.5 cycles of the fastest crop is urgent and also prefers the
    /// fastest crop. Existing zones are never re-cropped: the plan only adds patches.
    /// </summary>
    public static (FieldPlan Plan, bool Ok) PlanField(FieldRequest r)
    {
        if (!r.Climate.SowingOutdoors().TryGetValue(out var sowing) || !sowing)
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
        var seasonKnown = r.Climate.DaysRemaining.TryGetValue(out var season);
        if (!r.Coverage.TryGetValue(out var fraction) || !FoodNumber(fraction) || (!indoor && seasonKnown && !FieldPositive(season)))
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
            var known = crop.DietAllowed.TryGetValue(out var allowed);
            if ((known && !allowed) || (GrowsInDark(crop) && !known))
            {
                Exclude(crop, "crop diet unavailable or penalised");
                continue;
            }
            var ak = crop.Available.TryGetValue(out var available);
            var ek = crop.Edible.TryGetValue(out var edible);
            var gk = crop.GrowDays.TryGetValue(out var days);
            if (!ak || !ek || !gk || !FieldPositive(days))
            {
                Exclude(crop, "incomplete crop facts");
                continue;
            }
            if (!available || !edible)
            {
                Exclude(crop, "not an available edible crop");
                continue;
            }
            if (!indoor && GrowsInDark(crop))
            {
                Exclude(crop, "crop needs a dark room");
                continue;
            }
            if (!indoor && seasonKnown && days * 2.5 > season)
            {
                Exclude(crop, "season too short");
                continue;
            }
            var left = indoor ? Known(FieldYearDays) : r.Climate.DaysRemaining;
            if (!FieldTarget(r.Colonists, crop, r.ReserveDays, left).TryGetValue(out var count))
            {
                Exclude(crop, "field target unknown");
                continue;
            }
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
    /// True when the stored-food runway is shorter than 2.5 cycles of the fastest viable crop, or (outdoors)
    /// when the remaining season is unknown while sowing is possible.
    /// </summary>
    internal static bool FieldUrgency(FieldRequest r, IReadOnlyList<ViableCrop> viables, bool indoor)
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
        var seasonKnown = r.Climate.DaysRemaining.IsKnown;
        var runwayKnown = r.Runway.TryGetValue(out var foodDays);
        return (!indoor && !seasonKnown) || (runwayKnown && FoodNumber(foodDays) && foodDays < fastest * 2.5);
    }

    /// <summary>The Go <c>planField</c>: PlanField with the indoor switch.</summary>
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
        var urgent = FieldUrgency(r, viables, indoor);
        var candidates = new List<FieldCandidate>(excluded);
        foreach (var v in viables)
        {
            candidates.Add(FieldCandidateFor(r, v, urgent));
        }
        return RankField(plan with { Urgent = urgent, Candidates = EquatableArray.From(candidates) }, urgent);
    }

    /// <summary>Sites one viable crop on unroofed free soil (the plan field blocks when set) and scores it per needed cell.</summary>
    internal static FieldCandidate FieldCandidateFor(FieldRequest r, ViableCrop v, bool urgent)
    {
        var site = r.Site;
        var sites = SitePick(site, v, s => SiteKnownFalse(s.Roofed) && (site.Fields is null || site.Fields.GetValueOrDefault(s.Cell)), null, false);
        var c = new FieldCandidate { Crop = v.Crop, Needed = v.Needed, Sites = sites, Urgent = urgent, Price = v.Price };
        if (sites.Cells == 0)
        {
            return c with { Reason = "no plantable soil" };
        }
        var terms = new List<FarmSiteTerm>(SiteTerms(sites));
        if (v.Units == 0)
        {
            terms.AddRange(CropChoiceTerms(r, v.Crop, sites.Cells));
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
        a.Crop.GrowDays.TryGetValue(out var ad);
        b.Crop.GrowDays.TryGetValue(out var bd);
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

    internal static (double Soil, bool Ok) FreeCropSoil(SiteCell cell, double minimum, IReadOnlySet<Cell> blocked)
    {
        var wk = cell.Walkable.TryGetValue(out var walk);
        var zk = cell.Zone.TryGetValue(out var zone);
        var rk = cell.Roofed.TryGetValue(out var roof);
        var fk = cell.Fertility.TryGetValue(out var soil);
        return (soil, !blocked.Contains(cell.Cell) && wk && walk && !cell.Occupied() && zk && !zone && rk && !roof && fk && FoodNumber(soil) && soil >= minimum);
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
    /// harvest when the season is unknown or shorter than a cycle).
    /// </summary>
    public static Fact<int> FieldTarget(Fact<long> colonists, CropChoice crop, double reserve, Fact<double> season)
    {
        var ck = colonists.TryGetValue(out var count);
        var dk = crop.Demand.TryGetValue(out var demand);
        var gk = crop.GrowDays.TryGetValue(out var days);
        var yk = crop.HarvestNutrition.TryGetValue(out var yield);
        if (!ck || count < 0 || !dk || !gk || !yk || !FieldPositive(demand) || !FieldPositive(days) || !FieldPositive(yield) || !FieldPositive(reserve))
        {
            return Unknown<int>();
        }
        var harvests = 1.0;
        if (season.TryGetValue(out var left) && FieldPositive(left))
        {
            harvests = Math.Max(1, Math.Floor(left / days));
        }
        var target = Math.Max(count * 10.0, Math.Ceiling(demand * (days + (reserve / harvests)) / yield));
        if (!FieldPositive(target) || target > 65536)
        {
            return Unknown<int>();
        }
        return Known((int)target);
    }
}
