using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>An objective's remaining work: the work type that does it, the speed stat (null: every worker at rate 1), the amount and the bench factor.</summary>
public sealed record QuestWorkload
{
    public required WorkType Work { get; init; }

    public required string? Stat { get; init; }

    public required double Amount { get; init; }

    public required double RateFactor { get; init; }
}

public static class QuestCapacityForecast
{
    private readonly record struct QuestTimedWork(long Deadline, QuestWorkload Workload);

    /// <summary>
    /// Conditional deadline capacity at observed native work rates. Shared needs/service estimates
    /// debit recovery and approach; competing untimed work makes the affected capacity unassessed.
    /// </summary>
    public static QuestSkipReason QuestDeadlineFeasibility(QuestState offer, ReviewFacts f)
    {
        long now = f.QuestObservedTick.Value;
        var (tasks, reason) = QuestDeadlineTasks(offer, now, f.Recipes);
        if (!reason.IsNone)
        {
            return reason;
        }
        if (tasks.Count == 0)
        {
            return "";
        }
        if (f.QuestPawns is not QuestPawns.Seen { Workers: var workers, Departures: var capacities } || f.QuestRoster is not QuestRoster.Planned { Spare: var spare })
        {
            return "deadline_unknown";
        }
        // Nonautomatic open work has first claim. Untimed commitments reserve a whole worker; timed work
        // shares the same budgets as the new offer.
        long reserved = 0;
        foreach (var q in f.QuestOffers)
        {
            if (q.State != QuestStatus.Ongoing || q.Id == offer.Id)
            {
                continue;
            }
            if (f.RootOf(q) is not QuestRoot.Classified { Profile: var p })
            {
                return "open_demands_unknown";
            }
            if (p.Cost == QuestCost.Free || p.NeverAct || p.Disposition == QuestDisposition.Observe)
            {
                continue;
            }
            var (pending, r) = QuestDeadlineTasks(q, now, f.Recipes);
            if (!r.IsNone)
            {
                return "open_demands_unknown";
            }
            if (pending.Count == 0)
            {
                var (n, demandReason) = QuestSelection.QuestPawnDemand(q, f);
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
        foreach (var p in workers)
        {
            byId[p.Id] = p;
        }
        var allowed = new HashSet<PawnId>(spare);
        var eligible = new List<RimGovernor.Brain.GameFacts.Observations.QuestWorker>();
        foreach (var p in capacities.Select(c => c.Observed).OrderBy(c => c.PawnId))
        {
            if (!allowed.Contains(p.PawnId))
            {
                continue;
            }
            if (!p.HealthyAdult)
            {
                continue;
            }
            if (!byId.ContainsKey(p.PawnId))
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
                var p = byId[pawn.PawnId];
                if (!QuestWorkEnabled(p, task.Workload.Work))
                {
                    continue;
                }
                var days = (task.Deadline - now) / (double)GameTime.TicksPerDay;
                if (NativeLaborCapacity.HoursPerDay(p.Observed, f.ForecastObserved.Needs, [task.Workload.Work], days)
                    is not ForecastTerm.Assessed(var hours)) return "deadline_capacity_unassessed";
                // Native rates every recipe work speed stat, construction and plant work, so a missing one is a broken read.
                var rate = task.Workload.Stat is not { } stat ? 1.0
                    : pawn.Rates.FirstOrDefault(r => r.Stat == stat) is { } measured ? measured.Rate : throw new RequiredFactException($"quest work rate {stat} of {pawn.PawnId}");
                rate *= task.Workload.RateFactor;
                if (!FinitePositive(rate))
                {
                    continue;
                }
                var budget = days * hours * GameTime.TicksPerHour - used.GetValueOrDefault(pawn.PawnId);
                if (budget <= 0)
                {
                    continue;
                }
                var done = Math.Min(remaining, budget * rate);
                used[pawn.PawnId] = used.GetValueOrDefault(pawn.PawnId) + done / rate;
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

    private static (List<QuestTimedWork> Tasks, QuestSkipReason Reason) QuestDeadlineTasks(QuestState offer, long now, RecipeFacts recipes)
    {
        var tasks = new List<QuestTimedWork>();
        foreach (var objective in offer.Objectives)
        {
            if (objective.Kind == RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.Expiry)
            {
                continue;
            }
            if (offer.State == QuestStatus.Ongoing
                && !objective.Active && objective.Kind != RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.Monument)
            {
                continue;
            }
            long deadline;
            if (objective.Timing is RimGovernor.Brain.GameFacts.Observations.QuestObjectiveTiming.Deadline due)
            {
                deadline = due.Tick;
            }
            else if (objective.Timing is RimGovernor.Brain.GameFacts.Observations.QuestObjectiveTiming.Duration { Ticks: var duration })
            {
                if (duration > long.MaxValue - now)
                {
                    return ([], "deadline_unknown");
                }
                deadline = now + duration;
            }
            else
            {
                // An untimed objective sets no deadline.
                continue;
            }
            if (objective.Goal is RimGovernor.Brain.GameFacts.Observations.QuestObjectiveGoal.Things things && things.Done >= things.Count)
            {
                continue;
            }
            var work = objective.Workload is { } load ? WorkloadOf(load, recipes) : null;
            if (objective.Monument is { } marker)
            {
                if (marker.State is RimGovernor.Brain.GameFacts.Observations.QuestMonumentState.Installed { AllDone: true })
                {
                    continue;
                }
                var amount = 0.0;
                foreach (var piece in marker.Pieces)
                {
                    if (QuestMonumentPolicy.PieceBuilt(piece))
                    {
                        continue;
                    }
                    if (piece.Options.Count == 0)
                    {
                        // A stuffed piece whose stuff categories allow no stuff cannot be built.
                        return ([], "monument_piece_unbuildable");
                    }
                    var largest = 0.0;
                    foreach (var option in piece.Options)
                    {
                        largest = Math.Max(largest, option.Work);
                    }
                    amount += largest;
                }
                work = new QuestWorkload { Work = WorkType.Construction, Stat = "ConstructionSpeed", Amount = amount, RateFactor = 1 };
            }
            if (work is null)
            {
                return ([], "deadline_unknown");
            }
            if (work.Amount == 0)
            {
                continue;
            }
            tasks.Add(new QuestTimedWork(deadline, work));
        }
        return (tasks, "");
    }

    /// <summary>
    /// The objective's remaining work with the work type that does it: plant cutting for a harvest, the bill work of the
    /// recipe for a bill. Native names the recipe only from a usable bench, so a recipe the catalog's benches do not host
    /// raises <see cref="RequiredFactException"/>.
    /// </summary>
    internal static QuestWorkload WorkloadOf(RimGovernor.Brain.GameFacts.Observations.QuestWorkload load, RecipeFacts recipes)
    {
        var work = WorkType.PlantCutting;
        if (load.Recipe is { } recipe)
        {
            var (billWork, hosted) = recipes.BillWorkOf(recipe);
            work = hosted ? billWork : throw new RequiredFactException($"bill work type of quest recipe {recipe}");
        }
        return new QuestWorkload { Work = work, Stat = load.Stat, Amount = load.Work, RateFactor = load.RateFactor };
    }

    private static bool QuestWorkEnabled(WorkPawn p, WorkType work)
    {
        foreach (var w in p.Work.Priorities())
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
