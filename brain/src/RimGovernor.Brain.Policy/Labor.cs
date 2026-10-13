namespace RimGovernor.Brain.Policy;

// Native work types routine goals dispatch pawn work through. Names are RimWorld WorkTypeDef defNames as
// ListPawns reports them. The WorkType members this file's Go consts name (Construction, Research, Mining,
// PlantCutting, Crafting, Hauling, Cleaning, Basic = BasicWorker, Handling, Cooking, Firefighter, Tailoring,
// Smithing) belong to the WorkType enum.
public static partial class PolicyFunctions
{
    private static readonly Lazy<IReadOnlyDictionary<ConcernId, IReadOnlyList<WorkType>>> ConcernLaborTable = new(() =>
    {
        var table = new Dictionary<ConcernId, IReadOnlyList<WorkType>>();
        void Set(IReadOnlyList<WorkType> profile, params ConcernId[] ids)
        {
            foreach (var id in ids)
            {
                table[id] = profile;
            }
        }
        Set(
            [WorkType.Construction],
            ClearHomeObstructions, ClearAncientShrine, EnsureBasicDefense, EnsureComfort, MaintainHousing, MaintainEssentialRepairs, MaintainStoneShell,
            MaintainFoodStorage, MaintainHomeCoverage, MaintainAnimalContainment, MaintainLighting, MaintainFlooring, MaintainRoutes, EnsureMechCharger, MaintainGeneBank);
        Set([WorkType.PlantCutting], RemoveBlight);
        Set([WorkType.Art], MaintainArt);

        // A sale batch at any crafting bench: sculpture, apparel, weapon.
        Set([WorkType.Art, WorkType.Tailoring, WorkType.Smithing, WorkType.Crafting], MaintainTrade);
        Set([WorkType.Research], EnsureResearch);
        Set([WorkType.Mining, WorkType.PlantCutting, WorkType.Crafting], MaintainResource);

        // A wear order is a forced job any pawn carries; the replacement bill needs the bench's work type.
        // Construction keeps the workshop rung available before a bench exists to request its crafting work.
        Set([WorkType.Construction, WorkType.Tailoring, WorkType.Smithing, WorkType.Crafting], MaintainEquipment);
        Set([WorkType.Hauling], MaintainIncineration, ManagePollution);

        // The shell, sarcophagus and graves are built; vanilla haulers carry the corpses.
        Set([WorkType.Construction], MaintainBurial);

        // The range shell and its stands are built; the native training job (#2610) spends the trainees' own hours.
        Set([WorkType.Construction], MaintainTraining);
        Set([WorkType.Handling], MaintainHerd);
        Set([WorkType.Cooking], MaintainBabyFeeding);
        Set([WorkType.Firefighter], MaintainFireSafety);

        // Vents are built over standing walls and roofs are designated for builders.
        Set([WorkType.Construction], MaintainClimateEnvelope);

        // Plants are cut by plant cutters, wooden ruins taken down by builders.
        Set([WorkType.PlantCutting, WorkType.Construction], MaintainFirebreak);

        // A resized or retargeted stockpile is refilled by haulers.
        Set([WorkType.Hauling], MaintainStockpiles);

        // A medicine bill at a crafting bench, or a wild healroot harvest.
        Set([WorkType.Crafting, WorkType.PlantCutting], MaintainMedicalReserves);
        return table;
    });

    /// <summary>
    /// Lists the native work types a goal's methods can put pawns to: a goal is admissible when any listed type
    /// still has free labor. An empty profile means the goal issues no pawn work and is bounded only by the
    /// coarse worker count. Concerns not listed (emergencies, monitoring-only needs, configuration pushes) have
    /// no profile.
    /// </summary>
    public static IReadOnlyList<WorkType> ConcernLabor(ConcernId id) => ConcernLaborTable.Value.GetValueOrDefault(id) ?? [];

    /// <summary>
    /// Counts, per native work type, the pawns RoundsWorkers counts whose work settings enable that type
    /// (priority above zero and not disabled). Any counted pawn with unknown or empty work settings makes the
    /// whole census unknown: native lists every WorkTypeDef for a pawn whose work applies, so an empty list is
    /// an unobserved census, and partial labor evidence must not admit or refuse work.
    /// </summary>
    public static Fact<IReadOnlyDictionary<WorkType, int>> RoundsLabor(IReadOnlyList<WorkPawn> pawns)
    {
        var labor = new Dictionary<WorkType, int>();
        foreach (var p in pawns)
        {
            var known = p.Available.TryGetValue(out var available);
            var appliesKnown = p.Applies.TryGetValue(out var applies);
            if ((known && !available) || (appliesKnown && !applies))
            {
                continue;
            }
            var workKnown = p.Work.TryGetValue(out var work);
            if (!known || !appliesKnown || !workKnown || work!.Count == 0)
            {
                return Unknown<IReadOnlyDictionary<WorkType, int>>();
            }
            foreach (var w in work)
            {
                if (w.Priority > 0 && !w.Disabled)
                {
                    labor[w.Work] = labor.GetValueOrDefault(w.Work) + 1;
                }
            }
        }
        return Known<IReadOnlyDictionary<WorkType, int>>(labor);
    }
}
