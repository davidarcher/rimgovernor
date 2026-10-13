using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The draft, combat and movement acceptance helpers (Go <c>draft.go</c>): the pawn-row checks and the
/// EntityTarget a draft write carries.
/// </summary>
public static class Draft
{
    /// <summary>
    /// Identifies attempts and typed clock epochs in draft, combat and movement acceptance. It is not an authority
    /// ownership token; authority uses mode and native-generation continuity.
    /// </summary>
    public const string Controller = "native-draft-acceptance";

    /// <summary>
    /// Asserts an observations_list_pawns reply is a single complete, exact-match page whose context matches
    /// <paramref name="identity"/>, extracts the row for <paramref name="pawnId"/> (the first row when it is empty)
    /// and validates its snapshot invariants.
    /// </summary>
    public static JsonObject PawnRow(JsonObject reply, JsonObject identity, string pawnId)
    {
        var observed = Harness.Outcome(reply, "observed").Value;
        var observedContext = observed["context"].AsMap();
        if (!Dyn.DeepEqual(observedContext?["identity"], identity))
        {
            throw new AcceptanceException("pawn read context identity mismatch");
        }

        var rows = observed["pawns"].AsSlice();
        if (rows.Count == 0)
        {
            throw new AcceptanceException("expected at least one pawn row");
        }

        var row = rows[0].AsMap();
        var pawn = row?["pawn"].AsMap();
        if (pawnId.Length > 0)
        {
            // A multi-id filter returns one row per id; pick the requested one.
            row = null;
            pawn = null;
            foreach (var candidate in rows)
            {
                var r = candidate.AsMap();
                var p = r?["pawn"].AsMap();
                if (p?["id"].AsString() == pawnId)
                {
                    if (row is not null)
                    {
                        throw new AcceptanceException($"expected exactly one pawn for id \"{pawnId}\", found {rows.Count}");
                    }

                    row = r;
                    pawn = p;
                }
            }

            if (row is null)
            {
                throw new AcceptanceException($"expected pawn \"{pawnId}\" among {rows.Count} rows");
            }
        }

        var snapshot = row?["pawnSnapshot"].AsMap();
        if (snapshot?["entityId"].AsString() != pawn?["id"].AsString() || snapshot?["token"].AsString() is null or "")
        {
            throw new AcceptanceException($"pawn snapshot missing entityId/token: {snapshot.ToJson()}");
        }

        if (!Dyn.DeepEqual(snapshot?["context"], observed["context"]))
        {
            throw new AcceptanceException("pawn snapshot context does not match the observed read context");
        }

        if (row?["drafted"].AsBool() is null)
        {
            throw new AcceptanceException("pawn row drafted must be an explicit boolean");
        }

        return row;
    }

    /// <summary>Builds the EntityTarget{entityId, expectedSnapshotToken} for <paramref name="row"/>.</summary>
    public static JsonObject Target(JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new JsonObject
        {
            ["entityId"] = row["pawn"].AsMap()?["id"]?.DeepClone(),
            ["expectedSnapshotToken"] = row["pawnSnapshot"].AsMap()?["token"]?.DeepClone(),
        };
    }

    /// <summary>Asserts <paramref name="before"/> and <paramref name="after"/> describe the same draft control state (drafted flag and target/snapshot token).</summary>
    public static void SameControl(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var beforeDrafted = before["drafted"].AsBool() ?? false;
        var afterDrafted = after["drafted"].AsBool() ?? false;
        if (beforeDrafted != afterDrafted)
        {
            throw new AcceptanceException($"drafted flag changed: {Flag(beforeDrafted)} -> {Flag(afterDrafted)}");
        }

        if (!Dyn.DeepEqual(Target(before), Target(after)))
        {
            throw new AcceptanceException("pawn target/snapshot token changed unexpectedly");
        }
    }

    /// <summary>
    /// Asserts a test/b04f_setup external-order fixture reply was accepted and agrees with <paramref name="row"/>'s
    /// observed job.
    /// </summary>
    public static void ActualOrder(JsonObject external, JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(external);
        ArgumentNullException.ThrowIfNull(row);
        var success = external["success"].AsBool() ?? false;
        var accepted = external["accepted"].AsBool() ?? false;
        if (!success || !accepted)
        {
            throw new AcceptanceException($"external order fixture was not accepted: {external.ToJson()}");
        }

        var job = row["job"].AsMap();
        var jobId = external["jobId"];
        if (jobId is null)
        {
            if (external["jobDef"] is not null)
            {
                throw new AcceptanceException("expected no jobDef alongside a nil jobId");
            }

            if (job is not null && job.ContainsKey("loadId"))
            {
                throw new AcceptanceException("expected no loadId for a jobless external order");
            }

            if (job is not null && job.ContainsKey("defName"))
            {
                throw new AcceptanceException("expected no defName for a jobless external order");
            }

            if (job?["playerForced"].AsBool() ?? false)
            {
                throw new AcceptanceException("expected playerForced=false for a jobless external order");
            }

            return;
        }

        if (job?["loadId"].AsString() != Sprint(jobId))
        {
            throw new AcceptanceException("job.loadId does not match the external order's jobId");
        }

        if (job?["defName"].AsString() != external["jobDef"].AsString())
        {
            throw new AcceptanceException("job.defName does not match the external order's jobDef");
        }
    }

    private static string Flag(bool value) => value ? "true" : "false";

    // fmt.Sprint of a decoded JSON value: a string as it is, a number or boolean as its JSON text.
    private static string Sprint(JsonNode value) =>
        value is JsonValue json && json.TryGetValue<string>(out var text) ? text : value.ToJsonString().Trim('"');
}
