using System.Globalization;
using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>One region of a world's colony extent, the generation that first observed it and the tick it was established.</summary>
public sealed record EstablishedExtent(GenerationSnapshot Snapshot, Tick Tick, ExtentRegion Region);

/// <summary>A persisted layout plan with the tick that recorded it.</summary>
/// <param name="Invalid">Marks a newest saved plan that no longer decodes or passes <c>Valid</c>: the world reads as holding no plan, so the colony replans.</param>
public sealed record LayoutPlanRecord(LayoutPlan? Plan, Tick Tick, bool Invalid);

/// <summary>A <c>ledger_rows</c> row as stored.</summary>
public sealed record StoredLedgerRow(string Section, int Position, long Tick, IReadOnlyDictionary<string, object?> Cells);

/// <summary>
/// One change of a concern's winning typed cause, the player-facing explanation history the concern-transitions
/// dashboard reads. Every text field is a bounded vocabulary value, never free text.
/// </summary>
public sealed record ConcernTransition
{
    /// <summary>Assigned on insert and only read back; it never repeats across restarts of the same database.</summary>
    public long Sequence { get; init; }

    /// <summary>The wall clock, unix milliseconds.</summary>
    public long WallMs { get; init; }

    public long Tick { get; init; }

    public string Concern { get; init; } = "";

    public string Verdict { get; init; } = "";

    /// <summary>The new <see cref="Policy.Cause"/>, empty when the concern cleared.</summary>
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
    /// <summary>How many <c>concern_transitions</c> rows the table keeps: each insert drops the oldest rows beyond it.</summary>
    public const int ConcernTransitionRetention = 8192;

    private const int MaxExtentRegions = 4096;

    private const string ConcernTransitionColumns = "sequence,wall_ms,tick,concern,verdict,cause,subject,method,previous_cause,held_ticks";

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
                    DomainException.Require(before.Origin < p.Origin || (before.Origin == p.Origin && string.CompareOrdinal(before.Facility, p.Facility) <= 0), "invalid established extent region");
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
    /// Makes the table hold exactly the rows of one ledger review at <paramref name="tick"/>. The table is a derived
    /// copy for dashboards: nothing reads it back.
    /// </summary>
    public void ReplaceLedgerRows(long tick, IReadOnlyList<LedgerRow> rows, CancellationToken cancellationToken) =>
        Run(
            tx =>
            {
                _ = tx.Execute("DELETE FROM ledger_rows");
                foreach (var r in rows)
                {
                    _ = tx.Execute(
                        "INSERT INTO ledger_rows(section,position,tick,cells) VALUES($section,$position,$tick,$cells)",
                        ("$section", r.Section),
                        ("$position", r.Position),
                        ("$tick", tick),
                        ("$cells", LooseJson.Write(r.Cells))
                    );
                }
            },
            cancellationToken
        );

    public Task ReplaceLedgerRowsAsync(long tick, IReadOnlyList<LedgerRow> rows, CancellationToken cancellationToken) => Task.Run(() => ReplaceLedgerRows(tick, rows, cancellationToken), cancellationToken);

    /// <summary>Every stored ledger row, by section then position. The controller never reads the table back; this is for tests and tools.</summary>
    public IReadOnlyList<StoredLedgerRow> LedgerRows(CancellationToken cancellationToken) =>
        Run(
            tx =>
                (IReadOnlyList<StoredLedgerRow>)
                    tx.Query("SELECT section,position,tick,cells FROM ledger_rows ORDER BY section,position", [], r => new StoredLedgerRow(r.GetString(0), r.GetInt32(1), r.GetInt64(2), LooseJson.ReadObject(r.GetString(3)))),
            cancellationToken
        );

    public Task<IReadOnlyList<StoredLedgerRow>> LedgerRowsAsync(CancellationToken cancellationToken) => Task.Run(() => LedgerRows(cancellationToken), cancellationToken);

