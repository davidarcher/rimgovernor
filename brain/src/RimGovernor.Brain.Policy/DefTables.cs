namespace RimGovernor.Brain.Policy;

/// <summary>The items of the launcher's Rhadamant Sea Ice preset.</summary>
public sealed record RhadamantThingSet(string Silver, string Meal, string Wood, string Steel, string Component, string Medicine, string Rifle, string ShipChunk);

public static class DefTablesRules
{
    /// <summary>
    /// The Core items the launcher's scenario composer suggests for a starting thing. The composer takes any
    /// defName; this list only saves typing for common supplies.
    /// </summary>
    public static readonly IReadOnlyList<string> ScenarioThingChoices =
    [
        "Silver", "Gold", "WoodLog", "Steel", "Plasteel", "Uranium", "ComponentIndustrial", "ComponentSpacer",
        "MealSurvivalPack", "Pemmican", "MedicineHerbal", "MedicineIndustrial", "Gun_BoltActionRifle",
        "ShipChunk",
    ];

    public static readonly RhadamantThingSet RhadamantThings = new(
        "Silver", "MealSurvivalPack", "WoodLog", "Steel", "ComponentIndustrial", "MedicineIndustrial", "Gun_BoltActionRifle", "ShipChunk");

    /// <summary>The one shell def BuildingProperties.IsMortar tests a loadable turret gun's fixed storage filter against (ThingDefOf.Shell_HighExplosive).</summary>
    public static readonly IReadOnlyList<string> MortarProbeShells = ["Shell_HighExplosive"];

    /// <summary>
    /// The items a Beggars quest's request may be answered with. Native gives whatever the lord toil requests,
    /// so which items the colony hands over is policy's choice, not a game rule.
    /// </summary>
    public static readonly IReadOnlyList<string> QuestGiftDefs = ["Beer", "MedicineHerbal", "MedicineIndustrial", "Penoxycyline", "Silver"];

    /// <summary>The ThingDefOf names the game's room-role workers score a furniture role by: a def of the name has the role.</summary>
    public static readonly IReadOnlyDictionary<string, FurnitureRole> GameRoomRoleDefs = new Dictionary<string, FurnitureRole>(StringComparer.Ordinal)
    {
        ["ToyBox"] = FurnitureRole.Toy,
        ["BabyDecoration"] = FurnitureRole.Decoration,
        ["Blackboard"] = FurnitureRole.Board,
        ["SchoolDesk"] = FurnitureRole.Desk,
    };
}
