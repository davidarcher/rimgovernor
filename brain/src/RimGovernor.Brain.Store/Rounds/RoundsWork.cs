using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>One current-world, non-retired plan and the need it serves.</summary>
internal sealed record RoundsPlan(PlanState State, ConcernId Concern);

public sealed partial class Store
{
    /// <summary>
    /// Maps every plan of the current world to its bound need. Plans of another world, and plans with no method, are
    /// skipped.
    /// </summary>
    internal static List<RoundsPlan> RoundsPlans(StoreTransaction tx, GenerationSnapshot current)
    {
        var result = new List<RoundsPlan>();
        var here = new World(current.Colony, current.Load, current.Map);
        foreach (var plan in LoadPlansIn(tx))
        {
            var (need, found) = tx.QueryFirst(
                "SELECT need_id,colony,load_token,map_id FROM need_methods WHERE plan_id=$plan",
                [("$plan", plan.Spec.Id)],
                r => (Need: r.GetString(0), World: new World(new ColonyId(r.GetString(1)), new LoadId(r.GetString(2)), new MapId(r.GetInt32(3))))
            );
            if (!found || need.World != here)
            {
                continue;
            }
            // A Need's method serves its NeedId, filed as the concern key (#3250).
            result.Add(new RoundsPlan(plan, new NeedId(need.Need).FilingKey));
        }
        return result;
    }

    /// <summary>
    /// The shadow ready-work projection of the review's plans: diagnostics only, read by no admission. Stage
    /// inputs (bill ingredients, crop readiness) are not observed here yet, so staged work reads
    /// stage_inputs_unobserved (awaiting) rather than ready.
    /// </summary>
    internal static ReadyWorkReport ReadyWorkOf(RoundsRequest r, IReadOnlyList<RoundsPlan> plans, IReadOnlyList<ConcernId> unserved) =>
        ReadyWorkRules.ProjectReadyWork(
            new ReadyRequest
            {
                Snapshot = r.Current,
                Tick = r.Tick,
                Plans = [.. plans.Select(p => new ReadyPlan { Concern = p.Concern, Spec = p.State.Spec, Progress = p.State.Progress })],
                Unserved = unserved,
                Construction = r.Facts.CurrentConstruction,
                Recipes = r.Facts.Recipes,
            }
        );
}
