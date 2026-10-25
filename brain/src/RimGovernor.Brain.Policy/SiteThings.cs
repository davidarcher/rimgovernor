namespace RimGovernor.Brain.Policy;

// The thing records themselves (Thing, ThingMarks and the rest) are declared in the contracts assembly, beside the
// cell grid that carries them.
public static partial class SiteRules
{
    private const ThingMarks OccupyingFlags = ThingMarks.Edifice | ThingMarks.Blueprint | ThingMarks.Frame;

    internal static Thing? EdificeOf(this SiteCell c)
    {
        foreach (var t in c.Things)
        {
            if (t.Has(ThingMarks.Edifice))
            {
                return t;
            }
        }
        return null;
    }

    /// <summary>Reports a building, blueprint or frame on the cell: the native CellOccupied. Plants, items and filth never occupy a cell.</summary>
    public static bool Occupied(this SiteCell c)
    {
        foreach (var t in c.Things)
        {
            if ((t.Flags & OccupyingFlags) != 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Reports the cell's edifice is natural rock: a planned ring on it keeps it as wall; inside the room plan dig mines it.</summary>
    public static bool NaturalRock(this SiteCell c) => c.EdificeOf() is { } t && t.Has(ThingMarks.NaturalRock);

    /// <summary>Names the definition of the player-owned edifice on the cell, empty for none: a ring of that wall kind stands on it.</summary>
    public static string PlayerEdifice(this SiteCell c) =>
        c.EdificeOf() is { } t && t.Faction == ThingFaction.Player ? t.Def : "";

    /// <summary>Reports an unowned edifice the player may deconstruct, outside any ancient danger.</summary>
    public static bool Ruin(this SiteCell c) =>
        c.EdificeOf() is { } t && t.Faction != ThingFaction.Player && t.Has(ThingMarks.Deconstructible) && !t.Has(ThingMarks.AncientDanger);

    /// <summary>Names the definition of a ruin the player may claim, empty for none: a ring of that wall kind claims it and keeps it as wall instead of clearing it.</summary>
    public static string ClaimableRuin(this SiteCell c) =>
        c.EdificeOf() is { } t && c.Ruin() && t.Has(ThingMarks.Claimable) ? t.Def : "";

    /// <summary>
    /// The hold on the unowned building covering the cell that a claim honours, empty for none:
    /// "ancient_danger", or "casket" for a casket still holding contents. A ring claims a ruin only where the
    /// claim would act on it.
    /// </summary>
    public static string RuinHold(this SiteCell c)
    {
        var hold = "";
        foreach (var t in c.Things)
        {
            if (t.Category != ThingCategory.Building || t.Faction == ThingFaction.Player || t.Building is null
                || t.Has(ThingMarks.Blueprint) || t.Has(ThingMarks.Frame))
            {
                continue;
            }
            if (t.Has(ThingMarks.AncientDanger))
            {
                return "ancient_danger";
            }
            if (t.Building.Casket.Count > 0)
            {
                hold = "casket";
            }
        }
        return hold;
    }
}
