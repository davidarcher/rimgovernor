using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record QuestWorkCapacity
{
    public PawnId Pawn { get; init; }

    public IReadOnlyDictionary<string, double> Rates { get; init; } = new Dictionary<string, double>();

    public Fact<bool> HealthyAdult { get; init; }
}

public sealed record QuestWorkload
{
    public WorkType Work { get; init; }

    public string Stat { get; init; } = "";

    public double Amount { get; init; }

    public double RateFactor { get; init; }
}

public static partial class PolicyFunctions
{
    private readonly record struct QuestTimedWork(long Deadline, QuestWorkload Workload);

    /// <summary>
    /// The forecast prices actual native work at observed rates. It uses complete days, at most eight
    /// scheduled work/Anything hours per day, and half that time for useful work; the remainder funds
    /// hauling, walking and normal upkeep.
    /// </summary>
    public static QuestSkipReason QuestDeadlineFeasibility(JoinerOffer offer, RoundsFacts f)
    {
        var nk = f.QuestObservedTick.TryGetValue(out var observed);
        var now = observed.Value;
        var (tasks, reason) = QuestDeadlineTasks(offer, now);
        if (!reason.IsNone)
        {
            return reason;
        }
        if (tasks.Count == 0)
        {
            return "";
        }
        if (!nk)
        {
            return "deadline_unknown";
        }
        var ck = f.QuestWorkCapacity.TryGetValue(out var capacities);
        var wk = f.QuestWorkers.TryGetValue(out var workers);
        var sk = f.QuestSparePawns.TryGetValue(out var spare);
        var ok = f.QuestOffers.TryGetValue(out var open);
        if (!ck || !wk || !sk || !ok)
        {
            return "deadline_unknown";
        }
        // Nonautomatic open work has first claim. Untimed commitments reserve a whole worker; timed work
        // shares the same budgets as the new offer.
        long reserved = 0;
        foreach (var q in open!)
        {
            if (q.State != "Ongoing" || q.Quest == offer.Quest)
            {
                continue;
            }
            if (!q.Profile.TryGetValue(out var p))
            {
                return "open_demands_unknown";
            }
            if (p.Cost == QuestCost.Free || p.NeverAct || p.Disposition == QuestDisposition.Observe)
            {
                continue;
            }
            var (pending, r) = QuestDeadlineTasks(q, now);
            if (!r.IsNone)
            {
                return "open_demands_unknown";
            }
            if (pending.Count == 0)
            {
                var (n, demandReason) = QuestPawnDemand(q);
                if (!demandReason.IsNone)
                {
                    return demandReason;
                }
                reserved += n;
            }
            else
            {
                tasks.AddRange(pending);
            }
        }
        var byId = new Dictionary<PawnId, WorkPawn>();
        foreach (var p in workers!)
        {
            byId[p.Id] = p;
        }
        var allowed = new HashSet<PawnId>(spare!);
        var eligible = new List<QuestWorkCapacity>();
        foreach (var p in capacities!.OrderBy(c => c.Pawn))
        {
            if (!allowed.Contains(p.Pawn))
            {
                continue;
            }
            if (!p.HealthyAdult.TryGetValue(out var healthy) || !healthy)
            {
                continue;
            }
            if (!byId.ContainsKey(p.Pawn))
            {
                continue;
            }
            if (reserved > 0)
            {
                reserved--;
                continue;
            }
            eligible.Add(p);
        }
        var ordered = tasks.OrderBy(t => t.Deadline).ToList();
        var used = new Dictionary<PawnId, double>();
        foreach (var task in ordered)
        {
            if (task.Deadline <= now)
            {
                return "deadline";
            }
            var remaining = task.Workload.Amount;
            if (remaining <= 0)
            {
                continue;
            }
            foreach (var pawn in eligible)
            {
                var p = byId[pawn.Pawn];
                if (!p.WorkHoursPerDay().TryGetValue(out var scheduled))
                {
                    return "deadline_unknown";
                }
                if (!QuestWorkEnabled(p, task.Workload.Work))
                {
                    continue;
                }
                var rate = 1.0;
                if (task.Workload.Stat.Length != 0)
                {
                    if (!pawn.Rates.TryGetValue(task.Workload.Stat, out rate))
                    {
                        return "deadline_unknown";
                    }
                }
                rate *= task.Workload.RateFactor;
                if (!FinitePositive(rate))
                {
                    continue;
                }
                var budget = (double)((task.Deadline - now) / GameTime.TicksPerDay) * (Math.Min(scheduled, 8) * GameTime.TicksPerHour) / 2 - used.GetValueOrDefault(pawn.Pawn);
                if (budget <= 0)
                {
                    continue;
                }
                var done = Math.Min(remaining, budget * rate);
                used[pawn.Pawn] = used.GetValueOrDefault(pawn.Pawn) + done / rate;
                remaining -= done;
                if (remaining <= 1e-6)
                {
                    break;
                }
            }
            if (remaining > 1e-6)
            {
                return "deadline_capacity";
            }
        }
        return "";
    }

    private static (List<QuestTimedWork> Tasks, QuestSkipReason Reason) QuestDeadlineTasks(JoinerOffer offer, long now)
    {
        var tasks = new List<QuestTimedWork>();
        foreach (var objective in offer.Objectives)
        {
            if (objective.Kind == Pb.QuestObjectiveKind.Expiry)
            {
                continue;
            }
            if (offer.State == "Ongoing"
                && objective.Active.TryGetValue(out var active) && !active && objective.Kind != Pb.QuestObjectiveKind.Monument)
            {
                continue;
            }
            var dk = objective.DeadlineTicks.TryGetValue(out var deadline);
            var tk = objective.DurationTicks.TryGetValue(out var duration);
            if (!dk && !tk)
            {
                continue;
            }
            if (!dk)
            {
                if (duration > long.MaxValue - now)
                {
                    return ([], "deadline_unknown");
                }
                deadline = now + duration;
            }
            if (objective.Count.TryGetValue(out var count) && objective.Produced.TryGetValue(out var produced) && produced >= count)
            {
                continue;
            }
            var known = objective.Workload.TryGetValue(out var work);
            if (objective.Monument.TryGetValue(out var marker))
            {
                if (marker.AllDone.TryGetValue(out var allDone) && allDone)
                {
                    continue;
                }
                var amount = 0.0;
                foreach (var piece in marker.Pieces)
                {
                    if (piece.Built.TryGetValue(out var built) && built)
                    {
                        continue;
                    }
                    if (piece.BuildOptions.Count == 0)
                    {
                        return ([], "deadline_unknown");
                    }
                    var largest = 0.0;
                    foreach (var option in piece.BuildOptions)
                    {
                        largest = Math.Max(largest, option.Work);
                    }
                    amount += largest;
                }
                work = new QuestWorkload { Work = WorkType.Construction, Stat = "ConstructionSpeed", Amount = amount, RateFactor = 1 };
                known = true;
            }
            if (!known)
            {
                return ([], "deadline_unknown");
            }
            if (work!.Amount == 0)
            {
                continue;
            }
            tasks.Add(new QuestTimedWork(deadline, work));
        }
        return (tasks, "");
    }

    private static bool QuestWorkEnabled(WorkPawn p, WorkType work)
    {
        if (!p.Work.TryGetValue(out var rows))
        {
            return false;
        }
        foreach (var w in rows)
        {
            if (w.Work == work)
            {
                return !w.Disabled && w.Priority > 0;
            }
        }
        return false;
    }

    internal static bool FinitePositive(double v) => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v);
}
