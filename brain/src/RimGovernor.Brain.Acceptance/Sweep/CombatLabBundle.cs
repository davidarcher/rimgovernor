using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Simulation.CombatMetrics;
using RimGovernor.Brain.Telemetry;
using FightMetrics = RimGovernor.Brain.Simulation.CombatMetrics.Metrics;
using Fx = RimGovernor.Fixtures.Contracts;

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

    /// <summary>Scores a run's test/lab_stage read replies (tick order) with the shared combat metrics scoring.</summary>
    internal static FightMetrics Aggregate(string fixture, IReadOnlyDictionary<string, string> staged, IReadOnlyList<Fx.LabFightRead> reads) =>
        FightMetrics.Aggregate(fixture, staged, [.. reads.Select(ReadRows)]);

    /// <summary>Adapts one read reply to the scoring rows.</summary>
    internal static MetricsRead ReadRows(Fx.LabFightRead read) =>
        new(read.Tick)
        {
            Pawns = [.. read.Pawns.Select(p => new MetricsPawn(p.Id, p.Dead, p.Downed, p.Fleeing))],
            Damage = [.. read.Damage.Select(Hit)],
            DamageDropped = read.DamageDropped,
        };

    /// <summary>One ledger row as the scoring reads it.</summary>
    internal static MetricsHit Hit(Fx.LabDamageRow r) => new(r.Tick, r.Victim, r.VictimSide, r.Instigator, r.InstigatorSide, r.Dealt, r.Downed, r.Dead);

    /// <summary>One line of the reads file.</summary>
    internal static Fx.LabFightRead ParseRead(string line)
    {
        try
        {
            return Fx.FixtureJson.Deserialize<Fx.LabFightRead>(line);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{ReadsFile}: {e.Message}", e);
        }
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
                case TelemetryKinds.ClockStopKind:
                    if (f.TryGetValue("resume_latency_ms", out var latency))
                    {
                        latencies.Add((long)Number(latency));
                    }

                    break;
                case TelemetryKinds.CombatOrderKind:
                    if (Text(f, "verdict") == "refused")
                    {
                        refused++;
                    }
                    else
                    {
                        issued++;
                    }

                    break;
                case TelemetryKinds.DispatchKind:
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
        row.Kind == TelemetryKinds.CombatOrder.Name || row.Kind == TelemetryKinds.ClockStop.Name || row.Kind == TelemetryKinds.Dispatch.Name
        || row.Kind == TelemetryKinds.ClockStep.Name
        || (row.Kind == TelemetryKinds.PlannerStep.Name && Text(row.Fields(), "scope") == "immediate");

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
        var reads = new List<Fx.LabFightRead>();
        foreach (var line in data.Split('\n'))
        {
            if (line.Length == 0 || line == "\r")
            {
                continue;
            }

            reads.Add(ParseRead(line));
        }

        if (reads.Count == 0)
        {
            throw new AcceptanceException($"{ReadsFile}: no reads");
        }

        var staged = new Dictionary<string, string>(reads[0].Staged ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var m = Aggregate(fixture, staged, reads);
        return ScanLogs(m, Path.Combine(dir, CombatRowsFile));
    }

    /// <summary>The metrics as the Go JSON encoding held them (indented, damageDropped omitted at zero).</summary>
    internal static JsonNode MetricsJson(FightMetrics m) => OutputJson.Node(m, SweepJsonContext.Default.SimMetrics);

    /// <summary>The indented text of <see cref="MetricsJson"/>.</summary>
    internal static string MetricsText(FightMetrics m) => JsonSerializer.Serialize(m, SweepJsonContext.Default.SimMetrics) + "\n";

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
