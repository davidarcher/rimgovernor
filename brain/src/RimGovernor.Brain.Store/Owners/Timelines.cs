using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>One region of a world's colony extent, the generation that first observed it and the tick it was established.</summary>
public sealed record EstablishedExtent(GenerationSnapshot Snapshot, Tick Tick, ExtentRegion Region);

/// <summary>A persisted layout plan with the tick that recorded it.</summary>
/// <param name="Invalid">Marks a newest saved plan that no longer decodes or passes <c>Valid</c>: the world reads as holding no plan, so the colony replans.</param>
public sealed record LayoutPlanRecord(LayoutPlan? Plan, Tick Tick, bool Invalid);

/// <summary>
/// One change of a Need's winning typed cause, the player-facing explanation history explain reads. Every text field
/// is a bounded vocabulary value, never free text.
/// </summary>
public sealed record NeedTransition
{
    /// <summary>Assigned on insert and only read back; it never repeats across restarts of the same database.</summary>
    public long Sequence { get; init; }

    /// <summary>The wall clock, unix milliseconds.</summary>
    public long WallMs { get; init; }

    public long Tick { get; init; }

    /// <summary>
    /// The filing key: a Need's id (<c>family/kind[/subject]</c>), or the own name of a family or planner that files
    /// beside its Needs, or a ledger purpose's key.
    /// </summary>
    public string Need { get; init; } = "";

    public string Verdict { get; init; } = "";

    /// <summary>The new <see cref="Policy.Cause"/>, empty when the cause cleared.</summary>
    public string Cause { get; init; } = "";

    public string Subject { get; init; } = "";

    public string Method { get; init; } = "";

    /// <summary>False for the first transition seen after a restart; <see cref="Previous"/> is then empty.</summary>
    public bool PreviousKnown { get; init; }

    public string Previous { get; init; } = "";

    /// <summary>The game ticks the previous cause stood, null when unknown.</summary>
    public long? Held { get; init; }
}

public sealed partial class Store
{
    /// <summary>How many <c>need_transitions</c> rows the table keeps: each insert drops the oldest rows beyond it.</summary>
    public const int NeedTransitionRetention = 8192;

    private const int MaxExtentRegions = 4096;

    private const string NeedTransitionColumns = "sequence,wall_ms,tick,need,verdict,cause,subject,method,previous_cause,held_ticks";

    private static void ExtentScope(GenerationSnapshot s, Tick tick)
    {
        s.Validate();
        DomainException.Require(tick.Value >= 0, "negative colony extent tick");
    }

    private static string ExtentRegionEncode(ExtentRegion region)
    {
        foreach (var c in region.Cells)
        {
            DomainException.Require(c.Provenance.Count > 0, "invalid established extent region");
            for (var i = 0; i < c.Provenance.Count; i++)
            {
                var p = c.Provenance[i];
                DomainException.Require(p.Origin is ExtentOrigin.Facility or ExtentOrigin.EnclosedInterior or ExtentOrigin.Corridor or ExtentOrigin.Margin, "invalid established extent region");
                if (i > 0)
                {
                    var before = c.Provenance[i - 1];
                    DomainException.Require(string.CompareOrdinal(Wire.Name(before.Origin), Wire.Name(p.Origin)) < 0 || (before.Origin == p.Origin && string.CompareOrdinal(before.Facility, p.Facility) <= 0), "invalid established extent region");
                }
            }
        }
        return StoreJson.Serialize(region);
    }

    /// <summary>
    /// Appends regions observed established at <paramref name="tick"/> under <paramref name="snapshot"/>. A region
    /// already in the world's journal is not repeated; the count returned is the number of new entries.
    /// </summary>
    public int EstablishColonyExtent(GenerationSnapshot snapshot, Tick tick, IReadOnlyList<ExtentRegion> regions, CancellationToken cancellationToken)
    {
        ExtentScope(snapshot, tick);
        DomainException.Require(regions.Count <= MaxExtentRegions, "too many established extent regions");
        var encoded = regions.Select(ExtentRegionEncode).ToList();
        return Run(
            tx =>
            {
                var seen = tx.Query(
                        "SELECT payload FROM colony_extent_events WHERE colony=$colony AND map_id=$map AND tick<=$tick ORDER BY ordinal",
                        [("$colony", snapshot.Colony), ("$map", snapshot.Map), ("$tick", tick)],
                        r => r.GetString(0)
                    )
                    .ToHashSet(StringComparer.Ordinal);
                var added = 0;
                foreach (var data in encoded)
                {
                    if (!seen.Add(data))
                    {
                        continue;
                    }
                    var ordinal = tx.Scalar("SELECT coalesce(max(ordinal),0)+1 FROM colony_extent_events WHERE colony=$colony AND map_id=$map", ("$colony", snapshot.Colony), ("$map", snapshot.Map));
                    _ = tx.Execute(
                        "INSERT INTO colony_extent_events(colony,map_id,ordinal,tick,native_generation,payload) VALUES($colony,$map,$ordinal,$tick,$generation,$payload)",
                        ("$colony", snapshot.Colony),
                        ("$map", snapshot.Map),
                        ("$ordinal", ordinal),
                        ("$tick", tick),
                        ("$generation", snapshot.Native),
                        ("$payload", data)
                    );
                    added++;
                }
                return added;
            },
            cancellationToken
        );
    }

