using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Simulation.CombatMetrics;
using FightMetrics = RimGovernor.Brain.Simulation.CombatMetrics.Metrics;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The bundle files a combat-fixture run records and the scoring of its raw reads (Go <c>metrics.go</c>).
/// <see cref="ReadsFile"/> holds every raw test/lab_stage read reply, one JSON object per line, in tick order;
/// <see cref="MetricsFile"/> is the aggregate; <see cref="CombatRowsFile"/>, when a run served the planner, carries its
/// clock_stop, combat_order and dispatch rows.
/// </summary>
internal static class CombatLabBundle
{
    internal const string ReadsFile = "combat_reads.jsonl";
    internal const string MetricsFile = "combat_metrics.json";
    internal const string CombatRowsFile = "combat_rows.jsonl";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Scores a run's raw test/lab_stage read replies (tick order) with the shared combat metrics scoring.</summary>
    internal static FightMetrics Aggregate(string fixture, IReadOnlyDictionary<string, string> staged, IReadOnlyList<JsonObject> reads) =>
        FightMetrics.Aggregate(fixture, staged, [.. reads.Select(ReadRows)]);

    /// <summary>Adapts one raw read reply to the scoring rows.</summary>
    internal static MetricsRead ReadRows(JsonObject read)
    {
        var pawns = new List<MetricsPawn>();
        foreach (var r in read["pawns"].AsSlice())
        {
            var row = r.AsMap() ?? [];
            pawns.Add(new MetricsPawn(row["id"].AsString(), row["dead"].AsBool() == true, row["downed"].AsBool() == true, row["fleeing"].AsBool() == true));
        }

        var damage = new List<MetricsHit>();
        foreach (var r in read["damage"].AsSlice())
        {
            var row = r.AsMap() ?? [];
            damage.Add(new MetricsHit(
                (int)row["tick"].NumberOrZero(),
                row["victim"].AsString(),
                row["victimSide"].AsString(),
                row["instigator"].AsString(),
                row["instigatorSide"].AsString(),
                row["dealt"].NumberOrZero(),
                row["downed"].AsBool() == true,
                row["dead"].AsBool() == true));
        }

        return new MetricsRead((int)read["tick"].NumberOrZero()) { Pawns = pawns, Damage = damage, DamageDropped = (int)read["damageDropped"].NumberOrZero() };
    }

    /// <summary>
    /// Adds the rows' combat orders and step latency: each combat stop's resume_latency_ms, each combat_order row's
    /// verdict, and the routine defense planner's actions (drafts, holds) from the worker's outcome rows, issued once one
    /// completes, refused when one only ever refused. A missing file is a run no planner served.
    /// </summary>
    internal static FightMetrics ScanLogs(FightMetrics m, string path)
    {
        var latencies = new List<long>();
        var actions = new Dictionary<string, string>(StringComparer.Ordinal);
        var issued = m.OrdersIssued;
        var refused = m.OrdersRefused;
        foreach (var row in ServiceLogs.ReadLogs(path))
        {
            var f = row.Fields();
            switch (row.Kind)
            {
                case "clock_stop":
                    if (f.TryGetValue("resume_latency_ms", out var latency))
                    {
                        latencies.Add((long)Number(latency));
                    }

                    break;
                case "combat_order":
                    if (Text(f, "verdict") == "refused")
                    {
                        refused++;
                    }
                    else
                    {
                        issued++;
                    }

                    break;
                case "dispatch":
                    var action = Text(f, "target");
                    if (!action.StartsWith("routine-defense-", StringComparison.Ordinal))
                    {
                        break;
                    }

                    var verdict = Text(f, "verdict");
                    var current = actions.GetValueOrDefault(action, "");
                    if (Text(f, "stage_after") == "completed" || verdict == "completed")
                    {
                        actions[action] = "issued";
                    }
                    else if ((NonEmpty(f.GetValueOrDefault("refused")) || verdict == "refused") && current.Length == 0)
                    {
                        actions[action] = "refused";
                    }

                    break;
                default:
                    break;
            }
        }

        foreach (var outcome in actions.Values)
        {
            if (outcome == "issued")
            {
                issued++;
            }
            else
            {
                refused++;
            }
        }

        var result = m with { OrdersIssued = issued, OrdersRefused = refused };
        if (latencies.Count > 0)
        {
            latencies.Sort();
            result = result with { StepLatencyP95Ms = latencies[(((latencies.Count * 95) + 99) / 100) - 1] };
        }

        return result;
    }

