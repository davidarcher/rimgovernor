using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>pawn/reads</c> (Go <c>reads.go</c>): typed pawn rows from a fresh debug game, asserting the typed pawn read facts
/// against each other.
/// </summary>
internal static class PawnReads
{
    private static readonly string[] CoreFlags = ["colonist", "freeColonist", "prisoner", "animal", "humanlike", "mechanoid", "tame", "wild", "hostile", "dead", "downed", "drafted"];

    private static readonly string[] Sections = ["needs", "health", "equipment", "biography", "settings", "social", "animalState"];

    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/reads",
        Scope = "Fresh native pawn read facts, exact filters, explicit detail presence, bounded refusals and paused identity/tick invariance. Draft-control snapshot/claim read validation only; no pawn operation or health/settings CAS acceptance.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>Every optional detail family switched off, the shape the bounded reads of this case request.</summary>
    private static JsonObject NoDetails() => new()
    {
        ["needs"] = false,
        ["health"] = false,
        ["equipment"] = false,
        ["biography"] = false,
        ["settings"] = false,
        ["social"] = false,
        ["animals"] = false,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;

        // The lab holds only its colonists: add an animal so the census reads a non-colonist pawn too.
        var c = LabWorld.LabMapSize / 2;
        _ = await DebugGame.LabSpawnAsync(h, new LabThing { Def = "Muffalo", X = c + 4, Z = c, Unowned = true }, cancellationToken).ConfigureAwait(false);
        if (!Dyn.Contains(s.Names, "rimgovernor/observations_list_pawns"))
        {
            throw new AcceptanceException("missing rimgovernor/observations_list_pawns in discovery");
        }

        var identityBefore = await h.WireAsync("identity-before", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(identityBefore, "loaded");
        if (loaded["paused"].AsBool() != true)
        {
            throw new AcceptanceException("fresh debug game did not start paused");
        }

        var loadedContext = loaded["context"];
        var identity = loadedContext.Member("identity");
        var scope = new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity?.DeepClone() } };

        async Task<(List<JsonObject> Rows, JsonObject Observed)> Read(string label, JsonObject fields)
        {
            var reply = await h.WireAsync(label, "observations_list_pawns", Dyn.Merge(scope, fields), cancellationToken).ConfigureAwait(false);
            JsonObject observed;
            try
            {
                observed = Harness.Outcome(reply, "observed").Value;
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{label}: {e.Message}", e);
            }

            if (observed["context"] is not { } observedContext || !Dyn.DeepEqual(observedContext, loadedContext))
            {
                throw new AcceptanceException($"{label}: context drifted mid-run");
            }

            var rows = observed["pawns"].AsSlice().Select(raw => raw.AsMap() ?? []).ToList();
            foreach (var row in rows)
            {
                try
                {
                    DraftControl(row, observed["context"]);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"{label}: {e.Message}", e);
                }
            }

