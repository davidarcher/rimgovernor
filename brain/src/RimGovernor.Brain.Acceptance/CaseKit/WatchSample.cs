using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance;

// The typed timeline of a serve-driven watch (#3410): what one sample read from the journal and the service, folded by
// the metrics and the colony outcome from these fields and written to the report in the timeline's own JSON shape.

/// <summary>
/// One Need's journal sample (<see cref="CaseStore.SampleNeedAsync"/>): its progress row and its owner's unretired
/// Methods' plan stages in the review's world. A key that is not a NeedId is never bound.
/// </summary>
public sealed record NeedSample
{
    /// <summary>The wall-clock stamp of the read (<see cref="WatchSample.Stamp"/>).</summary>
    public string At { get; init; } = "";

    public int MethodCount { get; init; }

    public long ReviewRevision { get; init; }

    public long ReviewTick { get; init; }

    /// <summary>The Need's progress row's blocked reason; null when the review carries no row for it.</summary>
    public BlockedReason? Blocked { get; init; }

    public bool ConcernBound { get; init; }

    /// <summary>The owner's Methods' plans; null when the Need is not bound.</summary>
    public IReadOnlyList<MethodSample>? Plans { get; init; }

    /// <summary>The sample as its timeline row: at, method_count, review_revision, review_tick, blocked, concern_bound, plans.</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject { { "at", At }, { "method_count", MethodCount }, { "review_revision", ReviewRevision }, { "review_tick", ReviewTick } };
        if (Blocked is { } blocked)
        {
            row.Add("blocked", blocked.Value);
        }

        row.Add("concern_bound", ConcernBound);
        return row.AddIfPresent("plans", Plans is null ? null : OutputJson.Array(Plans, plan => plan.ToJson()));
    }
}

/// <summary>One Method's plan in a <see cref="NeedSample"/>: its stage and kind counts, or the error loading it.</summary>
public sealed record MethodSample
{
    public string Plan { get; init; } = "";

    /// <summary>Why the plan could not be loaded; the other fields are empty then.</summary>
    public string? Error { get; init; }

    public string Method { get; init; } = "";

    public int Actions { get; init; }

    public IReadOnlyDictionary<string, int> Stages { get; init; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, int> Kinds { get; init; } = new SortedDictionary<string, int>(StringComparer.Ordinal);

    public IReadOnlyList<UnsuccessfulAction> Unsuccessful { get; init; } = [];

    /// <summary>Whether every action of the plan is still pending (a plan that failed to load is not).</summary>
    public bool AllPending => Error is null && Stages.All(stage => stage.Key == "pending" || stage.Value <= 0);

    /// <summary>The plan as its sample row: {plan, error}, or {plan, method, actions, stages, kinds, unsuccessful}.</summary>
    public JsonObject ToJson()
    {
        if (Error is not null)
        {
            return new JsonObject { { "plan", Plan }, { "error", Error } };
        }

        var row = new JsonObject { { "plan", Plan }, { "method", Method }, { "actions", Actions }, { "stages", Counts(Stages) }, { "kinds", Counts(Kinds) } };
        return row.AddIfPresent("unsuccessful", Unsuccessful.Count > 0 ? OutputJson.Array(Unsuccessful, u => new JsonObject { { "action", u.Action }, { "kind", u.Kind }, { "reason", u.Reason } }) : null);
    }

    private static JsonObject Counts(IReadOnlyDictionary<string, int> counts)
    {
        var map = new JsonObject();
        foreach (var (key, count) in counts)
        {
            map.Add(key, count);
        }

        return map;
    }
}

/// <summary>An action of a sampled plan that ended unsuccessful, in the wire spellings.</summary>
public sealed record UnsuccessfulAction(string Action, string Kind, string Reason);

/// <summary>
/// The service's colony census for one sample (GET /api/player/colony): the reply body as the timeline records it,
/// or the error reading it. <see cref="Census"/> is the body's typed facts, null when unreadable.
/// </summary>
public sealed record ColonyRead
{
    public JsonObject Body { get; init; } = [];

    public string? Error { get; init; }

    public PlayerColony? Census { get; init; }

    /// <summary>The census as its timeline block: the reply body, or {error}.</summary>
    public JsonObject ToJson() => Error is not null ? new JsonObject { { "error", Error } } : (JsonObject)Body.DeepClone();
}

/// <summary>
/// One timeline sample: the watched goal's <see cref="NeedSample"/> (or the error reading it), each extra goal's, the
/// live game tick and the colony census.
/// </summary>
public sealed record WatchSample
{
    public NeedSample? Need { get; init; }

    /// <summary>Why the watched goal could not be sampled; <see cref="Need"/> is null then.</summary>
    public string? Error { get; init; }

    /// <summary>The stamp of an error sample (a read sample carries its own).</summary>
    public string ErrorAt { get; init; } = "";

    /// <summary>The goal id the timeline reader names the top-level row by, a Concern id or a NeedId.</summary>
    public string Concern { get; init; } = "";

    public IReadOnlyList<(string Goal, NeedSample? Sample, string? Error)> Extras { get; init; } = [];

    public ulong? Tick { get; init; }

    public ColonyRead Colony { get; init; } = new();

    public string At => Need?.At ?? ErrorAt;

    public int MethodCount => Need?.MethodCount ?? 0;

    /// <summary>The wall-clock stamp a sample's <c>at</c> member carries (RFC 3339 UTC).</summary>
    public static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses <see cref="At"/>; the zero time when it is malformed (Go discards the parse error too).</summary>
    public DateTimeOffset AtTime() =>
        DateTimeOffset.TryParse(At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : default;

    /// <summary>The sample as its timeline row: the goal's sample and concern (or error and at), each extra goal, tick and colony.</summary>
    public JsonObject ToJson()
    {
        JsonObject row;
        if (Need is not null)
        {
            row = Need.ToJson();
            row.Add("concern", Concern);
        }
        else
        {
            row = new JsonObject { { "error", Error }, { "at", ErrorAt } };
        }

        foreach (var (goal, sample, error) in Extras)
        {
            // A goal listed twice keeps its last read, as the map the Go sample was did.
            _ = row.Remove(goal);
            row.Add(goal, sample is not null ? sample.ToJson() : new JsonObject { { "error", error } });
        }

        return row.AddIfPresent("tick", Tick).AddIfPresent("colony", Colony.ToJson());
    }
}
