using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One startup/labor window: the fixture, variant and seed played under the service default. Every field is an
/// observation of that run, not a bound anything is held to (Go <c>startuplabor.Observation</c>).
/// </summary>
public sealed record LaborObservation
{
    public string Variant { get; init; } = "";

    public string Seed { get; init; } = "";

    public string Revision { get; init; } = "";

    /// <summary>The tick the first completed enclosure was observed; null (unknown) when none completed inside the window.</summary>
    public long? FirstEnclosure { get; init; }

    /// <summary>The tick shelter capacity covered every colonist; null (unknown) when it never did.</summary>
    public long? ShelterRecovery { get; init; }

    /// <summary>Review time per diagnosis class (<see cref="StartupLabor.BlockedTicks"/>).</summary>
    public IReadOnlyDictionary<string, long> Blocked { get; init; } = new Dictionary<string, long>();

    /// <summary>The run's idle accounting.</summary>
    public IdleAccount Idle { get; init; } = new();

    /// <summary>The observed window the run covered.</summary>
    public long WindowTicks { get; init; }

    /// <summary>
    /// Renders the observation artifact. It is labelled as an observation on the named revision: it diagnoses the run,
    /// it does not gate it and it is not a root cause.
    /// </summary>
    public JsonObject Row()
    {
        var blocked = new JsonObject();
        foreach (var (cls, ticks) in Blocked)
        {
            blocked[cls] = ticks;
        }

        return new JsonObject
        {
            ["kind"] = "startup_labor_observation",
            ["label"] = "observations on the tested revision; not a performance gate and not a proven root cause",
            ["variant"] = Variant,
            ["seed"] = Seed,
            ["revision"] = Revision,
            ["window_ticks"] = WindowTicks,
            ["idle"] = Idle.Row(),
            ["first_enclosure_tick"] = SampleJson.Num(FirstEnclosure is { } enclosure ? (double?)enclosure : null),
            ["shelter_recovery_tick"] = SampleJson.Num(ShelterRecovery is { } recovery ? (double?)recovery : null),
            ["blocked_ticks"] = blocked,
        };
    }
}

public static partial class StartupLabor
{
    /// <summary>
    /// Sums, per class, the review interval each diagnosis stood for: a diagnosis speaks for the ticks until the next
    /// review of the same goal, bounded the same way idle samples are. Diagnoses may arrive in any order; the last
    /// diagnosis of a goal credits nothing, as nothing observed its end.
    /// </summary>
    public static IReadOnlyDictionary<string, long> BlockedTicks(IReadOnlyList<LaborDiagnosis> diagnoses, long window, long maxGap)
    {
        ArgumentNullException.ThrowIfNull(diagnoses);
        if (maxGap <= 0)
        {
            maxGap = DefaultMaxGap;
        }

        var byConcern = new Dictionary<string, List<LaborDiagnosis>>(StringComparer.Ordinal);
        foreach (var d in diagnoses)
        {
            if (!byConcern.TryGetValue(d.Concern, out var run))
            {
                run = [];
                byConcern[d.Concern] = run;
            }

            run.Add(d);
        }

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var goal in byConcern.Keys.Order(StringComparer.Ordinal))
        {
            var run = byConcern[goal];
            for (var i = 0; i + 1 < run.Count; i++)
            {
                var span = run[i + 1].ReviewTick - run[i].ReviewTick;
                if (span <= 0)
                {
                    // A rewind or a repeated review tick credits nothing.
                    continue;
                }

                if (span > maxGap)
                {
                    span = maxGap;
                }

                if (window > 0 && span > window)
                {
                    span = window;
                }

                result[run[i].Class] = result.GetValueOrDefault(run[i].Class) + span;
            }
        }

        return result;
    }

    /// <summary>
    /// Reads pawn samples out of one <c>rimgovernor/observations_list_pawns</c> reply the service already made: the
    /// labor census every rounds takes. Nothing here calls native: the diagnosis rides the observation the review paid
    /// for, so no review grows a per-pawn RPC or a map scan.
    /// <para>
    /// Only free, living colonists are sampled; an animal or a prisoner is not the labor this epic accounts for. An
    /// absent job block is a pawn with nothing to do (protojson drops the empty message), unless the row also carries
    /// read issues, in which case the job was not read and the sample is unknown rather than idle.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PawnSample> SamplesFromPawnSnapshot(long tick, JsonNode? reply, bool restart)
    {
        var rows = FindPawnRows(reply, 0);
        var output = new List<PawnSample>();
        if (rows is null)
        {
            return output;
        }

        foreach (var raw in rows)
        {
            if (raw is not JsonObject row || row["dead"].AsBool() == true || row["freeColonist"].AsBool() != true)
            {
                continue;
            }

            var id = row["pawn"].Member("id").AsString();
            if (id.Length == 0)
            {
                continue;
            }

            var hasIssues = row["issues"].AsSlice().Count > 0;
            var job = row["job"] as JsonObject;
            output.Add(new PawnSample
            {
                Pawn = id,
                Tick = tick,
                Restart = restart,
                Drafted = row["drafted"].AsBool() == true,
                Downed = row["downed"].AsBool() == true,
                InBed = row["inBed"].AsBool() == true,
                Mental = row["mentalState"].AsString(),
                JobKnown = job is not null || !hasIssues,
                Job = job is null ? "" : job["defName"].AsString(),
            });
        }

        return output;
    }

    // Locates the PawnSnapshot's pawns array inside a reply: at the top level for a bare ListPawnsReply, or under
    // whatever envelope a log-ring row wrapped it in. The walk is depth-bounded so a large payload costs nothing to scan.
    private static JsonArray? FindPawnRows(JsonNode? value, int depth)
    {
        if (depth > 4 || value is not JsonObject map)
        {
            return null;
        }

        if (map["observed"] is JsonObject observed && observed["pawns"] is JsonArray rows)
        {
            return rows;
        }

        foreach (var key in new[] { "result", "structured", "reply", "data", "response" })
        {
            if (FindPawnRows(map[key], depth + 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
