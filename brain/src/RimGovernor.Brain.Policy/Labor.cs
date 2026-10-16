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
            MaintainHousing,
            MaintainRoutes, EnsureMechCharger, MaintainGeneBank);

        // A sale batch at any crafting bench: sculpture, apparel, weapon.
        Set([WorkType.Art, WorkType.Tailoring, WorkType.Smithing, WorkType.Crafting], MaintainTrade);

        Set([WorkType.Hauling], ManagePollution);

        // A resized or retargeted stockpile is refilled by haulers.
        Set([WorkType.Hauling], MaintainStockpiles);
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
