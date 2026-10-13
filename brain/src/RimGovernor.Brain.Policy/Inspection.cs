namespace RimGovernor.Brain.Policy;

/// <summary>
/// Names the observation facts native reports changed together (ObservationInvalidated): the unit an
/// inspection declares as an input.
/// </summary>
public enum FactFamily
{
    Definitions,
    World,
    Identity,
    Colony,
    Pawns,
    Emergency,
    Rooms,
    Research,
}

/// <summary>
/// Checks one Concern: its type, its department, the fact families its check reads and the check itself.
/// The registry is the only place a Concern is classified.
/// </summary>
/// <param name="Concern">The Concern checked.</param>
/// <param name="Type">The Concern's type.</param>
/// <param name="Department">The Concern's department.</param>
/// <param name="Inputs">The fact families the check reads; a config-only Concern declares an explicit empty list.</param>
/// <param name="Inspect">
/// Appends the Concern's assessment, and its row when owed, to the run. DetectRounds runs the inspections in
/// registry order, so the order below is the order of RoundsFindings.Assessments (the stored bindings are
/// index-aligned); an inspection may read what the shared reviews and every earlier inspection left on the
/// run, and RecoverDisasterServices must stay last because it promotes everything filed before it.
/// </param>
public sealed record Inspection(ConcernId Concern, ConcernType Type, Department Department, IReadOnlyList<FactFamily> Inputs, Action<RoundsRun> Inspect);

