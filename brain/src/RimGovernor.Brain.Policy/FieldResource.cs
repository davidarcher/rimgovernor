using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// A demand-driven field decision: sow the crop whose harvest is Resource so that Deficit units come in.
/// Edibility is irrelevant; only Harvests and UnitsPerCell say what a crop is for. (Go <c>ResourceFieldRequest</c>.)
/// </summary>
public sealed record ResourceFieldRequest
{
    public Resource Resource { get; init; }

    /// <summary>The units still wanted; unknown or non-positive plans nothing.</summary>
    public Fact<double> Deficit { get; init; }

    public IReadOnlyList<CropChoice> Choices { get; init; } = [];

    public CropClimate Climate { get; init; } = new();

    public FarmSiteRequest Site { get; init; } = new();

    /// <summary>
    /// The best Plants level among the colonists who sow (<c>GrowerSkill</c>); a crop with a sow skill floor is
    /// planned only when it is known and meets it.
    /// </summary>
    public Fact<int> GrowerSkill { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Starts the ID of a resource field candidate: the prefix plus the crop name, so the executor reads the plan it priced by ID.</summary>
    public const string ResourceFieldPrefix = "field:";

    /// <summary>
    /// Go <c>PlanFieldByResource</c>: chooses among the available crops whose Harvests is the demanded resource
    /// and sites patches for the best one. It shares the food planner's viability rules and patch picker, with
    /// cells needed = ceil(deficit / UnitsPerCell). Unlike food it never plants on a guess: unknown season,
    /// darkness or crop facts refuse. Candidates are ranked by yield per day over the needed cells. A tree
    /// species (<c>IsTreeCrop</c>) is planned on the native lattice, one tree per
    /// <see cref="DomainConstants.TreeCellsPerTree"/> cells. Null when no field is planned.
    /// </summary>
    public static FieldPlan? PlanFieldByResource(ResourceFieldRequest r)
    {
        var dk = r.Deficit.TryGetValue(out var deficit);
        var sk = r.Climate.SowingOutdoors().TryGetValue(out var sowing);
        var ck = r.Climate.DaysRemaining.TryGetValue(out var season);
        if (!dk || !FieldPositive(deficit) || !sk || !sowing || !ck || !FieldPositive(season) || r.Resource.IsEmpty)
        {
            return null;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plan = new FieldPlan();
        var candidates = new List<FieldCandidate>();
        var viables = new List<ViableCrop>();
        foreach (var crop in r.Choices)
        {
            if (!seen.Add(crop.Name))
            {
                return null;
            }
            if (crop.Harvests.TryGetValue(out var resource) && resource != r.Resource)
            {
                continue;
            }
            var (v, reason) = ResourceCropViable(crop, deficit, season, r);
            if (reason.Length != 0)
            {
                candidates.Add(new FieldCandidate { Crop = crop, Reason = reason });
                continue;
            }
            viables.Add(v);
        }
        plan = plan with { Candidates = candidates };
        if (viables.Count == 0)
        {
            return null;
        }
        var req = new FieldRequest { Site = r.Site };
        foreach (var v in viables)
        {
            candidates.Add(FieldCandidateFor(req, v, false));
        }
        return RankField(plan with { Candidates = candidates }, false);
    }

    /// <summary>
    /// Go <c>ResourceFieldCandidate</c>: prices a planned resource field as a harvest candidate: the plan's
    /// cells, the crop's grow days and units per cell, the sowing as upfront labor (FieldSowTicksPerCell) and
    /// the harvest work over the grow time as daily labor. Null when the plan has no cells or a crop fact is
    /// unknown; a field is never priced on a guess.
    /// </summary>
    public static SupplyCandidate? ResourceFieldCandidate(Resource resource, FieldPlan plan)
    {
        var dk = plan.Crop.GrowDays.TryGetValue(out var days);
        var uk = plan.Crop.UnitsPerCell.TryGetValue(out var units);
        var wk = plan.Crop.HarvestWork.TryGetValue(out var work);
        var cells = plan.Sites.Cells;
        if (IsTreeCrop(plan.Crop))
        {
            return TreeFieldCandidate(resource, plan);
        }
        if (!dk || !uk || !wk || !FieldPositive(days) || !FieldPositive(units) || work < 0 || cells <= 0)
        {
            return null;
        }
        return FieldHarvestCandidate(
            resource,
            ResourceFieldPrefix + plan.Crop.Name,
            new FieldHarvest
            {
                Cells = cells,
                GrowDays = days,
                UnitsPerCell = units,
                SetupTicks = cells * FieldSowTicksPerCell,
                WorkPerDay = work * cells / days,
            }
        );
    }

    /// <summary>
    /// Prices a planned tree plantation: its trees' sowing as upfront labor, the days to the fell point as lead,
    /// and the felling over those days as daily labor, each tree giving its wood at that point.
    /// </summary>
    private static SupplyCandidate? TreeFieldCandidate(Resource resource, FieldPlan plan)
    {
        var (fraction, wood, _, ok) = TreePricing(plan.Crop);
        var days = plan.Crop.GrowDays.ValueOrDefault;
        var sow = plan.Crop.SowWork.ValueOrDefault;
        var fell = plan.Crop.HarvestWork.ValueOrDefault;
        var trees = plan.Sites.Cells / DomainConstants.TreeCellsPerTree;
        if (!ok || trees <= 0)
        {
            return null;
        }
        return FieldHarvestCandidate(
            resource,
            ResourceFieldPrefix + plan.Crop.Name,
            new FieldHarvest
            {
                Cells = (long)trees * DomainConstants.TreeCellsPerTree,
                GrowDays = days * fraction,
                UnitsPerCell = wood / DomainConstants.TreeCellsPerTree,
                SetupTicks = trees * sow,
                WorkPerDay = fell * trees / (days * fraction),
            }
        );
    }

    /// <summary>
    /// Go <c>StandingFieldYield</c>: the units one standing field of crop on cells usable cells delivers per
    /// harvest toward resource: zero when the crop harvests something else or a fact is unknown. A tree zone
    /// counts its lattice trees at the wood each gives at the fell point.
    /// </summary>
    public static double StandingFieldYield(CropChoice crop, Resource resource, Fact<uint> cells)
    {
        var hk = crop.Harvests.TryGetValue(out var harvests);
        var uk = crop.UnitsPerCell.TryGetValue(out var units);
        var nk = cells.TryGetValue(out var n);
        if (IsTreeCrop(crop) && hk && harvests == resource && nk)
        {
            var (_, wood, _, ok) = TreePricing(crop);
            return ok ? wood * (n / (uint)DomainConstants.TreeCellsPerTree) : 0;
        }
        if (!hk || !uk || !nk || harvests != resource || !FieldPositive(units))
        {
            return 0;
        }
        return units * n;
    }

    /// <summary>Screens one crop that harvests the demanded resource, returning the viable crop or the reason it is excluded.</summary>
    private static (ViableCrop Crop, string Reason) ResourceCropViable(CropChoice crop, double deficit, double season, ResourceFieldRequest r)
    {
        var ak = crop.Available.TryGetValue(out var available);
        var gk = crop.GrowDays.TryGetValue(out var days);
        var uk = crop.UnitsPerCell.TryGetValue(out var units);
        var rk = crop.Harvests.IsKnown;
        if (!rk || !ak || !gk || !uk || !FieldPositive(days) || !FieldPositive(units))
        {
            return (new ViableCrop(), "incomplete crop facts");
        }
        if (!available)
        {
            return (new ViableCrop(), "crop not available");
        }
        if (GrowsInDark(crop))
        {
            return (new ViableCrop(), "crop needs a dark room");
        }
        var sowReason = CropSowable(crop, r.GrowerSkill);
        if (sowReason.Length != 0)
        {
            return (new ViableCrop(), sowReason);
        }
        if (IsTreeCrop(crop))
        {
            return TreeViable(crop, deficit, r);
        }
        if (days * FieldCycles > season)
        {
            return (new ViableCrop(), "season too short");
        }
        var needed = (int)Math.Ceiling((deficit / units) - 1e-9);
        if (needed <= 0)
        {
            return (new ViableCrop(), string.Create(CultureInfo.InvariantCulture, $"no cells needed for {deficit:F2} units"));
        }
        return (new ViableCrop { Crop = crop, Days = days, Needed = needed, Units = units }, "");
    }

    /// <summary>
    /// Sizes a tree plantation for deficit units: whole lattice trees of the wood one gives at the fell point,
    /// <see cref="DomainConstants.TreeCellsPerTree"/> cells each.
    /// </summary>
    private static (ViableCrop Crop, string Reason) TreeViable(CropChoice crop, double deficit, ResourceFieldRequest r)
    {
        var sowReason = TreeSowable(crop, r.Climate.Biome, r.GrowerSkill);
        if (sowReason.Length != 0)
        {
            return (new ViableCrop(), sowReason);
        }
        var (_, wood, price, ok) = TreePricing(crop);
        if (!ok)
        {
            return (new ViableCrop(), "incomplete crop facts");
        }
        var trees = (int)Math.Ceiling((deficit / wood) - 1e-9);
        if (trees <= 0)
        {
            return (new ViableCrop(), string.Create(CultureInfo.InvariantCulture, $"no trees needed for {deficit:F2} units"));
        }
        var days = crop.GrowDays.ValueOrDefault;
        return (
            new ViableCrop
            {
                Crop = crop,
                Days = days,
                Needed = trees * DomainConstants.TreeCellsPerTree,
                Units = wood / DomainConstants.TreeCellsPerTree,
                Price = price,
            },
            ""
        );
    }
}