    public Task<int> EstablishColonyExtentAsync(GenerationSnapshot snapshot, Tick tick, IReadOnlyList<ExtentRegion> regions, CancellationToken cancellationToken) =>
        Task.Run(() => EstablishColonyExtent(snapshot, tick, regions, cancellationToken), cancellationToken);

    /// <summary>The world's established regions visible at <paramref name="tick"/>, oldest first, each with the generation that first observed it.</summary>
    public IReadOnlyList<EstablishedExtent> EstablishedColonyExtent(GenerationSnapshot snapshot, Tick tick, CancellationToken cancellationToken)
    {
        ExtentScope(snapshot, tick);
        return Run(
            tx =>
                (IReadOnlyList<EstablishedExtent>)
                    tx.Query(
                            "SELECT tick,native_generation,payload FROM colony_extent_events WHERE colony=$colony AND map_id=$map AND tick<=$tick ORDER BY ordinal",
                            [("$colony", snapshot.Colony), ("$map", snapshot.Map), ("$tick", tick)],
                            r => (Tick: new Tick(r.GetInt64(0)), Generation: new NativeGeneration(r.UnsignedText(1)), Payload: r.GetString(2))
                        )
                        .Select(e => new EstablishedExtent(snapshot with { Native = e.Generation }, e.Tick, StoreJson.Deserialize<ExtentRegion>(e.Payload)))
                        .ToList(),
            cancellationToken
        );
    }

    public Task<IReadOnlyList<EstablishedExtent>> EstablishedColonyExtentAsync(GenerationSnapshot snapshot, Tick tick, CancellationToken cancellationToken) =>
        Task.Run(() => EstablishedColonyExtent(snapshot, tick, cancellationToken), cancellationToken);

    /// <summary>Records a layout plan for the world at <paramref name="tick"/>. The caller records only a new plan or a deliberate replan.</summary>
    public void RecordLayoutPlan(GenerationSnapshot snapshot, Tick tick, LayoutPlan plan, CancellationToken cancellationToken)
    {
        ExtentScope(snapshot, tick);
        DomainException.Require(plan.Valid(), "invalid layout plan");
        var encoded = StoreJson.Serialize(plan);
        Run(
            tx =>
                tx.Execute(
                    "INSERT OR REPLACE INTO colony_layout_plans(colony,map_id,tick,plan) VALUES($colony,$map,$tick,$plan)",
                    ("$colony", snapshot.Colony),
                    ("$map", snapshot.Map),
                    ("$tick", tick),
                    ("$plan", encoded)
                ),
            cancellationToken
        );
    }

    public Task RecordLayoutPlanAsync(GenerationSnapshot snapshot, Tick tick, LayoutPlan plan, CancellationToken cancellationToken) =>
        Task.Run(() => RecordLayoutPlan(snapshot, tick, plan, cancellationToken), cancellationToken);

    /// <summary>The layout plan the world holds at <paramref name="tick"/>; null when it holds none.</summary>
    public LayoutPlanRecord? LayoutPlan(GenerationSnapshot snapshot, Tick tick, CancellationToken cancellationToken)
    {
        ExtentScope(snapshot, tick);
        return Run(
            tx =>
            {
                var (row, found) = tx.QueryFirst(
                    "SELECT tick,plan FROM colony_layout_plans WHERE colony=$colony AND map_id=$map AND tick<=$tick ORDER BY tick DESC LIMIT 1",
                    [("$colony", snapshot.Colony), ("$map", snapshot.Map), ("$tick", tick)],
                    r => (Tick: new Tick(r.GetInt64(0)), Plan: r.GetString(1))
                );
                if (!found)
                {
                    return null;
                }
                try
                {
                    var plan = StoreJson.Deserialize<LayoutPlan>(row.Plan);
                    return plan.Valid() ? new LayoutPlanRecord(plan, row.Tick, false) : new LayoutPlanRecord(null, row.Tick, true);
                }
                catch (StoreCorruptException)
                {
                    // A plan written before a wire change reads as no plan, so the next survey derives a fresh one.
                    return new LayoutPlanRecord(null, row.Tick, true);
                }
            },
            cancellationToken
        );
    }

