using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using DomainCell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Brain.GameAccess;

/// <summary>One inclusive cell rectangle, the unit both defense reads and their callers' tiling work in.</summary>
public readonly record struct CellRect(DomainCell Min, DomainCell Max)
{
    /// <summary>The inclusive cell count, zero for an inverted rectangle.</summary>
    public long Cells => Max.X < Min.X || Max.Z < Min.Z ? 0 : (long)(Max.X - Min.X + 1) * (Max.Z - Min.Z + 1);

    /// <summary>Whether <paramref name="region"/> is a non-inverted rectangle on the defense map.</summary>
    public static bool IsValidDefenseRegion(CellRect region) =>
        region.Min.X >= 0
        && region.Min.Z >= 0
        && region.Max.X >= region.Min.X
        && region.Max.Z >= region.Min.Z
        && region.Max.X < DefenseSiteRules.MaxDefenseSiteExtent
        && region.Max.Z < DefenseSiteRules.MaxDefenseSiteExtent;
}

/// <summary>The defense site census's functions.</summary>
public static class DefenseSiteRules
{
    /// <summary>Bounds a defense-layout cell coordinate.</summary>
    internal const int MaxDefenseSiteExtent = 4096;

    /// <summary>The native designation def a cover's kind takes, the one a CoverClearance action carries.</summary>
    public static CoverClearanceDesignation Designation(this DefenseCover cover)
    {
        switch (cover.Kind)
        {
            case CoverKind.Plant:
                return CoverClearanceDesignation.CutPlant;
            case CoverKind.Chunk:
                return CoverClearanceDesignation.Haul;
            case CoverKind.Mineable:
                return CoverClearanceDesignation.Mine;
            case CoverKind.Building:
                return CoverClearanceDesignation.Deconstruct;
        }
        throw new ArgumentOutOfRangeException(nameof(cover), cover.Kind, "unhandled cover kind");
    }
}