internal static class InspectionRegistry
{
    /// <summary>
    /// Lists every inspection in evaluation order. Inputs name the fact families the inspection's body
    /// reads; a Concern that reads only configuration declares an explicit empty list.
    /// </summary>
    public static readonly IReadOnlyList<Inspection> Inspections =
    [
        new(AnswerDialog, ConcernType.IncidentConcern, Department.System, [FactFamily.Emergency], InspectAnswerDialog),
        new(ActiveCombat, ConcernType.IncidentConcern, Department.Military, [FactFamily.Emergency, FactFamily.Colony], InspectActiveCombat),
        new(CriticalMedicine, ConcernType.IncidentConcern, Department.Medical, [FactFamily.Pawns, FactFamily.Emergency], InspectCriticalMedicine),
        new(RestoreWorkers, ConcernType.IncidentConcern, Department.People, [FactFamily.Emergency, FactFamily.Pawns], InspectRestoreWorkers),
        new(ManageSupplySafety, ConcernType.StandardConcern, Department.Storage, [FactFamily.Emergency, FactFamily.Colony], InspectManageSupplySafety),
        new(EnsureWorkAssignments, ConcernType.OnceConcern, Department.People, [FactFamily.Pawns, FactFamily.Identity, FactFamily.Emergency], InspectWorkAssignments),
        new(EnsureFoodSupply, ConcernType.StandardConcern, Department.Food, [FactFamily.Colony, FactFamily.Pawns, FactFamily.Rooms], InspectFoodSupply),
        new(MaintainHousing, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Colony, FactFamily.Rooms, FactFamily.Pawns], InspectHousing),
        new(EnsureTemperatureSafety, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Colony, FactFamily.Rooms, FactFamily.World], InspectTemperatureSafety),
        new(EnsureCooking, ConcernType.OnceConcern, Department.Food, [FactFamily.Colony], InspectCooking),
        new(EnsureBasicPower, ConcernType.OnceConcern, Department.Industry, [FactFamily.Colony, FactFamily.World], InspectBasicPower),
        new(EnsureBasicDefense, ConcernType.StandardConcern, Department.Military, [FactFamily.Pawns, FactFamily.Emergency], InspectBasicDefense),
        new(EnsureComfort, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Colony, FactFamily.Rooms], InspectComfort),
        new(ClearPests, ConcernType.StandardConcern, Department.Military, [FactFamily.Colony, FactFamily.World], InspectPests),
        new(MaintainEquipment, ConcernType.StandardConcern, Department.Military, [FactFamily.Pawns, FactFamily.Colony], InspectEquipment),
        new(MaintainTraining, ConcernType.StandardConcern, Department.Military, [FactFamily.Pawns, FactFamily.Rooms, FactFamily.Colony], InspectTraining),
        new(EnsureResearch, ConcernType.OnceConcern, Department.Industry, [FactFamily.Research, FactFamily.Pawns, FactFamily.Colony], InspectResearch),
        new(MaintainResource, ConcernType.StandardConcern, Department.Industry, [FactFamily.Colony, FactFamily.Definitions, FactFamily.Research, FactFamily.Pawns], InspectResource),
        new(TradeWithCaravan, ConcernType.IncidentConcern, Department.Industry, [FactFamily.World, FactFamily.Colony, FactFamily.Definitions, FactFamily.Pawns], InspectTrade),
        new(EnsureDefensiveLayout, ConcernType.OnceConcern, Department.Military, [], InspectDefensiveLayout),
        new(MaintainFireSafety, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Colony, FactFamily.Emergency], UpkeepInspection(MaintainFireSafety, HoldFireSafety)),
        new(MaintainEssentialRepairs, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Colony], UpkeepInspection(MaintainEssentialRepairs, null)),
        new(ClearHomeObstructions, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Colony], UpkeepInspection(ClearHomeObstructions, HoldClearance)),
        new(ClearAncientShrine, ConcernType.OnceConcern, Department.Military, [FactFamily.Colony, FactFamily.World], UpkeepInspection(ClearAncientShrine, HoldShrine)),
        new(MaintainHomeCoverage, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Colony, FactFamily.World], InspectHomeCoverage),
        new(MaintainStoneShell, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Colony, FactFamily.Research], InspectStoneShell),
        new(MaintainMedicalReserves, ConcernType.StandardConcern, Department.Medical, [FactFamily.Pawns, FactFamily.Colony], InspectMedicalReserves),
        new(MaintainSurgery, ConcernType.StandardConcern, Department.Medical, [FactFamily.Pawns, FactFamily.Colony, FactFamily.Definitions], InspectSurgery),
        new(MaintainBabyFeeding, ConcernType.StandardConcern, Department.Food, [FactFamily.Pawns, FactFamily.Colony], InspectBabyFeeding),
        new(MaintainFoodStorage, ConcernType.StandardConcern, Department.Food, [FactFamily.Colony], InspectFoodStorage),
        new(MaintainRefrigeration, ConcernType.StandardConcern, Department.Food, [FactFamily.Rooms, FactFamily.Colony], InspectRefrigeration),
        new(MaintainLighting, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Rooms, FactFamily.Colony, FactFamily.World], InspectLighting),
        new(MaintainClimateEnvelope, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Rooms, FactFamily.Colony], InspectClimateEnvelope),
        new(MaintainFlooring, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Rooms, FactFamily.Colony], InspectFlooring),
        new(MaintainRoutes, ConcernType.StandardConcern, Department.Shelter, [FactFamily.World, FactFamily.Colony], InspectRoutes),
        new(MaintainArt, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Rooms, FactFamily.Colony, FactFamily.Pawns], InspectArt),
        new(MaintainTrade, ConcernType.StandardConcern, Department.Industry, [FactFamily.Colony, FactFamily.Pawns, FactFamily.Definitions, FactFamily.World], InspectExport),
        new(MaintainShelter, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Rooms, FactFamily.World, FactFamily.Emergency], InspectShelter),
        new(MaintainFirebreak, ConcernType.StandardConcern, Department.Shelter, [FactFamily.Colony, FactFamily.World], InspectFirebreak),
        new(MaintainWorkLedger, ConcernType.StandardConcern, Department.Industry, [FactFamily.Colony], InspectWorkLedger),
        new(MaintainMechs, ConcernType.StandardConcern, Department.Industry, [FactFamily.Colony, FactFamily.Pawns], InspectMechs),
        new(MaintainPsylink, ConcernType.StandardConcern, Department.People, [FactFamily.Pawns, FactFamily.Colony], InspectPsylink),
        new(MaintainIdeoRoles, ConcernType.StandardConcern, Department.People, [FactFamily.Pawns, FactFamily.Colony], InspectIdeoRoles),
        new(ImproveIdeoligion, ConcernType.StandardConcern, Department.People, [FactFamily.Pawns, FactFamily.Colony, FactFamily.Definitions, FactFamily.Emergency], InspectIdeoligion),
        new(MaintainRituals, ConcernType.StandardConcern, Department.People, [FactFamily.Pawns, FactFamily.Colony], InspectRituals),
        new(HoldGatherings, ConcernType.StandardConcern, Department.People, [FactFamily.Pawns, FactFamily.Colony, FactFamily.Emergency], InspectGathering),
        new(MaintainPermits, ConcernType.StandardConcern, Department.People, [FactFamily.Pawns, FactFamily.Definitions, FactFamily.Colony], InspectPermits),
        new(ManageCreepJoiners, ConcernType.StandardConcern, Department.People, [FactFamily.Pawns, FactFamily.Emergency], InspectCreepJoiners),
        new(MaintainHerd, ConcernType.StandardConcern, Department.People, [FactFamily.Pawns, FactFamily.Colony, FactFamily.Rooms], InspectHerd),
        new(MaintainPopulation, ConcernType.StandardConcern, Department.People, [FactFamily.Pawns, FactFamily.Colony, FactFamily.World], InspectPopulation),
        new(MaintainAnimalContainment, ConcernType.StandardConcern, Department.People, [FactFamily.Colony, FactFamily.Pawns, FactFamily.Rooms], InspectAnimalContainment),
        new(MaintainBurial, ConcernType.StandardConcern, Department.People, [FactFamily.Colony], InspectBurial),
        new(MaintainIncineration, ConcernType.StandardConcern, Department.Sanitation, [FactFamily.Colony], InspectIncineration),
        new(RemoveBlight, ConcernType.StandardConcern, Department.Food, [FactFamily.Colony], InspectBlight),
        new(ManagePollution, ConcernType.StandardConcern, Department.Sanitation, [FactFamily.Colony, FactFamily.World], InspectPollution),
        new(EnsureMechCharger, ConcernType.StandardConcern, Department.Industry, [FactFamily.Colony, FactFamily.Pawns], InspectMechCharger),
        new(MaintainGeneBank, ConcernType.StandardConcern, Department.Medical, [FactFamily.Colony], InspectGeneBank),
        new(MaintainStockpiles, ConcernType.StandardConcern, Department.Storage, [FactFamily.Colony], InspectStockpiles),
        new(EnsureMood, ConcernType.IncidentConcern, Department.People, [FactFamily.Pawns], InspectMood),
        new(RecoverDisasterServices, ConcernType.IncidentConcern, Department.Shelter, [FactFamily.Emergency, FactFamily.Colony, FactFamily.World], InspectDisaster),
    ];

    /// <summary>Maps each registered ConcernId to its inspection.</summary>
    public static readonly IReadOnlyDictionary<ConcernId, Inspection> Index = Inspections.ToDictionary(d => d.Concern);
}