    public Task<LayoutPlanRecord?> LayoutPlanAsync(GenerationSnapshot snapshot, Tick tick, CancellationToken cancellationToken) => Task.Run(() => LayoutPlan(snapshot, tick, cancellationToken), cancellationToken);

    /// <summary>
    /// Appends rows in one transaction and trims the table to <see cref="NeedTransitionRetention"/>. A cause outside
    /// the <see cref="Policy.Cause"/> vocabulary is refused before anything is written.
    /// </summary>
    public void RecordNeedTransitions(IReadOnlyList<NeedTransition> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }
        foreach (var r in rows)
        {
            DomainException.Require(r.Need.Length > 0 && r.Tick >= 0 && r.Held is null or >= 0, $"need transition \"{r.Need}\": a key and a non-negative tick and hold are required");
            foreach (var cause in new[] { r.Cause, r.Previous })
            {
                if (cause.Length > 0)
                {
                    new Cause(cause).Validate();
                }
            }
        }
        Run(
            tx =>
            {
                foreach (var r in rows)
                {
                    _ = tx.Execute(
                        "INSERT INTO need_transitions(wall_ms,tick,need,verdict,cause,subject,method,previous_cause,held_ticks) VALUES($wall,$tick,$need,$verdict,$cause,$subject,$method,$previous,$held)",
                        ("$wall", r.WallMs),
                        ("$tick", r.Tick),
                        ("$need", r.Need),
                        ("$verdict", r.Verdict),
                        ("$cause", r.Cause),
                        ("$subject", r.Subject),
                        ("$method", r.Method),
                        ("$previous", r.PreviousKnown ? r.Previous : null),
                        ("$held", r.Held)
                    );
                }
                var last = tx.Scalar("SELECT max(sequence) FROM need_transitions");
                _ = tx.Execute("DELETE FROM need_transitions WHERE sequence<=$floor", ("$floor", last - NeedTransitionRetention));
            },
            cancellationToken
        );
    }

    public Task RecordNeedTransitionsAsync(IReadOnlyList<NeedTransition> rows, CancellationToken cancellationToken) => Task.Run(() => RecordNeedTransitions(rows, cancellationToken), cancellationToken);

    /// <summary>Every retained Need transition, oldest first.</summary>
    public IReadOnlyList<NeedTransition> NeedTransitions(CancellationToken cancellationToken) =>
        Run(
            tx =>
                (IReadOnlyList<NeedTransition>)
                    tx.Query(
                        "SELECT " + NeedTransitionColumns + " FROM need_transitions ORDER BY sequence",
                        [],
                        r => new NeedTransition
                        {
                            Sequence = r.GetInt64(0),
                            WallMs = r.GetInt64(1),
                            Tick = r.GetInt64(2),
                            Need = r.GetString(3),
                            Verdict = r.GetString(4),
                            Cause = r.GetString(5),
                            Subject = r.GetString(6),
                            Method = r.GetString(7),
                            PreviousKnown = !r.IsDBNull(8),
                            Previous = r.IsDBNull(8) ? "" : r.GetString(8),
                            Held = r.IsDBNull(9) ? null : r.GetInt64(9),
                        }
                    ),
            cancellationToken
        );

    public Task<IReadOnlyList<NeedTransition>> NeedTransitionsAsync(CancellationToken cancellationToken) => Task.Run(() => NeedTransitions(cancellationToken), cancellationToken);
}

internal static class TimelinesSchema
{
    internal static readonly SchemaPart TimelinePart = new(
        """
        CREATE TABLE colony_extent_events(colony TEXT NOT NULL, map_id INTEGER NOT NULL, ordinal INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), native_generation TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(colony,map_id,ordinal)) STRICT;
        CREATE TABLE colony_layout_plans(colony TEXT NOT NULL, map_id INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), plan TEXT NOT NULL, PRIMARY KEY(colony,map_id,tick)) STRICT;
        CREATE TABLE need_transitions(sequence INTEGER PRIMARY KEY AUTOINCREMENT, wall_ms INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), need TEXT NOT NULL CHECK(need<>''), verdict TEXT NOT NULL, cause TEXT NOT NULL, subject TEXT NOT NULL, method TEXT NOT NULL, previous_cause TEXT, held_ticks INTEGER CHECK(held_ticks IS NULL OR held_ticks>=0)) STRICT;
        """,
        [
            "SELECT colony,map_id,ordinal,tick,native_generation,payload FROM colony_extent_events LIMIT 0",
            "SELECT colony,map_id,tick,plan FROM colony_layout_plans LIMIT 0",
            "SELECT sequence,wall_ms,tick,need,verdict,cause,subject,method,previous_cause,held_ticks FROM need_transitions LIMIT 0",
        ]
    );
}
