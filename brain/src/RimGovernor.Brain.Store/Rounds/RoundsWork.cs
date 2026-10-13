using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>One current-world, non-retired plan and the need (or player project) it serves (Go <c>roundsPlan</c>).</summary>
internal sealed record RoundsPlan(PlanState State, ConcernId Concern);

public sealed partial class Store
{
    /// <summary>
    /// Maps every plan of the current world to its goal: routine plans to their bound need, player submissions to
    /// a per-plan project id. Plans of another world, and plans with neither a method nor a submission, are
    /// skipped (Go <c>roundsPlans</c>).
    /// </summary>
    internal static List<RoundsPlan> RoundsPlans(StoreTransaction tx, GenerationSnapshot current)
    {
        var result = new List<RoundsPlan>();
        var here = new World(current.Colony, current.Load, current.Map);
        foreach (var plan in LoadPlansIn(tx))
        {
            ConcernId concern;
            World world;
            var (method, found) = tx.QueryFirst("SELECT kind,owner_id FROM plan_methods WHERE plan_id=$plan", [("$plan", plan.Spec.Id)], r => (Kind: r.GetString(0), Owner: r.GetString(1)));
            if (found && string.Equals(method.Kind, "incident", StringComparison.Ordinal))
            {
                // An incident's method serves its Response kind.
                var i = LoadIncident(tx, new IncidentId(method.Owner));
                concern = i.Incident.Kind;
                world = new World(i.Incident.Snapshot.Colony, i.Incident.Snapshot.Load, i.Incident.Snapshot.Map);
            }
            else if (found)
            {
                var g = LoadStandard(tx, new StandardId(method.Owner));
                concern = g.Standard.Kind;
                world = new World(g.Standard.Snapshot.Colony, g.Standard.Snapshot.Load, g.Standard.Snapshot.Map);
            }
            else
            {
                var (row, submitted) = tx.QueryFirst(
                    "SELECT colony,load_token,map_id FROM submissions WHERE plan_id=$plan",
                    [("$plan", plan.Spec.Id)],
                    r => new World(new ColonyId(r.GetString(0)), new LoadId(r.GetString(1)), new MapId(r.GetInt32(2)))
                );
                if (!submitted)
                {
                    continue;
                }
                world = row;
                concern = new ConcernId("player-project-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.Spec.Id.Value))).ToLowerInvariant());
            }
            if (world != here)
            {
                continue;
            }
            result.Add(new RoundsPlan(plan, concern));
        }
        return result;
    }

    /// <summary>
    /// The shadow ready-work projection of the review's plans: diagnostics only, read by no admission. Stage
    /// inputs (bill ingredients, crop readiness) are not observed here yet, so staged work reads
    /// awaiting_observation rather than ready (Go <c>readyWorkOf</c>).
    /// </summary>
    internal static ReadyWorkReport ReadyWorkOf(RoundsRequest r, IReadOnlyList<RoundsPlan> plans, IReadOnlyList<ConcernId> unserved) =>
        PolicyFunctions.ProjectReadyWork(
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

    /// <summary>
    /// Refuses work from a stale or disabled review, independently of pawn availability. Safety is enforced by the
    /// shared Safeguards (Go <c>admitRoundsReview</c>).
    /// </summary>
    internal static void AdmitRoundsReview(StoreTransaction tx, OwnerSummary g)
    {
        var review = LoadRounds(tx);
        var bound = false;
        foreach (var b in review.Standards)
        {
            bound = bound || string.Equals(b.Standard.Value, g.Id, StringComparison.Ordinal);
        }
        if (!bound)
        {
            return;
        }
        if (!review.Enabled || review.Snapshot != g.Snapshot)
        {
            throw new NotAdmittedException($"standard {g.Id} has no current enabled review");
        }
    }

    /// <summary>
    /// The ready-work report of this review and the dependency records that survive it (Go <c>reviewWork</c>).
    /// <paramref name="states"/> holds each assessment's row in assessment order.
    /// </summary>
    internal static (ReadyWorkReport Ready, EquatableArray<DependencyRecord> Dependencies) ReviewWork(
        StoreTransaction tx,
        RoundsRequest r,
        RoundsFindings needs,
        IReadOnlyList<StandardState> states,
        EquatableArray<DependencyRecord> records
    )
    {
        var bindings = new List<RoundsStandard>();
        var unserved = new List<ConcernId>();
        for (var i = 0; i < needs.Assessments.Count; i++)
        {
            var n = needs.Assessments[i];
            bindings.Add(new RoundsStandard(n.Id, states[i].Standard.Id));
            if (!states[i].OwnerActive || PolicyFunctions.ConcernLabor(n.Id).Count == 0)
            {
                continue;
            }
            if (((IMethodOwner)states[i]).ServedCount(tx) == 0)
            {
                unserved.Add(n.Id);
            }
        }
        var plans = RoundsPlans(tx, r.Current);
        var (kept, _) = RoundsDependencies(tx, records, bindings, states, r.Tick);
        return (ReadyWorkOf(r, plans, unserved), EquatableArray.From(kept));
    }
}