    /// <summary>Whether a row is one <see cref="ScanLogs"/> reads: the bundle keeps only these.</summary>
    internal static bool CombatRow(LogRow row) =>
        row.Kind == "combat_order" || row.Kind == "clock_stop" || row.Kind == "dispatch" || row.Kind == "clock_step"
        || (row.Kind == "planner_step" && Text(row.Fields(), "scope") == "immediate");

    /// <summary>Maps a staging's pawn ids to their sides.</summary>
    internal static Dictionary<string, string> StagedSides(StagedFixture s)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in s.Colonists())
        {
            output[id] = CombatFixtures.Colonist;
        }

        // Manhunters and mechanoids are the enemy side of their fixtures.
        foreach (var ids in new[] { s.Hostiles(), s.Manhunters(), s.Mechs() })
        {
            foreach (var id in ids)
            {
                output[id] = CombatFixtures.Hostile;
            }
        }

        return output;
    }

    /// <summary>
    /// Recomputes a recorded run's metrics from its bundle directory: the reads file, whose first line (the read right
    /// after staging) carries staged {id: side}, and the rows when present.
    /// </summary>
    internal static async Task<FightMetrics> AggregateBundleAsync(string dir, string fixture, CancellationToken cancellationToken)
    {
        var data = await File.ReadAllTextAsync(Path.Combine(dir, ReadsFile), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var reads = new List<JsonObject>();
        foreach (var line in data.Split('\n'))
        {
            if (line.Length == 0 || line == "\r")
            {
                continue;
            }

            try
            {
                reads.Add(JsonNode.Parse(line)?.AsObject() ?? throw new AcceptanceException($"{ReadsFile}: line is not an object"));
            }
            catch (JsonException e)
            {
                throw new AcceptanceException($"{ReadsFile}: {e.Message}", e);
            }
        }

        if (reads.Count == 0)
        {
            throw new AcceptanceException($"{ReadsFile}: no reads");
        }

        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, side) in reads[0]["staged"].AsMap() ?? [])
        {
            staged[id] = side.AsString();
        }

        var m = Aggregate(fixture, staged, reads);
        return ScanLogs(m, Path.Combine(dir, CombatRowsFile));
    }

    /// <summary>The metrics as the Go JSON encoding held them (indented, damageDropped omitted at zero).</summary>
    internal static JsonObject MetricsJson(FightMetrics m)
    {
        var o = new JsonObject
        {
            ["version"] = m.Version,
            ["fixture"] = m.Fixture,
            ["ticks"] = m.Ticks,
            ["colonistDamage"] = m.ColonistDamage,
            ["colonistDowns"] = m.ColonistDowns,
            ["colonistDeaths"] = m.ColonistDeaths,
            ["enemyDowns"] = m.EnemyDowns,
            ["enemyDeaths"] = m.EnemyDeaths,
            ["enemyFled"] = m.EnemyFled,
            ["firstContactTick"] = m.FirstContactTick,
            ["resolvedTick"] = m.ResolvedTick,
            ["contactToResolutionTicks"] = m.ContactToResolution,
            ["winner"] = m.Winner,
            ["friendlyFireHits"] = m.FriendlyFireHits,
        };
        if (m.DamageDropped != 0)
        {
            o["damageDropped"] = m.DamageDropped;
        }

        o["ordersIssued"] = m.OrdersIssued;
        o["ordersRefused"] = m.OrdersRefused;
        o["stepLatencyP95Ms"] = m.StepLatencyP95Ms;
        return o;
    }

    /// <summary>The indented text of <see cref="MetricsJson"/>.</summary>
    internal static string MetricsText(FightMetrics m) => MetricsJson(m).ToJsonString(Indented) + "\n";

    private static string Text(IReadOnlyDictionary<string, object?> fields, string key) => fields.GetValueOrDefault(key) as string ?? "";

    private static double Number(object? value) => value switch
    {
        double d => d,
        long l => l,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => -1,
    };

    private static bool NonEmpty(object? value) => value is System.Collections.IEnumerable e and not string && e.GetEnumerator().MoveNext();
}
