namespace RimGovernor.Brain.Policy;

// Go: tree_plantation.go. Tree plantations: a standing wood demand sows a rectangular growing zone of one tree
// species, one tree per TreeCellsPerTree cells (the native lattice). Trees are always felled early, at the first
// harvestable growth (the species' harvestMinGrowth), when a tree yields harvestYield * 0.5 wood
// (Plant.YieldNow at growth == harvestMinGrowth). Species rank by wood per cell-day of land at lattice density.

public static partial class PolicyFunctions
{
    /// <summary>Whether crop is a tree plantation species: a plant whose harvest fells it and whose neighbours the native sower keeps clear.</summary>
    public static bool IsTreeCrop(CropChoice crop)
    {
        var bk = crop.BlockAdjacentSow.TryGetValue(out var block);
        var dk = crop.HarvestDestroys.TryGetValue(out var destroys);
        return bk && dk && block && destroys;
    }

    // The fell fraction of crop's growth (its harvestMinGrowth), the wood one tree gives then, and the wood per
    // cell-day of land it earns. False when a crop fact is unknown.
    private static (double Fraction, double Wood, double Price, bool Ok) TreePricing(CropChoice crop)
    {
        var gk = crop.GrowDays.TryGetValue(out var days);
        var yk = crop.UnitsPerCell.TryGetValue(out var yield);
        var mk = crop.HarvestMinGrowth.TryGetValue(out var minGrowth);
        var sk = crop.SowWork.TryGetValue(out var sow);
        var fk = crop.HarvestWork.TryGetValue(out var fell);
        if (!gk || !yk || !mk || !sk || !fk || !FieldPositive(days) || !FieldPositive(yield) || !(minGrowth > 0 && minGrowth < 1) || sow < 0 || fell < 0)
        {
            return (0, 0, 0, false);
        }
        var wood = yield * 0.5;
        return (minGrowth, wood, wood / (DomainConstants.TreeCellsPerTree * days * minGrowth), true);
    }

    // Why crop cannot be sown as a plantation here, empty when it can: not researched, no colonist at the sow
    // skill (the best Plants level among those who sow), or wild-only and not native to the biome.
    private static string TreeSowable(CropChoice crop, Fact<string> biome, Fact<int> skill)
    {
        var reason = CropSowable(crop, skill);
        if (reason.Length != 0)
        {
            return reason;
        }
        if (!crop.MustBeWildToSow.TryGetValue(out var wild) || wild)
        {
            var bk = biome.TryGetValue(out var name);
            var nk = crop.WildBiomes.TryGetValue(out var native);
            if (!bk || !nk || !native!.Contains(name!))
            {
                return "not native to the biome";
            }
        }
        return "";
    }

    // The availability and sow-skill gate of any resource crop.
    private static string CropSowable(CropChoice crop, Fact<int> skill)
    {
        if (!crop.Available.TryGetValue(out var available) || !available)
        {
            return "crop not available";
        }
        if (crop.SowMinSkill.TryGetValue(out var need) && need > 0)
        {
            if (!skill.TryGetValue(out var have) || have < need)
            {
                return "no colonist meets the sow skill";
            }
        }
        return "";
    }

    /// <summary>
    /// Whether the colony can grow resource: some crop harvests it and can be sown now (a tree species for wood),
    /// the condition under which the supply plan can open a field candidate with a rate for it. A resource nothing
    /// sows is a finite stock: wild trees and chemfuel run out.
    /// </summary>
    public static bool ResourceGrowable(IReadOnlyList<CropChoice> choices, Resource resource, Fact<string> biome, Fact<int> skill)
    {
        foreach (var crop in choices)
        {
            if (!crop.Harvests.TryGetValue(out var harvests) || harvests != resource)
            {
                continue;
            }
            var reason = IsTreeCrop(crop) ? TreeSowable(crop, biome, skill) : CropSowable(crop, skill);
            if (reason.Length == 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The colony-wide growth fraction at which plantation trees are felled: the harvestMinGrowth of the
    /// best-priced tree species harvesting resource that can be sown now. False when no species can, so nothing
    /// gates the chop. It is a pure function of the catalog and the colony, recomputed each review.
    /// </summary>
    public static (double Fraction, bool Ok) TreeFellFraction(IReadOnlyList<CropChoice> choices, Resource resource, Fact<string> biome, Fact<int> skill)
    {
        var best = 0.0;
        var fraction = 0.0;
        foreach (var crop in choices)
        {
            if (!crop.Harvests.TryGetValue(out var harvests) || harvests != resource || !IsTreeCrop(crop) || TreeSowable(crop, biome, skill).Length != 0)
            {
                continue;
            }
            var (f, _, price, ok) = TreePricing(crop);
            if (ok && price > best)
            {
                best = price;
                fraction = f;
            }
        }
        return (fraction, best > 0);
    }

    /// <summary>The best Plants level among the colonists who sow: available, able to do growing work. Unknown while any pawn's availability, work or skills were not read.</summary>
    public static Fact<int> GrowerSkill(Fact<EquatableArray<WorkPawn>> fact)
    {
        if (!fact.TryGetValue(out var pawns))
        {
            return Unknown<int>();
        }
        var best = 0;
        foreach (var pawn in pawns)
        {
            var ak = pawn.Available.TryGetValue(out var available);
            var wk = pawn.Work.TryGetValue(out var work);
            var sk = pawn.Skills.TryGetValue(out var skills);
            if (!ak || (available && (!wk || !sk)))
            {
                return Unknown<int>();
            }
            if (!available)
            {
                continue;
            }
            var profile = BuildProfile(pawn);
            foreach (var row in work!)
            {
                if (row.Work != WorkType.Growing || row.Disabled || !profile.Capable(row.Work, 0))
                {
                    continue;
                }
                foreach (var skill in skills!)
                {
                    if (skill.Name == "Plants" && !skill.Disabled)
                    {
                        best = Math.Max(best, skill.Level);
                    }
                }
            }
        }
        return Known(best);
    }
}
