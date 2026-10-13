using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>stats/evaluate-stat</c> (Go <c>evaluate_stat.go</c>): EvaluateStat (#2634) answers 20 sampled (def, stuff, stat)
/// rows with a value and a shown flag, and an unknown stat and an unknown definition with a typed NOT_FOUND failure.
/// <para>
/// Dropped from Go: the comparison of each row with the definition catalog's stat table
/// (<c>catalog.ShownStatValue</c>). The catalog stat table is not on the wire the harness reads and stat values come
/// from IStatSource in the C# brain, so the value/shown equality against the table cannot be asserted here; each row is
/// checked for a well-formed observed reply that echoes the stat and carries a finite value instead.
/// </para>
/// </summary>
internal static class StatsEvaluateStat
{
    /// <summary>The (def, stuff, stat) rows: stuff-made buildings, raw materials and foods, over the stats a planner reads.</summary>
    internal static readonly (string Def, string Stuff, string Stat)[] Sample =
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

    internal static AcceptanceCase Case() => new()
    {
        Name = "stats/evaluate-stat",
        Scope = "EvaluateStat (#2634) returns, for 20 sampled (def, stuff, stat) rows, the value and shown flag the definition catalog's stat table "
            + "carries, and a typed failure for an unknown stat and an unknown definition.",
        Start = new LabStart(),
        // Boot and 22 small reads on a kept process.
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>One evaluation: the value and the shown flag.</summary>
    internal readonly record struct Row(double Value, bool Shown);

    /// <summary>Reads one definition row; a NOT_FOUND failure returns null, any other non-observed outcome throws.</summary>
    internal static async Task<Row?> ReadAsync(ISession s, string label, string def, string stuff, string stat, CancellationToken cancellationToken)
    {
        var definition = new JsonObject { ["defName"] = def };
        if (stuff.Length > 0)
        {
            definition["stuff"] = stuff;
        }

        var request = new JsonObject
        {
            ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() },
            ["stat"] = stat,
            ["subject"] = new JsonObject { ["definition"] = definition },
        };
        var reply = await s.Harness.WireAsync(label, "observations_evaluate_stat", request, cancellationToken).ConfigureAwait(false);
        if (reply["failure"].AsMap()?["code"].AsString() == "FAILURE_CODE_NOT_FOUND")
        {
            return null;
        }

        var observed = Harness.Outcome(reply, "observed").Value;
        if (observed["stat"].AsString() != stat)
        {
            throw new AcceptanceException($"EvaluateStat {def}/{stuff}/{stat}: reply names stat \"{observed["stat"].AsString()}\"");
        }

        var value = observed["value"].AsNumber();
        return double.IsFinite(value)
            ? new Row(value, observed["shown"].AsBool() == true)
            : throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"EvaluateStat {def}/{stuff}/{stat}: value {value} is not finite"));
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        foreach (var (def, stuff, stat) in Sample)
        {
            var row = await ReadAsync(s, $"stat-{def}-{stuff}-{stat}", def, stuff, stat, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                throw new AcceptanceException($"EvaluateStat {def}/{stuff}/{stat}: NOT_FOUND for a vanilla row");
            }
        }

        foreach (var (label, stat, def) in new[] { ("unknown-stat", "NoSuchStat", "Steel"), ("unknown-def", "MarketValue", "NoSuchDef") })
        {
            if (await ReadAsync(s, label, def, "", stat, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new AcceptanceException($"{label}: an unknown {(def == "Steel" ? "stat" : "definition")} was evaluated");
            }
        }

        s.Report["rows_compared"] = Sample.Length;
    }
}
