using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Simulation.CombatMetrics;

// Port of Go internal/combatmetrics (metrics.go): scores a combat fight from plain per-tick rows, so the
// real-game combat lab and the simulator report fights with one definition. It knows nothing of the
// bridge read format or the logs. The JSON names are the Go field names: the committed combat lab
// baselines are Sweep files, the one persisted text in this package.

/// <summary>Sides a staged pawn fights on.</summary>
public static class Sides
{
    public const string Colonist = "colonist";
    public const string Hostile = "hostile";
}

/// <summary>
/// One combat-fixture run: evidence, not a gate. The committed combat lab baselines are this shape.
/// </summary>
public sealed record Metrics
{
    /// <summary>
    /// Bump when a field changes meaning, and re-record the baselines.
    /// </summary>
    public const int SchemaVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; init; } = SchemaVersion;

    [JsonPropertyName("fixture")]
    public string Fixture { get; init; } = "";

    /// <summary>Game ticks run after staging.</summary>
    [JsonPropertyName("ticks")]
    public int Ticks { get; init; }

    [JsonPropertyName("colonistDamage")]
    public double ColonistDamage { get; init; }

    [JsonPropertyName("colonistDowns")]
    public int ColonistDowns { get; init; }

    [JsonPropertyName("colonistDeaths")]
    public int ColonistDeaths { get; init; }

    [JsonPropertyName("enemyDowns")]
    public int EnemyDowns { get; init; }

    [JsonPropertyName("enemyDeaths")]
    public int EnemyDeaths { get; init; }

    [JsonPropertyName("enemyFled")]
    public int EnemyFled { get; init; }

    /// <summary>The first hit either way; -1 without contact.</summary>
    [JsonPropertyName("firstContactTick")]
    public int FirstContactTick { get; init; } = -1;

    /// <summary>The read where one side was all down, dead or gone; -1 while unresolved.</summary>
    [JsonPropertyName("resolvedTick")]
    public int ResolvedTick { get; init; } = -1;

    /// <summary>ResolvedTick - FirstContactTick, -1 when the run ended unresolved or without contact.</summary>
    [JsonPropertyName("contactToResolutionTicks")]
    public int ContactToResolution { get; init; } = -1;

    /// <summary>colonist, hostile or "" unresolved.</summary>
    [JsonPropertyName("winner")]
    public string Winner { get; init; } = "";

    [JsonPropertyName("friendlyFireHits")]
    public int FriendlyFireHits { get; init; }

    [JsonPropertyName("damageDropped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DamageDropped { get; init; }

    /// <summary>
    /// Combat orders from the log rows: combat_order rows and the routine defense planner's actions, issued
    /// once one completes, refused when one only ever carried a refusal.
    /// </summary>
    [JsonPropertyName("ordersIssued")]
    public int OrdersIssued { get; init; }

    [JsonPropertyName("ordersRefused")]
    public int OrdersRefused { get; init; }

    /// <summary>The p95 of the combat stops' resume_latency_ms, -1 when no planner served the run.</summary>
    [JsonPropertyName("stepLatencyP95Ms")]
    public long StepLatencyP95Ms { get; init; } = -1;

    /// <summary>The metrics of a run that has no reads yet (Go <c>New</c>).</summary>
    public static Metrics New(string fixture) => new() { Fixture = fixture };

    /// <summary>
    /// Folds a run's reads (tick order) into metrics. <paramref name="staged"/> are the pawn ids and sides the
    /// staging placed: a staged pawn missing from a later read left the map, which for a hostile is fled.
    /// </summary>
    public static Metrics Aggregate(string fixture, IReadOnlyDictionary<string, string> staged, IReadOnlyList<MetricsRead> reads)
    {
        var m = New(fixture);
        if (reads.Count == 0)
        {
            return m;
        }
        var start = reads[0].Tick;
        var ticks = 0;
        var resolvedTick = -1;
        var winner = "";
        for (var i = 0; i < reads.Count; i++)
        {
            var read = reads[i];
            ticks = read.Tick - start;
            // The staging read resolves nothing: raiders still in their drop pods are not on the map yet
            // and would read as a side already gone.
            if (resolvedTick < 0 && i > 0)
            {
                var pawns = Index(read.Pawns);
                var side = Resolved(staged, pawns);
                if (side.Length > 0)
                {
                    resolvedTick = DecisiveHit(staged, pawns, side, read.Damage, read.Tick);
                    winner = side;
                }
            }
        }
        var last = reads[^1];
        var final = Index(last.Pawns);
        // The native read omits every dead pawn, a colonist as much as a hostile (Verse.MapPawns.AllPawns
        // drops the dead), so the ledger is the only record of a death. Sweeps recorded before #2989
        // counted a missing colonist as neither dead nor down, so their colonistDeaths are 0 and their
        // colonistDowns count only the colonists that were still lying there.
        var killed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in last.Damage)
        {
            if (h.Dead)
            {
                killed.Add(h.Victim);
            }
        }
        int colonistDeaths = 0, colonistDowns = 0, enemyDeaths = 0, enemyDowns = 0, enemyFled = 0;
        foreach (var (id, side) in staged)
        {
            var present = final.TryGetValue(id, out var row);
            if (side == Sides.Colonist && (row.Dead || killed.Contains(id)))
            {
                colonistDeaths++;
            }
            else if (side == Sides.Colonist && row.Downed)
            {
                colonistDowns++;
            }
            else if (side == Sides.Hostile && (row.Dead || killed.Contains(id)))
            {
                enemyDeaths++;
            }
            else if (side == Sides.Hostile && row.Downed)
            {
                enemyDowns++;
            }
            else if (side == Sides.Hostile && (!present || row.Fleeing))
            {
                enemyFled++;
            }
        }
        // The ledger is cumulative since the staging: the last read holds it all.
        var firstContact = -1;
        var colonistDamage = 0.0;
        var friendlyFire = 0;
        foreach (var h in last.Damage)
        {
            if (firstContact < 0 || h.Tick < firstContact)
            {
                firstContact = h.Tick;
            }
            if (h.VictimSide == Sides.Colonist)
            {
                colonistDamage += h.Dealt;
                if (h.InstigatorSide == Sides.Colonist)
                {
                    friendlyFire++;
                }
            }
        }
        colonistDamage = Math.Round(colonistDamage * 10, MidpointRounding.AwayFromZero) / 10;
        var contactToResolution = firstContact >= 0 && resolvedTick >= firstContact ? resolvedTick - firstContact : -1;
        return m with
        {
            Ticks = ticks,
            ResolvedTick = resolvedTick,
            Winner = winner,
            ColonistDeaths = colonistDeaths,
            ColonistDowns = colonistDowns,
            EnemyDeaths = enemyDeaths,
            EnemyDowns = enemyDowns,
            EnemyFled = enemyFled,
            FirstContactTick = firstContact,
            ColonistDamage = colonistDamage,
            FriendlyFireHits = friendlyFire,
            DamageDropped = Math.Max(0, last.DamageDropped),
            ContactToResolution = contactToResolution,
        };
    }

    private static Dictionary<string, MetricsPawn> Index(IReadOnlyList<MetricsPawn> pawns)
    {
        var result = new Dictionary<string, MetricsPawn>(pawns.Count, StringComparer.Ordinal);
        foreach (var p in pawns)
        {
            result[p.Id] = p;
        }
        return result;
    }

    // Narrows a resolution first seen on a read at tick to the ledger hit that downed or killed the loser's
    // last standing pawn: a served run reads only once after its window. A loser that left the map or went
    // down without a hit (bleeding) keeps the read's tick.
    private static int DecisiveHit(IReadOnlyDictionary<string, string> staged, Dictionary<string, MetricsPawn> pawns, string winner, IReadOnlyList<MetricsHit> damage, int tick)
    {
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in damage)
        {
            if ((h.Downed || h.Dead) && h.Tick <= tick && (!first.TryGetValue(h.Victim, out var prev) || h.Tick < prev))
            {
                first[h.Victim] = h.Tick;
            }
        }
        var last = -1;
        foreach (var (id, side) in staged)
        {
            if (side == winner)
            {
                continue;
            }
            var ok = first.TryGetValue(id, out var at);
            if (!ok || !pawns.ContainsKey(id))
            {
                return tick;
            }
            last = Math.Max(last, at);
        }
        return last < 0 ? tick : last;
    }

    // Names the side left standing once every staged pawn of the other side is down, dead or off the map;
    // "" while both fight.
    private static string Resolved(IReadOnlyDictionary<string, string> staged, Dictionary<string, MetricsPawn> pawns)
    {
        var colonists = 0;
        var hostiles = 0;
        foreach (var (id, side) in staged)
        {
            if (!pawns.TryGetValue(id, out var p))
            {
                continue;
            }
            // An undrafted colonist flees raiders and comes back; only a fleeing hostile has left the fight.
            if (!p.Dead && !p.Downed && !(p.Fleeing && side == Sides.Hostile))
            {
                if (side == Sides.Hostile)
                {
                    hostiles++;
                }
                else if (side == Sides.Colonist)
                {
                    colonists++;
                }
            }
        }
        if (hostiles == 0)
        {
            return Sides.Colonist;
        }
        return colonists == 0 ? Sides.Hostile : "";
    }
}

/// <summary>One pawn's state in a read (Go <c>combatmetrics.Pawn</c>).</summary>
public readonly record struct MetricsPawn(string Id, bool Dead = false, bool Downed = false, bool Fleeing = false);

/// <summary>
/// One damage ledger row of a read (Go <c>combatmetrics.Hit</c>). Downed and Dead say the hit left the victim
/// down or dead.
/// </summary>
public readonly record struct MetricsHit(
    int Tick,
    string Victim,
    string VictimSide,
    string Instigator,
    string InstigatorSide,
    double Dealt,
    bool Downed,
    bool Dead);

/// <summary>
/// One read of the fight: the pawns on the map, the damage ledger cumulative since the staging, and the
/// ledger rows dropped for size (Go <c>combatmetrics.Read</c>).
/// </summary>
public sealed record MetricsRead(int Tick)
{
    public IReadOnlyList<MetricsPawn> Pawns { get; init; } = [];

    public IReadOnlyList<MetricsHit> Damage { get; init; } = [];

    public int DamageDropped { get; init; }
}
