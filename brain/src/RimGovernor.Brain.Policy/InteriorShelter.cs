namespace RimGovernor.Brain.Policy;

// The shelter's slot names and bunk footprint. What the starter room holds is ShelterWants
// (InteriorRegionRoles.cs): the region placer seats it.
public static partial class PolicyConstants
{
    internal const string ShelterCraftingDef = "CraftingSpot";

    /// <summary>Names every bunk slot ("bunk.1", "bunk.2", ...).</summary>
    internal const string ShelterBunkSlotPrefix = "bunk.";

    /// <summary>Names the passive cooler slots ("cooler.1", ...), a hot map's floor slot for the temperature planner.</summary>
    public const string ShelterCoolerSlotPrefix = "cooler.";

    /// <summary>A bunk's North footprint: the 1x2 of a SleepingSpot, a Bed and a Bedroll.</summary>
    internal static readonly Cell ShelterBunkSize = new(1, 2);
}

public static partial class PolicyFunctions
{
    /// <summary>Reports whether the piece is one of the shelter's bunk slots.</summary>
    public static bool IsBunk(this InteriorPiece p) => p.Slot.StartsWith(ShelterBunkSlotPrefix, StringComparison.Ordinal) && p.Slot.Length > ShelterBunkSlotPrefix.Length;
}
