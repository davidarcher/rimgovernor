using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Simulation.CombatMetrics;
using static RimGovernor.Brain.Domain.Fact;
using RimGovernor.Brain.Telemetry;
using FightMetrics = RimGovernor.Brain.Simulation.CombatMetrics.Metrics;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The bundle files a combat-fixture run records and the scoring of its raw reads.
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
        FightMetrics.Aggregate(fixture, staged, [.. reads.Select(ReadRows)], reads.Count > 0 ? StagingOf(reads[0]) : null);

    // What the sweep stamped on the first read; a bundle recorded before schema 2 carries none.
    private static MetricsStaging? StagingOf(Fx.LabFightRead first) =>
        first.Roles is { } roles && first.RaidPoints is { } points && first.DefenseCapacity is { } capacity
            ? new MetricsStaging(roles, points, capacity)
            : null;

    /// <summary>
    /// Stamps the staging on the first read of a run: the pawns' sides, the colonists' roles, the hostiles' raid points and
    /// the colonists' and turrets' defense capacity, so scoring the bundle later needs no game.
    /// </summary>
    internal static void Stamp(StagedFixture s, JsonObject first)
    {
        var sides = new JsonObject();
        foreach (var (id, side) in StagedSides(s).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sides.Add(id, side);
        }

        var roles = new JsonObject();
        foreach (var (id, role) in Roles(s).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            roles.Add(id, role);
        }

        first.Add("staged", sides);
        first.Add("roles", roles);
        first.Add("raidPoints", RaidPoints(s));
        first.Add("defenseCapacity", DefenseCapacity(s));
    }

    /// <summary>
    /// Each staged colonist's role: gunner with a ranged primary, melee with a melee primary or unarmed (the defense planner
    /// drafts every lab colonist that can fight), noncombatant when it is staged downed.
    /// </summary>
    internal static Dictionary<string, string> Roles(StagedFixture s)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < s.Fixture.Pawns.Count; i++)
        {
            if (s.Fixture.Pawns[i].Side != CombatFixtures.Colonist)
            {
                continue;
            }

            output[s.Pawns[i].Id] = s.Pawns[i].Primary switch
            {
                Fx.StagedPrimary.Ranged => ExposureRoles.Gunner,
                Fx.StagedPrimary.Melee => ExposureRoles.Melee,
                _ => s.Fixture.Pawns[i].Downed ? ExposureRoles.Noncombatant : ExposureRoles.Melee,
            };
        }

        return output;
    }

    /// <summary>The staged hostiles' summed kind combat power: the raid's size in points.</summary>
    internal static double RaidPoints(StagedFixture s)
    {
        var points = 0.0;
        for (var i = 0; i < s.Fixture.Pawns.Count; i++)
        {
            if (s.Fixture.Pawns[i].Side == CombatFixtures.Hostile)
            {
                points += s.Pawns[i].CombatPower;
            }
        }

        return points;
    }

    /// <summary>
    /// The staged defense in raid-point units, by the policy function the colony reads it with: each colonist that is not
    /// staged downed adds melee DPS (scaled by health) and ranged DPS, each powered turret its DPS. Every staged colonist
    /// counts as capable of violence.
    /// </summary>
    internal static double DefenseCapacity(StagedFixture s)
    {
        var defenders = new List<DefenderStrength>();
        for (var i = 0; i < s.Fixture.Pawns.Count; i++)
        {
            if (s.Fixture.Pawns[i].Side != CombatFixtures.Colonist)
            {
                continue;
            }

            var pawn = s.Pawns[i];
            defenders.Add(new DefenderStrength
            {
                Id = new PawnId(pawn.Id),
                Dead = false,
                Downed = s.Fixture.Pawns[i].Downed,
                ViolenceCapable = true,
                MeleePower = pawn.MeleeDps * pawn.Health,
                RangedDps = pawn.RangedDps,
            });
        }

        var turrets = s.Guns.Select(g => new DefenseTurretFacts { Id = g.Id, Powered = g.Powered, Dps = g.Dps, OutOfFuel = false }).ToList();
        return DefenseCapacityPolicy.DefenseCapacity(EquatableArray.From(defenders), EquatableArray.From(turrets));
    }

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

    /// <summary>
    /// The armed-stop check (#3551): a combat watch holds on a colonist downed or killed while its stops are armed, so a
    /// seed whose watch stopped the clock, whose fight downed or killed a colonist and none of whose stops was a combat
    /// event played the fight with its stops unarmed. Returns the flag naming the stops by event and the combat orders,
    /// or null; the rows stay in the seed's <see cref="CombatRowsFile"/>.
    /// </summary>
    internal static string? UnarmedStops(FightMetrics m, string path)
    {
        var byEvent = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var orders = 0;
        var combat = 0;
        foreach (var row in ServiceLogs.ReadLogs(path))
        {
            if (row.Kind == TelemetryKinds.ClockStopKind)
            {
                var kind = Text(row.Fields(), "event");
                byEvent[kind] = byEvent.GetValueOrDefault(kind) + 1;
            }
            else if (row.Kind == TelemetryKinds.CombatOrderKind)
            {
                orders++;
            }
            else if (row.Kind == TelemetryKinds.ClockRunKind && row.Fields().GetValueOrDefault("combat") is true)
            {
                combat++;
            }
        }

        var hurt = m.ColonistDowns + m.ColonistDeaths;
        if (byEvent.Count == 0 || hurt == 0 || byEvent.Keys.Any(CombatEventKinds.Contains))
        {
            return null;
        }

        var stops = string.Join(", ", byEvent.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key} {p.Value}")));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"combat stops not armed (#3551): {hurt} colonist downs or deaths, but no stop was a combat event (stops by event: {stops}); {combat} combat clock_run rows, {orders} combat_order rows; rows in {CombatRowsFile}");
    }

    // The clock_stop event names of the native combat events: the wire enum's C# name in lower snake case, as the clock
    // scheduler files them.
    private static readonly HashSet<string> CombatEventKinds =
        [.. Enum.GetValues<Clock.CombatEvent>().Select(e => Snake(e.ToString()))];

    private static string Snake(string pascal)
    {
        var b = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (char.IsUpper(pascal[i]) && i > 0)
            {
                _ = b.Append('_');
            }

            _ = b.Append(char.ToLowerInvariant(pascal[i]));
        }

        return b.ToString();
    }

    /// <summary>Whether a row is one <see cref="ScanLogs"/> or <see cref="UnarmedStops"/> reads: the bundle keeps only these.</summary>
    internal static bool CombatRow(LogRow row) =>
        row.Kind == TelemetryKinds.CombatOrder.Name || row.Kind == TelemetryKinds.ClockStop.Name || row.Kind == TelemetryKinds.Dispatch.Name
        || row.Kind == TelemetryKinds.ClockStep.Name || row.Kind == TelemetryKinds.ClockRun.Name
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

    /// <summary>The metrics as JSON (indented, damageDropped omitted at zero).</summary>
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
