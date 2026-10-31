using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Store.StorePackage;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// Dispatched evidence that settles an action for retirement: a completed effect, a building that ended
    /// unsuccessful, or a building intent native refused (a refusal is terminal, Unsuccessful with an absent effect).
    /// </summary>
    internal static bool SettledRetirementOutcome(Progress progress)
    {
        var w = progress.View;
        if (w.Effect is not EffectState.Observed observed)
        {
            return false;
        }
        var effect = observed.Kind;
        var ended = w.Stage is Stage.Unsuccessful or Stage.Cancelled;
        if (effect == Effect.Completed)
        {
            return w.Stage is Stage.Completed or Stage.Cancelled;
        }
        if (progress.Action.Kind != ActionKind.Building)
        {
            return false;
        }
        return effect == Effect.Unsuccessful ? ended
            : effect == Effect.Absent ? ended && w.Attempt.Value > 0
            : false;
    }

    /// <summary>
    /// The one plan retirement write: <paramref name="plan"/> retires at <paramref name="observed"/>, the observation tick that
    /// ended it (<c>plans.retired_tick</c>, which the schema requires of every retired plan), superseded by <paramref name="next"/>
    /// when given. Admission reads the tick to refuse a justification no newer than a retired purpose.
    /// </summary>
    internal static void RetirePlanIn(StoreTransaction tx, PlanId plan, Tick observed, PlanId? next = null) =>
        _ = next is { } successor
            ? tx.Execute("UPDATE plans SET retired=1,superseded_by=$next,retired_tick=$tick WHERE id=$id",
                ("$next", successor), ("$tick", observed.Value), ("$id", plan))
            : tx.Execute("UPDATE plans SET retired=1,retired_tick=$tick WHERE id=$id", ("$tick", observed.Value), ("$id", plan));

    /// <summary>The newest observation a plan's own progress holds: the retirement tick of an owner that retires a plan on its
    /// recorded outcome (combat, load) rather than on a review's read. A plan without progress observed nothing (tick 0).</summary>
    internal static Tick ObservedTickIn(PlanState plan) => plan.Progress.Count == 0 ? new Tick(0) : plan.Progress.Max(p => p.View.Tick);

    /// <summary>
    /// Only settled autopilot methods retire. The current root plan, unresolved effects, cleanup and unsuccessful
    /// work keep their complete catalog entries. <paramref name="floors"/> collects the newest settled tick per
    /// world; the caller raises the shared floors with it once the transaction commits.
    /// </summary>
    internal static void RetireReviewPlans(
        StoreTransaction tx,
        GenerationSnapshot current,
        Tick tick,
        CurrentConstruction? census,
        WorkAdmissionEvidence workEvidence,
        Dictionary<(ColonyId Colony, MapId Map), Tick> floors
    )
    {
        var links = tx.Query(
            "SELECT p.id FROM plans p INDEXED BY active_plans CROSS JOIN need_methods m ON m.plan_id=p.id WHERE p.retired=0 ORDER BY p.id LIMIT 257",
            [],
            r => new PlanId(r.GetString(0))
        );
        if (links.Count > 256)
        {
            throw new CapacityException();
        }
        foreach (var plan in links)
        {
            if (plan == current.Plan)
            {
                continue;
            }
            var p = LoadIn(tx, plan);
            if (PlanWorkOpen(p, census))
            {
                continue;
            }
            // Accepted assignment is installation evidence, not completion of its selected graph.
            // Continuation or observed ownership resolution retires that Method; a refused
            // replacement still owes cleanup of the installed predecessor.
            if (InstalledActivityPlan(tx, p)?.Progress[0].Action.PawnActivities is { } activity
                && activity.Orders.Any(order => !order.Release))
            {
                continue;
            }
            var queues = p.Progress.Where(progress => progress.Action.OrderedWork is { Cancel: false }
                && progress.View.Snapshot.SameWorld(current))
                .Select(progress => new WorkAction(plan, progress.Action, progress.View)).ToArray();
            if (queues.Length != 0 && WorkRules.WorkClaimsOf(workEvidence, queues).Pawns.Count != 0)
                continue;
            // A fight owns its empty plan while open or rostering a drafted defender: retiring it would hide the
            // fight from its goal, and the clock scheduler would stop admitting ticks mid-fight.
            if (CombatFightHolds(tx, plan))
            {
                continue;
            }
            var settled = true;
            foreach (var progress in p.Progress)
            {
                var w = progress.View;
                var absent =
                    w.Stage == Stage.Cancelled
                    && (w.Attempt.Value == 0 || w.Observes(Effect.Absent));
                var settledOutcome = SettledRetirementOutcome(progress);
                if (!absent && !settledOutcome)
                {
                    settled = false;
                    break;
                }
                if (settledOutcome && w.Snapshot.Colony == current.Colony && w.Snapshot.Load == current.Load && w.Snapshot.Map == current.Map && tick < w.Tick)
                {
                    settled = false;
                    break;
                }
            }
            if (!settled)
            {
                continue;
            }
            foreach (var progress in p.Progress)
            {
                var w = progress.View;
                if (SettledRetirementOutcome(progress))
                {
                    var key = (w.Snapshot.Colony, w.Snapshot.Map);
                    floors[key] = floors.TryGetValue(key, out var floor) && floor > w.Tick ? floor : w.Tick;
                }
            }
            RetirePlanIn(tx, plan, tick);
        }
    }
}
