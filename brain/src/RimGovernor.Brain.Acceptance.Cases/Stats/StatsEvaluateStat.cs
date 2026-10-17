using System.Globalization;
using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>stats/evaluate-stat</c>: observations_evaluate_stat (#2634) answers 20 sampled (def, stuff, stat) rows with an
/// observed evaluation that echoes the stat and carries a finite value, a second read of each row is bit-identical
/// (the oracle the def stat table is computed from is deterministic on a paused world), and an unknown stat and an
/// unknown definition fail NOT_FOUND.
/// </summary>
internal static class StatsEvaluateStat
{
    /// <summary>The (def, stuff, stat) rows: stuff-made buildings, raw materials and foods, over the stats a planner reads.</summary>
    private static readonly (string Def, string Stuff, string Stat)[] Sample =
    [
        ("Wall", "BlocksGranite", "MaxHitPoints"),
        ("Wall", "BlocksGranite", "WorkToBuild"),
        ("Wall", "Steel", "MaxHitPoints"),
        ("Wall", "Steel", "Flammability"),
        ("Wall", "WoodLog", "MaxHitPoints"),
        ("Wall", "WoodLog", "Flammability"),
        ("Wall", "WoodLog", "Beauty"),
        ("Door", "Steel", "MaxHitPoints"),
        ("Door", "WoodLog", "WorkToBuild"),
        ("Door", "WoodLog", "MarketValue"),
        ("Steel", "", "MarketValue"),
        ("Steel", "", "Mass"),
        ("Steel", "", "MaxHitPoints"),
        ("WoodLog", "", "MarketValue"),
        ("WoodLog", "", "Flammability"),
        ("Silver", "", "MarketValue"),
        ("MealSimple", "", "MarketValue"),
        ("MealSimple", "", "Nutrition"),
        ("Campfire", "", "MaxHitPoints"),
        ("Campfire", "", "WorkToBuild"),
    ];

    internal static TypedCase Case() => new()
    {
        Name = "stats/evaluate-stat",
        Scope = "observations_evaluate_stat (#2634) answers 20 sampled (def, stuff, stat) rows with an observed evaluation of that stat "
            + "and a finite value, a repeated read of each row is bit-identical, and an unknown stat and an unknown definition fail NOT_FOUND.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task<Observations.EvaluateStatReplyOutcome?> ReadAsync(TypedSession s, string label, string def, string stuff, string stat, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.EvaluateStat, new Observations.EvaluateStatRequest
        {
            Scope = s.Scope,
            Stat = stat,
            Subject = new Observations.StatSubject
            {
                Subject = new Observations.StatSubjectSubject.DefinitionCase(new Observations.StatDefSubject { DefName = def, Stuff = stuff.Length > 0 ? stuff : null }),
            },
        }, cancellationToken).ConfigureAwait(false);
        return reply.Outcome;
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var (def, stuff, stat) in Sample)
        {
            var label = $"stat-{def}-{stuff}-{stat}";
            var first = (await ReadAsync(s, label, def, stuff, stat, cancellationToken).ConfigureAwait(false))?.Observed;
            var second = (await ReadAsync(s, label + "-again", def, stuff, stat, cancellationToken).ConfigureAwait(false))?.Observed;
            if (first is null || second is null)
            {
                problems.Add($"{def}/{stuff}/{stat}: not observed");
            }
            else if (first.Stat != stat || !double.IsFinite(first.Value))
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{def}/{stuff}/{stat}: read stat \"{first.Stat}\" value {first.Value}"));
            }
            else if (first.Shown != second.Shown || BitConverter.SingleToInt32Bits((float)first.Value) != BitConverter.SingleToInt32Bits((float)second.Value))
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{def}/{stuff}/{stat}: first read {first.Value}/{first.Shown}, second {second.Value}/{second.Shown}"));
            }
        }

        foreach (var (label, stat, def) in new[] { ("unknown-stat", "NoSuchStat", "Steel"), ("unknown-def", "MarketValue", "NoSuchDef") })
        {
            var outcome = await ReadAsync(s, label, def, "", stat, cancellationToken).ConfigureAwait(false);
            if (outcome?.Failure?.Code != Common.FailureCode.NotFound)
            {
                problems.Add($"{label}: want NOT_FOUND, got {outcome}");
            }
        }

        s.Record("rows", Sample.Length);
        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}