    /// <summary>
    /// Appends rows in one transaction and trims the table to <see cref="ConcernTransitionRetention"/>. A cause outside
    /// the <see cref="Policy.Cause"/> vocabulary is refused before anything is written.
    /// </summary>
    public void RecordConcernTransitions(IReadOnlyList<ConcernTransition> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }
        foreach (var r in rows)
        {
            DomainException.Require(r.Concern.Length > 0 && r.Tick >= 0 && r.Held is null or >= 0, $"concern transition \"{r.Concern}\": concern and a non-negative tick and hold are required");
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
                        "INSERT INTO concern_transitions(wall_ms,tick,concern,verdict,cause,subject,method,previous_cause,held_ticks) VALUES($wall,$tick,$concern,$verdict,$cause,$subject,$method,$previous,$held)",
                        ("$wall", r.WallMs),
                        ("$tick", r.Tick),
                        ("$concern", r.Concern),
                        ("$verdict", r.Verdict),
                        ("$cause", r.Cause),
                        ("$subject", r.Subject),
                        ("$method", r.Method),
                        ("$previous", r.PreviousKnown ? r.Previous : null),
                        ("$held", r.Held)
                    );
                }
                var last = tx.Scalar("SELECT max(sequence) FROM concern_transitions");
                _ = tx.Execute("DELETE FROM concern_transitions WHERE sequence<=$floor", ("$floor", last - ConcernTransitionRetention));
            },
            cancellationToken
        );
    }

    public Task RecordConcernTransitionsAsync(IReadOnlyList<ConcernTransition> rows, CancellationToken cancellationToken) => Task.Run(() => RecordConcernTransitions(rows, cancellationToken), cancellationToken);

    /// <summary>Every retained concern transition, oldest first.</summary>
    public IReadOnlyList<ConcernTransition> ConcernTransitions(CancellationToken cancellationToken) =>
        Run(
            tx =>
                (IReadOnlyList<ConcernTransition>)
                    tx.Query(
                        "SELECT " + ConcernTransitionColumns + " FROM concern_transitions ORDER BY sequence",
                        [],
                        r => new ConcernTransition
                        {
                            Sequence = r.GetInt64(0),
                            WallMs = r.GetInt64(1),
                            Tick = r.GetInt64(2),
                            Concern = r.GetString(3),
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

    public Task<IReadOnlyList<ConcernTransition>> ConcernTransitionsAsync(CancellationToken cancellationToken) => Task.Run(() => ConcernTransitions(cancellationToken), cancellationToken);
}

/// <summary>The JSON of a loosely typed cell map (a ledger row): strings, numbers, booleans, null, nested objects and arrays.</summary>
internal static class LooseJson
{
    public static string Write(IReadOnlyDictionary<string, object> cells)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            WriteValue(w, cells);
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case ulong u:
                w.WriteNumberValue(u);
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            case float f:
                w.WriteNumberValue(f);
                break;
            case IReadOnlyDictionary<string, object> map:
                w.WriteStartObject();
                foreach (var (key, item) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(key);
                    WriteValue(w, item);
                }
                w.WriteEndObject();
                break;
            case System.Collections.IEnumerable items:
                w.WriteStartArray();
                foreach (var item in items)
                {
                    WriteValue(w, item);
                }
                w.WriteEndArray();
                break;
            case IFormattable f:
                w.WriteStringValue(f.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                w.WriteStringValue(value.ToString());
                break;
        }
    }

    public static IReadOnlyDictionary<string, object?> ReadObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object ? (Dictionary<string, object?>)ReadValue(document.RootElement)! : throw new StoreCorruptException("ledger cells are not an object");
    }

    private static object? ReadValue(JsonElement e) =>
        e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
            JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ReadValue(p.Value), StringComparer.Ordinal),
            JsonValueKind.Array => e.EnumerateArray().Select(ReadValue).ToList(),
            _ => null,
        };
}

internal static partial class StoreSchema
{
    internal static readonly SchemaPart TimelinePart = new(
        """
        CREATE TABLE colony_extent_events(colony TEXT NOT NULL, map_id INTEGER NOT NULL, ordinal INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), native_generation TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(colony,map_id,ordinal)) STRICT;
        CREATE TABLE colony_layout_plans(colony TEXT NOT NULL, map_id INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), plan TEXT NOT NULL, PRIMARY KEY(colony,map_id,tick)) STRICT;
        CREATE TABLE concern_transitions(sequence INTEGER PRIMARY KEY AUTOINCREMENT, wall_ms INTEGER NOT NULL, tick INTEGER NOT NULL CHECK(tick>=0), concern TEXT NOT NULL CHECK(concern<>''), verdict TEXT NOT NULL, cause TEXT NOT NULL, subject TEXT NOT NULL, method TEXT NOT NULL, previous_cause TEXT, held_ticks INTEGER CHECK(held_ticks IS NULL OR held_ticks>=0)) STRICT;
        CREATE TABLE ledger_rows(section TEXT NOT NULL CHECK(section<>''), position INTEGER NOT NULL CHECK(position>=0), tick INTEGER NOT NULL CHECK(tick>=0), cells TEXT NOT NULL CHECK(json_valid(cells)), PRIMARY KEY(section,position)) STRICT;
        """,
        [
            "SELECT colony,map_id,ordinal,tick,native_generation,payload FROM colony_extent_events LIMIT 0",
            "SELECT colony,map_id,tick,plan FROM colony_layout_plans LIMIT 0",
            "SELECT sequence,wall_ms,tick,concern,verdict,cause,subject,method,previous_cause,held_ticks FROM concern_transitions LIMIT 0",
            "SELECT section,position,tick,cells FROM ledger_rows LIMIT 0",
        ]
    );
}
