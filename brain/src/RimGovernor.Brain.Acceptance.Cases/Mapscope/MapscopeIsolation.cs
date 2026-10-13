using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mapscope/isolation</c> (Go <c>mapscope/isolation.go</c>): typed reads and operations are scoped to the map their
/// identity names, not to whichever map the player is viewing. A fixture-generated second player map makes the
/// distinction observable. Requires a build with the MapScopeFixture for test/map_scope_generate and test/map_scope_view.
/// </summary>
internal static class MapscopeIsolation
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "mapscope/isolation",
        Scope = "Two loaded player maps: a status read for the second map's identity answers about that map while the first is viewed; switching the viewed map invalidates authority (IdentityChanged, generation+1) but reads for either identity still resolve their own map, the roster spans both maps with map_id, presentation reads bound to the viewed map refuse the other identity as stale, an identity naming an unloaded map is stale with the viewed context, and after switching back a draft operation bound to the first map is granted and lands there.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var home = s.Identity;
        foreach (var tool in new[]
        {
            "rimgovernor/authority_read_status", "rimgovernor/authority_control", "rimgovernor/observations_read_status",
            "rimgovernor/observations_list_pawns", "rimgovernor/presentation_colonists", "rimgovernor/presentation_selection",
            "test/map_scope_generate", "test/map_scope_view",
        })
        {
            if (!Dyn.Contains(s.Names, tool))
            {
                throw new AcceptanceException($"missing {tool} in discovery (fixture build required)");
            }
        }

        var homeMap = (int)home["mapId"].AsNumber();

        // Setup: Auto granted on the viewed (home) map.
        var (generation, state) = await AuthorityAsync(h, "status-fresh", home, cancellationToken).ConfigureAwait(false);
        if (state.ContainsKey("unavailable"))
        {
            generation = 1;
        }

        var granted = await GrantAsync(h, "grant-home", home, generation, cancellationToken).ConfigureAwait(false);

        // Case 1: a second player map exists; a read for its identity answers about it while the home map stays viewed.
        JsonObject generated;
        try
        {
            generated = await h.CallAsync("generate", "test/map_scope_generate", [], cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("generate: " + e.Message, e);
        }

        var secondMap = (int)generated["secondMapId"].AsNumber();
        if (generated["success"].AsBool() != true || secondMap == homeMap || (int)generated["viewedMapId"].AsNumber() != homeMap)
        {
            throw new AcceptanceException($"generate: expected a distinct second map with home still viewed, got {generated.ToJson()}");
        }

        var second = WithMap(home, secondMap);
        s.Report["setup"] = new JsonObject { ["homeMapId"] = homeMap, ["secondMapId"] = secondMap, ["generationGranted"] = granted };
        await StatusOnAsync(h, "status-second-while-home-viewed", second, secondMap, cancellationToken).ConfigureAwait(false);
        await StatusOnAsync(h, "status-home-while-home-viewed", home, homeMap, cancellationToken).ConfigureAwait(false);

        // Map generation may flip the viewed map and back; that is the player's own view change as far as authority is
        // concerned. Measure from here.
        var (beforeSwitch, beforeState) = await AuthorityAsync(h, "status-after-generate", home, cancellationToken).ConfigureAwait(false);
        s.Report["case_generate"] = new JsonObject { ["state"] = beforeState, ["generation"] = beforeSwitch };

        // Case 2: switching the viewed map invalidates authority as IdentityChanged at generation+1; reads for either
        // identity still resolve their own map.
        JsonObject viewed;
        try
        {
            viewed = await h.CallAsync("view-second", "test/map_scope_view", new JsonObject { ["mapId"] = secondMap }, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("view-second: " + e.Message, e);
        }

        if ((int)viewed["viewedMapId"].AsNumber() != secondMap)
        {
            throw new AcceptanceException($"view-second: expected map {secondMap} viewed, got {viewed.ToJson()}");
        }

        var (afterSwitch, switchState) = await AuthorityAsync(h, "status-after-switch", home, cancellationToken).ConfigureAwait(false);
        var inactive = switchState["inactive"].AsMap();
        if (inactive is null || inactive["reason"].AsString() != "REVOCATION_REASON_IDENTITY_CHANGED")
        {
            throw new AcceptanceException($"status-after-switch: expected inactive IDENTITY_CHANGED, got {switchState.ToJson()}");
        }

        if (afterSwitch != beforeSwitch + 1)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"status-after-switch: expected generation {beforeSwitch + 1}, got {afterSwitch}"));
        }

        await StatusOnAsync(h, "status-home-while-second-viewed", home, homeMap, cancellationToken).ConfigureAwait(false);
        await StatusOnAsync(h, "status-second-while-second-viewed", second, secondMap, cancellationToken).ConfigureAwait(false);
        s.Report["case_switch"] = new JsonObject { ["state"] = switchState, ["generation"] = afterSwitch };

        // Case 3: the roster spans both maps and carries map_id; the home map's colonists are all on the home map, and
        // none is claimed on the second.
        var rosterReply = await h.WireAsync("roster", "presentation_colonists", new JsonObject { ["identity"] = home.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var roster = OutcomeOf("roster", rosterReply, "roster");
        var colonists = roster["colonists"].AsSlice();
        if (colonists.Count == 0)
        {
            throw new AcceptanceException("roster: no colonists");
        }

        var firstColonist = string.Empty;
        foreach (var row in colonists)
        {
            var m = row.AsMap() ?? [];
            if ((int)m["mapId"].AsNumber() != homeMap)
            {
                throw new AcceptanceException($"roster: colonist {m.ToJson()} is not on the home map");
            }

            if (firstColonist.Length == 0)
            {
                firstColonist = m["pawnId"].AsString();
            }
        }

        var narrowReply = await h.WireAsync(
            "roster-second-only",
            "presentation_colonists",
            new JsonObject { ["identity"] = second.DeepClone(), ["currentMapOnly"] = true },
            cancellationToken).ConfigureAwait(false);
        var narrow = OutcomeOf("roster-second-only", narrowReply, "roster");
        var narrowCount = narrow["colonists"].AsSlice().Count;
        if (narrowCount != 0)
        {
            throw new AcceptanceException($"roster-second-only: expected no colonists on the generated map, got {narrowCount}");
        }

        s.Report["case_roster"] = new JsonObject { ["colonists"] = colonists.Count, ["secondOnly"] = 0 };

        // Case 4: presentation state lives on the viewed map; the home identity is stale there, with the viewed map's
        // context, while the second is not.
        var selectionReply = await h.WireAsync("selection-home-while-second-viewed", "presentation_selection", new JsonObject { ["identity"] = home.DeepClone() }, cancellationToken).ConfigureAwait(false);
        if (FailureCode(selectionReply) != "FAILURE_CODE_STALE_IDENTITY")
        {
            throw new AcceptanceException($"selection-home-while-second-viewed: expected STALE_IDENTITY, got {selectionReply.ToJson()}");
        }

        var observed = selectionReply["failure"].AsMap()?["observedContext"].AsMap();
        if ((int)(observed?["identity"].AsMap()?["mapId"].AsNumber() ?? 0) != secondMap)
        {
            throw new AcceptanceException($"selection-home-while-second-viewed: expected observed map {secondMap}, got {observed.ToJson()}");
        }

        var selectionSecond = await h.WireAsync("selection-second-while-second-viewed", "presentation_selection", new JsonObject { ["identity"] = second.DeepClone() }, cancellationToken).ConfigureAwait(false);

        // Identity accepted: a selection snapshot when rendered, and headless the graphical-only refusal, which comes
        // after the identity check.
        if (!selectionSecond.ContainsKey("selection") && !(s.Config.Headless && FailureCode(selectionSecond) == "FAILURE_CODE_UNAVAILABLE"))
        {
            throw new AcceptanceException($"selection-second-while-second-viewed: expected a selection outcome, got {selectionSecond.ToJson()}");
        }

        // Case 5: an identity naming a map that is not loaded is stale, carrying the viewed map's context so the caller
        // can re-anchor.
        var goneReply = await h.WireAsync(
            "status-unloaded-map",
            "observations_read_status",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = WithMap(home, 987654) } },
            cancellationToken).ConfigureAwait(false);
        if (FailureCode(goneReply) != "FAILURE_CODE_STALE_IDENTITY")
        {
            throw new AcceptanceException($"status-unloaded-map: expected STALE_IDENTITY, got {goneReply.ToJson()}");
        }

        var goneObserved = goneReply["failure"].AsMap()?["observedContext"].AsMap();
        if ((int)(goneObserved?["identity"].AsMap()?["mapId"].AsNumber() ?? 0) != secondMap)
        {
            throw new AcceptanceException($"status-unloaded-map: expected observed map {secondMap}, got {goneObserved.ToJson()}");
        }

        // Case 6: back on the home map, a fresh grant admits a draft bound to the home identity and it lands on the home
        // map's colonist.
        try
        {
            _ = await h.CallAsync("view-home", "test/map_scope_view", new JsonObject { ["mapId"] = homeMap }, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("view-home: " + e.Message, e);
        }

        var (backGeneration, _) = await AuthorityAsync(h, "status-back-home", home, cancellationToken).ConfigureAwait(false);
        var regranted = await GrantAsync(h, "regrant-home", home, backGeneration, cancellationToken).ConfigureAwait(false);
        var draftResult = await ApplyIntents.ApplyOneAsync(
            h,
            "draft",
            home,
            "mapscope-draft-1",
            new JsonObject { ["draft"] = new JsonObject { ["pawnId"] = firstColonist, ["drafted"] = true } },
            cancellationToken).ConfigureAwait(false);
        var job = ApplyIntents.AppliedJob("draft", draftResult);
        if (job["issued"].AsBool() != true)
        {
            throw new AcceptanceException($"draft: expected {firstColonist} drafted, got {draftResult.ToJson()}");
        }

        var receipt = draftResult["applied"].AsMap();
        var afterReply = await h.WireAsync(
            "pawn-after",
            "observations_list_pawns",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = home.DeepClone() },
                ["filter"] = new JsonObject { ["colonist"] = true, ["downed"] = false, ["ids"] = new JsonArray(firstColonist) },
            },
            cancellationToken).ConfigureAwait(false);
        var admittedIdentity = receipt?["admittedContext"].AsMap()?["identity"].AsMap();
        if ((int)(admittedIdentity?["mapId"].AsNumber() ?? 0) != homeMap)
        {
            throw new AcceptanceException($"draft: admitted on map {admittedIdentity?["mapId"].ToJson()}, expected {homeMap}");
        }

        var afterObserved = OutcomeOf("pawn-after", afterReply, "observed");
        var afterRows = afterObserved["pawns"].AsSlice();
        if (afterRows.Count != 1)
        {
            throw new AcceptanceException($"pawn-after: expected one row, got {afterRows.Count}");
        }

        var after = afterRows[0].AsMap();
        var afterPawn = after?["pawn"].AsMap();
        var afterSnapshot = after?["pawnSnapshot"].AsMap();
        if (after?["drafted"].AsBool() != true || (int)(afterPawn?["mapId"].AsNumber() ?? 0) != homeMap
            || afterSnapshot?["token"].AsString() != job["resultingSnapshotToken"].AsString())
        {
            throw new AcceptanceException($"draft: expected {firstColonist} drafted on map {homeMap} with the receipt's snapshot token, got {after.ToJson()}");
        }

        s.Report["case_draft_home"] = new JsonObject { ["generation"] = regranted, ["pawnId"] = firstColonist };
    }

    private static JsonObject WithMap(JsonObject identity, int mapId)
    {
        var copy = (JsonObject)identity.DeepClone();
        copy["mapId"] = mapId;
        return copy;
    }

    private static string FailureCode(JsonObject reply) => reply["failure"].AsMap()?["code"].AsString() ?? string.Empty;

    private static JsonObject OutcomeOf(string label, JsonObject reply, string kind)
    {
        try
        {
            return Harness.Outcome(reply, kind).Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: expected a {kind} outcome: {e.Message}", e);
        }
    }

    // Asserts observations_read_status for identity answers with that identity's map in its context, whichever map is viewed.
    private static async Task StatusOnAsync(Harness h, string label, JsonObject identity, int mapId, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label,
            "observations_read_status",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["colonists"] = false,
                ["threats"] = false,
            },
            cancellationToken).ConfigureAwait(false);
        var observed = OutcomeOf(label, reply, "observed");
        var context = observed["context"].AsMap();
        if ((int)(context?["identity"].AsMap()?["mapId"].AsNumber() ?? 0) != mapId)
        {
            throw new AcceptanceException($"{label}: expected context map {mapId}, got {context.ToJson()}");
        }
    }

    private static async Task<(ulong Generation, JsonObject State)> AuthorityAsync(Harness h, string label, JsonObject identity, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, "authority_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var status = OutcomeOf(label, reply, "status");
        var generation = status["context"].AsMap()?["nativeGeneration"].AsUInt64() ?? 0;
        var state = new JsonObject();
        foreach (var key in new[] { "unavailable", "inactive", "active" })
        {
            if (status.TryGetPropertyValue(key, out var value))
            {
                state[key] = value?.DeepClone();
            }
        }

        return state.Count != 1
            ? throw new AcceptanceException($"{label}: expected exactly one authority state, got {status.ToJson()}")
            : (generation, state);
    }

    private static async Task<ulong> GrantAsync(Harness h, string label, JsonObject identity, ulong expected, CancellationToken cancellationToken)
    {
        var granted = await Authority.GrantAutoAtAsync(h.WireFunc(), label, identity, expected, cancellationToken).ConfigureAwait(false);
        return granted["context"].AsMap()?["nativeGeneration"].AsUInt64() ?? 0;
    }
}