            return (rows, observed);
        }

        // The unfiltered, all-details default request (details omitted entirely) asks the native tool to populate every
        // detail family for every pawn on the map, per its documented default. The reply carries no envelope size cap any
        // more; a LIMIT_EXCEEDED refusal from a per-collection bound is still tolerated here, and any other unavailable
        // reply is rejected (contracts/native-operation-variants.md).
        var defaultReply = await h.WireAsync("default-pawns", "observations_list_pawns", scope, cancellationToken).ConfigureAwait(false);
        if (!defaultReply.ContainsKey("observed"))
        {
            var reason = PawnSupport.UnavailableReason(defaultReply);
            if (reason != "UNAVAILABLE_REASON_LIMIT_EXCEEDED")
            {
                throw new AcceptanceException($"default-pawns: expected observed or UNAVAILABLE_REASON_LIMIT_EXCEEDED, got \"{reason}\"");
            }
        }

        // Prove the happy path with a bounded, filtered read over the same population: every detail family explicitly
        // disabled keeps the reply well under the envelope while still exercising the core pawn facts and the CAS-snapshot
        // invariants that DraftControl checks.
        var (baseline, _) = await Read("default-pawns-bounded", new JsonObject { ["details"] = NoDetails() }).ConfigureAwait(false);
        if (baseline.Count <= 3)
        {
            throw new AcceptanceException($"expected more than 3 fresh pawns, found {baseline.Count}");
        }

        RequireCore(baseline);

        var (colonists, _) = await Read("colonists", new JsonObject { ["filter"] = new JsonObject { ["colonist"] = true, ["humanlike"] = true, ["animal"] = false } }).ConfigureAwait(false);
        if (colonists.Count != 3)
        {
            throw new AcceptanceException($"expected exactly 3 colonists, found {colonists.Count}");
        }

        RequireDetails(colonists);

        var target = colonists[0]["pawn"].Member("id").AsString();
        var (exact, _) = await Read("exact-id", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(target), ["colonist"] = true, ["animal"] = false } }).ConfigureAwait(false);
        if (exact.Count != 1)
        {
            throw new AcceptanceException("exact id filter did not return exactly one pawn");
        }

        var (contradictory, _) = await Read("contradictory-filter", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(target), ["colonist"] = false } }).ConfigureAwait(false);
        if (contradictory.Count != 0)
        {
            throw new AcceptanceException("contradictory filter unexpectedly matched a pawn");
        }

        var (unknown, _) = await Read("unknown-id", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray("missing-pawn-id") } }).ConfigureAwait(false);
        if (unknown.Count != 0)
        {
            throw new AcceptanceException("unknown id filter unexpectedly matched a pawn");
        }

        // As with default-pawns above, omitting details requests every detail family for every matched pawn; a fresh
        // map's animal population can carry enough hediff/needs/social data. Only pawnSnapshot is checked below, which
        // does not depend on details, so keep this small the same way.
        var (animals, _) = await Read("animals", new JsonObject { ["filter"] = new JsonObject { ["colonist"] = false, ["animal"] = true }, ["details"] = NoDetails() }).ConfigureAwait(false);
        if (animals.Count == 0)
        {
            throw new AcceptanceException("expected at least one animal in a fresh tribal colony");
        }

        foreach (var row in animals)
        {
            try
            {
                NativeAssert.RequireSnapshot(row["pawnSnapshot"]);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"animal row missing exact target snapshot: {e.Message}", e);
            }
        }

        var (disabled, _) = await Read("details-disabled", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(target) }, ["details"] = NoDetails() }).ConfigureAwait(false);
        if (disabled.Count != 1)
        {
            throw new AcceptanceException("details-disabled did not return exactly one pawn");
        }

        foreach (var section in Sections)
        {
            if (disabled[0].ContainsKey(section))
            {
                throw new AcceptanceException($"disabled section {section} unexpectedly present");
            }
        }

        // drafted:false,downed:false can match nearly the whole population; only the drafted/downed booleans are checked
        // below, so keep this small like default-pawns above.
        var (knownFalse, _) = await Read("known-false", new JsonObject { ["filter"] = new JsonObject { ["drafted"] = false, ["downed"] = false }, ["details"] = NoDetails() }).ConfigureAwait(false);
        foreach (var row in knownFalse)
        {
            if (row["drafted"].AsBool() == true || row["downed"].AsBool() == true)
            {
                throw new AcceptanceException("known-false filter returned a drafted or downed pawn");
            }
        }

        foreach (var (label, change) in new (string, JsonObject)[]
        {
            ("duplicate-id", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(target, target) } }),
            ("negative-distance", new JsonObject { ["filter"] = new JsonObject { ["withinColonistDistance"] = -1 } }),
        })
        {
            var reply = await h.WireAsync(label, "observations_list_pawns", Dyn.Merge(scope, change), cancellationToken).ConfigureAwait(false);
            var code = WallCensus.FailureCode(reply);
            if (code != "FAILURE_CODE_INVALID_REQUEST")
            {
                throw new AcceptanceException($"{label}: expected FAILURE_CODE_INVALID_REQUEST, got \"{code}\"");
            }
        }

        var staleIdentity = Dyn.Merge(identity.AsMap(), new JsonObject { ["loadToken"] = "stale-load" });
        var staleScope = new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = staleIdentity } };
        var staleReply = await h.WireAsync("stale-identity", "observations_list_pawns", staleScope, cancellationToken).ConfigureAwait(false);
        if (WallCensus.FailureCode(staleReply) != "FAILURE_CODE_STALE_IDENTITY")
        {
            throw new AcceptanceException("expected FAILURE_CODE_STALE_IDENTITY for a stale load token");
        }

        var identityAfter = await h.WireAsync("identity-after", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, finalLoaded) = Harness.Outcome(identityAfter, "loaded");
        if (finalLoaded["paused"].AsBool() != true)
        {
            throw new AcceptanceException("game unexpectedly resumed during the read pass");
        }

        if (!Dyn.DeepEqual(finalLoaded["context"], loadedContext))
        {
            throw new AcceptanceException("identity/tick changed during a read-only pass");
        }

        // Repeat the exact same request that produced baseline (omitting details would compare an all-details reply
        // against baseline's all-details-disabled shape, which can never match).
        var (repeat, _) = await Read("repeat-default", new JsonObject { ["details"] = NoDetails() }).ConfigureAwait(false);
        if (!Dyn.DeepEqual(new JsonArray([.. repeat.Select(row => (JsonNode?)row.DeepClone())]), new JsonArray([.. baseline.Select(row => (JsonNode?)row.DeepClone())])))
        {
            throw new AcceptanceException("repeating the default read returned a different snapshot");
        }

        report["context"] = loadedContext?.DeepClone();
        report["pawns"] = baseline.Count;
        report["colonists"] = colonists.Count;
        report["animals"] = animals.Count;
    }

    /// <summary>The unavailable reason of the row's <c>pawn.snapshot</c> issue, empty when it has none.</summary>
    private static string SnapshotIssueReason(JsonObject row)
    {
        foreach (var raw in row["issues"].AsSlice())
        {
            var issue = raw.AsMap() ?? [];
            if (issue["field"].AsString() == "pawn.snapshot")
            {
                return issue["unavailable"].Member("reason").AsString();
            }
        }

        return "";
    }

    /// <summary>
    /// Validates a pawn row's snapshot invariants. The health section carries its own populated CAS snapshot, and social
    /// is a populated PawnSocial block: earlier acceptance runs predated both and asserted their absence, which the Go
    /// port corrected rather than preserved.
    /// </summary>
    private static void DraftControl(JsonObject row, JsonNode? context)
    {
        var pawn = row["pawn"].AsMap() ?? [];
        NativeAssert.RequireIdentifier(pawn["id"], "pawn id");
        if (!row.ContainsKey("pawnSnapshot"))
        {
            var reason = SnapshotIssueReason(row);
            if (reason is not "UNAVAILABLE_REASON_NOT_APPLICABLE" and not "UNAVAILABLE_REASON_NATIVE_COMPONENT_MISSING")
            {
                throw new AcceptanceException($"a row without a pawn snapshot needs a pawn.snapshot issue, got reason \"{reason}\"");
            }

            var identity = context.Member("identity");
            var dead = row["dead"].AsBool() == true;
            var animal = row["animal"].AsBool() == true;
            var onCurrentMap = pawn["mapId"].AsNumber() == identity.Member("mapId").AsNumber();
            if (animal && !dead && onCurrentMap && reason != "UNAVAILABLE_REASON_NATIVE_COMPONENT_MISSING")
            {
                throw new AcceptanceException($"a live current-map animal cannot be blanket \"{reason}\"");
            }
        }
        else
        {
            var snapshot = row["pawnSnapshot"];
            try
            {
                NativeAssert.RequireSnapshot(snapshot);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"draftable pawn missing a populated snapshot: {e.Message}", e);
            }

            if (snapshot.Member("entityId").AsString() != pawn["id"].AsString())
            {
                throw new AcceptanceException("snapshot entityId does not match the row's pawn id");
            }

            if (!Dyn.DeepEqual(snapshot.Member("context"), context))
            {
                throw new AcceptanceException("snapshot context does not match the read context");
            }
        }

        // "health" carries a populated CAS snapshot (PawnHealth.snapshot).
        if (row.TryGetPropertyValue("health", out var health))
        {
            try
            {
                NativeAssert.RequireSnapshot(health.Member("snapshot"));
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"health section missing a populated CAS snapshot: {e.Message}", e);
            }
        }

        if (row.ContainsKey("social"))
        {
            NativeAssert.RequireSocial(row);
        }
    }

    /// <summary>Checks every core classification of every typed row is an explicit boolean.</summary>
    private static void RequireCore(List<JsonObject> typed)
    {
        if (typed.Count == 0)
        {
            throw new AcceptanceException("typed pawn read returned no rows");
        }

        foreach (var row in typed)
        {
            var pawn = row["pawn"].AsMap() ?? [];
            if (pawn["id"].AsString().Length == 0 || pawn["defName"].AsString().Length == 0)
            {
                throw new AcceptanceException($"typed pawn row without an id and defName: {pawn.ToJson()}");
            }

            foreach (var key in CoreFlags)
            {
                if (row[key].AsBool() is null)
                {
                    throw new AcceptanceException($"{key} must be an explicit boolean for pawn {pawn["id"].AsString()}, found {row[key].ToJson()}");
                }
            }
        }
    }

    /// <summary>Checks each colonist's health section carries a populated CAS snapshot and its social section is well formed.</summary>
    private static void RequireDetails(List<JsonObject> typed)
    {
        foreach (var row in typed)
        {
            var id = row["pawn"].Member("id").AsString();
            try
            {
                NativeAssert.RequireSnapshot(row["health"].Member("snapshot"));
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"health section missing populated snapshot for {id}: {e.Message}", e);
            }

            try
            {
                NativeAssert.RequireSocial(row);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"colonist {id}: {e.Message}", e);
            }
        }
    }
}
