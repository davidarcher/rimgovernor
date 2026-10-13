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
        if (!w.Effect.TryGetValue(out var effect))
        {
            return false;
        }
        var ended = w.Stage is Stage.Unsuccessful or Stage.Cancelled;
        if (effect == Effect.Completed)
        {
            return w.Stage is Stage.Completed or Stage.Cancelled;
        }
        if (progress.Action.Kind != ActionKind.Building)
        {
            return false;
        }
        return effect switch
        {
            Effect.Unsuccessful => ended,
            Effect.Absent => ended && w.Attempt.Value > 0,
            _ => false,
        };
    }

    /// <summary>
    /// Only settled autopilot methods retire. The current root plan, unresolved effects, cleanup and unsuccessful
    /// work keep their complete catalog entries. <paramref name="floors"/> collects the newest settled tick per
    /// world; the caller raises the shared floors with it once the transaction commits.
    /// </summary>
    internal static void RetireRoundsPlans(
        StoreTransaction tx,
        GenerationSnapshot current,
        Tick tick,
        Fact<CurrentConstruction> census,
        Dictionary<(ColonyId Colony, MapId Map), Tick> floors
    )
    {
        var links = tx.Query(
            "SELECT p.id,m.kind,m.owner_id FROM plans p INDEXED BY active_plans CROSS JOIN plan_methods m ON m.plan_id=p.id WHERE p.retired=0 ORDER BY p.id LIMIT 257",
            [],
            r => (Plan: new PlanId(r.GetString(0)), Kind: r.GetString(1), Owner: r.GetString(2))
        );
        if (links.Count > 256)
        {
            throw new CapacityException();
        }
        foreach (var v in links)
        {
            if (v.Plan == current.Plan)
            {
                continue;
            }
            StandardState? g = v.Kind == "standard" ? LoadStandard(tx, new StandardId(v.Owner)) : null;
            var p = LoadIn(tx, v.Plan);
            if (PlanWorkOpen(p, census))
            {
                continue;
            }
            // A fight owns its empty plan while open or rostering a drafted defender: retiring it would hide the
            // fight from its goal, and the clock scheduler would stop admitting ticks mid-fight.
            if (CombatFightHolds(tx, v.Plan))
            {
                continue;
            }
            var settled = true;
            foreach (var progress in p.Progress)
            {
                var w = progress.View;
                var known = w.Effect.TryGetValue(out var effect);
                var absent = w.Stage == Stage.Cancelled && (w.Attempt.Value == 0 || (known && effect == Effect.Absent));
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
            if (g is { Revision: ulong.MaxValue })
            {
                throw new CapacityException();
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
            _ = tx.Execute("UPDATE plans SET retired=1 WHERE id=$id", ("$id", v.Plan));
            if (g is not null)
            {
                BumpRevision(tx, g);
            }
        }
    }
}