public static partial class PolicyFunctions
{
    /// <summary>Lists every family, in wire order.</summary>
    public static IReadOnlyList<FactFamily> FactFamilies() =>
        [FactFamily.Definitions, FactFamily.World, FactFamily.Identity, FactFamily.Colony, FactFamily.Pawns, FactFamily.Emergency, FactFamily.Rooms, FactFamily.Research];

    /// <summary>Lists every registered inspection.</summary>
    public static IReadOnlyList<Inspection> AllInspections() => [.. InspectionRegistry.Inspections];

    /// <summary>Returns id's inspection; an unknown id reports false.</summary>
    public static (Inspection? Inspection, bool Ok) InspectionFor(ConcernId id) =>
        InspectionRegistry.Index.TryGetValue(id, out var d) ? (d, true) : (null, false);

    /// <summary>
    /// Throws for the first registry fault: a ConcernId in ids without exactly one inspection, an
    /// inspection for no listed ConcernId, or an inspection with no type, no department or an unknown input
    /// family. (Go's nil inspect function and nil input list cannot occur: both are non-null here.)
    /// </summary>
    public static void ValidateInspections(IReadOnlyList<Inspection> inspections, IReadOnlyList<ConcernId> ids)
    {
        var count = new Dictionary<ConcernId, int>();
        var known = new HashSet<FactFamily>(FactFamilies());
        foreach (var d in inspections)
        {
            count[d.Concern] = count.GetValueOrDefault(d.Concern) + 1;
            if (d.Type == ConcernType.UnknownConcern)
            {
                throw new DomainException($"inspection {d.Concern}: no type");
            }
            if (d.Department == Department.Unknown)
            {
                throw new DomainException($"inspection {d.Concern}: no department");
            }
            foreach (var input in d.Inputs)
            {
                if (!known.Contains(input))
                {
                    throw new DomainException($"inspection {d.Concern}: unknown input family \"{Wire.Name(input)}\"");
                }
            }
        }
        foreach (var id in ids)
        {
            var n = count.GetValueOrDefault(id);
            if (n != 1)
            {
                throw new DomainException($"ConcernId {id} has {n} inspections, want 1");
            }
            count.Remove(id);
        }
        foreach (var id in count.Keys.Order())
        {
            throw new DomainException($"inspection {id} matches no Concern");
        }
    }
}
