using RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy;

/// <summary>A generated practice session (#3341): its task and the native mechanics of the pawn practising there.</summary>
public sealed record ForecastPracticeTask(string Task, PracticeWorker Worker);
public sealed record ForecastMaintenanceRequirement(AssignedCapability Requirement, EquatableArray<WorkSkill> Skills);
public sealed record ForecastMaintenanceConsequence(string Requirement, Fact<double> MissingXp, EquatableArray<string> Findings);

public static class ForecastMaintenancePolicy
{
    public static EquatableArray<ForecastMaintenanceConsequence> ForecastMaintenance(
        EquatableArray<ForecastMaintenanceRequirement> requirements, EquatableArray<ForecastPracticeTask> practice,
        ForecastActivityPlan plan, IReadOnlyDictionary<string, Fact<bool>> completed, double horizon)
    {
        var result = new List<ForecastMaintenanceConsequence>();
        foreach (var requirement in requirements)
        {
            var gaps = new List<double>(); var unknown = false; var findings = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var alternative in requirement.Requirement.Alternatives)
            {
                var skill = requirement.Skills.FirstOrDefault(s => s.Name == alternative.Skill);
                if (skill?.Disabled == true) continue;
                if (skill?.Learning is not { } learning || Bound(learning, horizon) is not { } bound)
                { unknown = true; findings.Add("maintenance_learning_unknown:" + alternative.Skill); continue; }

                if (bound < 0) { unknown = true; findings.Add("maintenance_window_expired"); continue; }
                var callbacks = plan.Allocations.Where(a => a.Pawn == requirement.Requirement.Pawn.Value
                    && completed.GetValueOrDefault(a.Activity) == Known(true) && a.End <= bound
                    && a.Learning?.Skill == alternative.Skill).SelectMany(a => a.Learning!.Callbacks).ToList();
                foreach (var task in practice.Where(p => p.Worker.Pawn == requirement.Requirement.Pawn.Value && p.Worker.Skill == alternative.Skill))
                {
                    var allocation = plan.Allocations.FirstOrDefault(a => a.Activity == task.Task);
                    if (allocation is null || completed.GetValueOrDefault(task.Task) != Known(true)) continue;
                    findings.UnionWith(task.Worker.Findings);
                    findings.Add("practice_injury_recovery_unassessed");
                    if (Interval(task.Worker) is null)
                    { unknown = true; findings.Add("practice_callback_cadence_unknown"); continue; }
                    callbacks.AddRange(PracticeCallbacks(task, allocation, bound));
                }
                var projected = ForecastSkillPolicy.ForecastSkill(learning, callbacks.OrderBy(e => e.Tick).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray(), bound);
                findings.UnionWith(projected.Findings);
                if (!projected.StoredLevel.TryGetValue(out var level) || !projected.Xp.TryGetValue(out var xp) || !learning.Aptitude.IsKnown)
                { unknown = true; continue; }
                var floor = Math.Max(0, alternative.MinLevel - learning.Aptitude.Value);
                if (level >= floor) { gaps.Add(0); continue; }
                if (floor >= learning.Levels.Count) { unknown = true; continue; }
                gaps.Add(Math.Max(0, learning.Levels.Skip(level).Take(floor - level).Sum(l => l.RequiredXp) - xp));
            }
            // A known satisfied OR branch suffices. Otherwise an unknown alternative can be the winner.
            Fact<double> missing = gaps.Contains(0) ? Known(0d) : unknown || gaps.Count == 0 ? default : Known(gaps.Min());
            result.Add(new(requirement.Requirement.Id, missing, EquatableArray.From(findings)));
        }
        return EquatableArray.From(result);
    }
    /// <summary>The last tick the learning's callbacks may fall at within <paramref name="horizon"/>: before its read stops
    /// being valid. Null when that validity was not read.</summary>
    private static int? Bound(SkillLearning learning, double horizon) =>
        learning.ValidForTicks.TryGetValue(out var valid) ? Math.Min((int)horizon, valid - 1) : null;

    /// <summary>The ticks between a practice session's callbacks; null when that cadence or the XP a callback pays was not read.</summary>
    private static double? Interval(PracticeWorker worker) =>
        worker.CallbackIntervalTicks.TryGetValue(out var interval) && interval > 0 && worker.RawXpPerCallback.IsKnown ? interval : null;

    /// <summary>The same bounded native practice callbacks used by maintenance and terminal skill stock.</summary>
    internal static IEnumerable<SkillXpEvent> PracticeCallbacks(ForecastPracticeTask task, ForecastActivityAllocation allocation, int bound)
    {
        if (Interval(task.Worker) is not { } interval) yield break;
        for (var number = 1; number <= 128 && allocation.Start + task.Worker.PreparationTicks + interval * number <= Math.Min(bound, allocation.End); number++)
            yield return new(task.Task + "/callback/" + number, (int)Math.Ceiling(allocation.Start + task.Worker.PreparationTicks + interval * number),
                task.Worker.RawXpPerCallback, task.Worker.Skill == "Shooting" ? SkillXpPath.Range : SkillXpPath.NormalTracker, true);
    }

    /// <summary>
    /// The XP a scheduled practice session applies to its pawn's skill (#3852): its native callbacks (<see cref="PracticeCallbacks"/>)
    /// through the skill arithmetic (<see cref="ForecastSkillPolicy.ForecastSkill"/>: learn rate, saturation, the range budget and
    /// the practice ceiling) within the day and the read's validity. Tier 1 prices it at the skill stock's value per XP, the raid
    /// harm one level more saves (<see cref="ColonyValue"/>). Unread mechanics, cadence or budget raise
    /// <see cref="RequiredFactException"/>.
    /// </summary>
    internal static double SessionXp(ForecastPracticeTask task, ForecastActivityAllocation allocation, double horizon)
    {
        var fact = task.Worker.Skill.ToLowerInvariant() + " practice " + task.Worker.Pawn;
        if (Interval(task.Worker) is null) throw new RequiredFactException(fact + ": callback cadence");
        if (Bound(task.Worker.Learning, Math.Min(horizon, GameTime.TicksPerDay)) is not { } bound) throw new RequiredFactException(fact + ": learning validity");
        var projected = ForecastSkillPolicy.ForecastSkill(task.Worker.Learning, [.. PracticeCallbacks(task, allocation, bound)], Math.Max(0, bound));
        return projected.AppliedGain.TryGetValue(out var xp) ? xp : throw new RequiredFactException(fact + ": " + string.Join(", ", projected.Findings));
    }
}
