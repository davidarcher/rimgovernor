namespace RimGovernor.Brain.Policy;

public static class DefTablesRules
{
    /// <summary>The one shell def BuildingProperties.IsMortar tests a loadable turret gun's fixed storage filter against (ThingDefOf.Shell_HighExplosive).</summary>
    public static readonly IReadOnlyList<string> MortarProbeShells = ["Shell_HighExplosive"];

    /// <summary>
    /// The items a Beggars quest's request may be answered with. Native gives whatever the lord toil requests,
    /// so which items the colony hands over is policy's choice, not a game rule.
    /// </summary>
    public static readonly IReadOnlyList<string> QuestGiftDefs = ["Beer", "MedicineHerbal", "MedicineIndustrial", "Penoxycyline", "Silver"];

    /// <summary>The ThingDefOf names the game's room-role workers score a furniture role by: a def of the name has the role. A toy is the play read's (DefinitionCatalog.PlayBuildings).</summary>
    public static readonly IReadOnlyDictionary<string, FurnitureRole> GameRoomRoleDefs = new Dictionary<string, FurnitureRole>(StringComparer.Ordinal)
    {
        ["Blackboard"] = FurnitureRole.Board,
        ["SchoolDesk"] = FurnitureRole.Desk,
    };
}
