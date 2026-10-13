using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>The upkeep goals whose unresolved effects still count as an issued obligation.</summary>
    private static readonly ConcernId[] UpkeepIssuedConcerns =
    [
        PolicyFunctions.ClearHomeObstructions,
        PolicyFunctions.MaintainFireSafety,
        PolicyFunctions.MaintainEssentialRepairs,
        PolicyFunctions.MaintainMedicalReserves,
        PolicyFunctions.MaintainFoodStorage,
        PolicyFunctions.MaintainAnimalContainment,
        PolicyFunctions.MaintainHousing,
        PolicyFunctions.MaintainHomeCoverage,
        PolicyFunctions.MaintainStoneShell,
    ];

    /// <summary>
    /// The upkeep goals with an issued, still-open obligation in the current world. Only the journal may retain an
    /// issued upkeep obligation: review callers cannot invent a pending order, and retired observed methods need
    /// no fresh hold (Go <c>roundsUpkeepIssued</c>).
    /// </summary>
    internal static Dictionary<ConcernId, bool> RoundsUpkeepIssued(StoreTransaction tx, GenerationSnapshot current)
    {
        var result = new Dictionary<ConcernId, bool>();
        foreach (var plan in LoadPlansIn(tx))
        {
            var issued = false;
            foreach (var p in plan.Progress)
            {
                issued = issued || (p.View.Attempt.Value != 0 && Standard.WorkOpen([p]));
            }
            if (!issued)
            {
                continue;
            }
            var (goal, found) = tx.QueryFirst("SELECT owner_id FROM plan_methods WHERE plan_id=$plan AND kind='standard'", [("$plan", plan.Spec.Id)], r => r.GetString(0));
            if (!found)
            {
                continue;
            }
            var g = LoadStandard(tx, new StandardId(goal!));
            var s = g.Standard.Snapshot;
            if (s.Colony != current.Colony || s.Load != current.Load || s.Map != current.Map)
            {
                continue;
            }
            // Invalidated methods keep their original goal binding while new routine goals replace the current
            // review. Their unresolved effects still count.
            if (Array.IndexOf(UpkeepIssuedConcerns, g.Standard.Kind) >= 0)
            {
                result[g.Standard.Kind] = true;
            }
        }
        return result;
    }
}
