namespace RimGovernor.Brain.Policy;

/// <summary>
/// The colony area a Concern belongs to, like a Civ advisor. It is a tag for grouping goals in panels only; it
/// never ranks goals or budgets labor. The table matches the department table in
/// docs/developers/architecture/control-loop.md.
/// </summary>
public enum Department
{
    Unknown = 0,
    [WireName("Food")]
    Food,
    [WireName("Shelter")]
    Shelter,
    [WireName("Industry")]
    Industry,
    [WireName("Military")]
    Military,
    [WireName("Medical")]
    Medical,
    [WireName("People")]
    People,
    [WireName("Storage")]
    Storage,
    [WireName("Sanitation")]
    Sanitation,

    /// <summary>Game-plumbing goals; panels do not show it.</summary>
    [WireName("System")]
    System,
}

public static partial class PolicyFunctions
{
    /// <summary>Tags id with its colony area; an unknown id is Department.Unknown.</summary>
    public static Department DepartmentOf(ConcernId id) =>
        InspectionRegistry.Index.TryGetValue(id, out var entry) ? entry.Department : Department.Unknown;

    /// <summary>
    /// The number of stockpile-zone creations one method of each Standard Concern that owns stores may carry.
    /// ClearHomeObstructions keeps its chunk dump until that zone goes, and MaintainStockpiles applies every
    /// declaration of the storage planner in one batch. A concern absent from the table owns no store.
    /// </summary>
    internal static readonly IReadOnlyDictionary<ConcernId, int> StockpileZoneLimits = new Dictionary<ConcernId, int>
    {
        [MaintainStockpiles] = 16,
        [ClearHomeObstructions] = 1,

        // Food
        [EnsureFoodSupply] = 1,
        [EnsureCooking] = 1,
        [MaintainBabyFeeding] = 1,
        [MaintainFoodStorage] = 1,
        [MaintainRefrigeration] = 1,
        [RemoveBlight] = 1,

        // Industry
        [EnsureBasicPower] = 1,
        [EnsureResearch] = 1,
        [MaintainResource] = 1,
        [MaintainTrade] = 1,
        [MaintainWorkLedger] = 1,
        [MaintainMechs] = 1,
        [EnsureMechCharger] = 1,

        // Military
        [EnsureBasicDefense] = 1,
        [ClearPests] = 1,
        [MaintainEquipment] = 1,
        [MaintainTraining] = 1,
        [EnsureDefensiveLayout] = 1,
        [ClearAncientShrine] = 1,

        // Medical
        [MaintainMedicalReserves] = 1,
        [MaintainSurgery] = 1,
        [MaintainGeneBank] = 1,

        // People
        [EnsureWorkAssignments] = 1,
        [MaintainPsylink] = 1,
        [MaintainIdeoRoles] = 1,
        [ImproveIdeoligion] = 1,
        [MaintainRituals] = 1,
        [HoldGatherings] = 1,
        [MaintainPermits] = 1,
        [ManageCreepJoiners] = 1,
        [MaintainHerd] = 1,
        [MaintainPopulation] = 1,
        [MaintainAnimalContainment] = 1,
        [MaintainBurial] = 1,
    };

    /// <summary>The number of stockpile-zone creations one method of concern may carry; zero means the concern owns no store.</summary>
    public static int StockpileZoneLimit(ConcernId concern) => StockpileZoneLimits.GetValueOrDefault(concern);
}
