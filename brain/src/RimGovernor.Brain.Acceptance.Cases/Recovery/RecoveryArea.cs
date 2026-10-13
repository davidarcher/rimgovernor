using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>recovery/area</c> (Go <c>recovery/area.go</c>): the disaster-recovery allowed-area write (G01.07e), a
/// WorkSettingsIntent on Actions/Apply carrying only an allowed area. A colonist restricted to an outdoor Area during a
/// registered ToxicFallout hazard is reassigned to a named roofed refuge Area, observed via
/// <c>observations_list_pawns</c> readback; the write is synchronous, so the case polls no game ticks. It uses the
/// disposable <c>test/recovery_area_prepare</c> fixture since a deterministic roofed refuge Area, outdoor Area and
/// hazard-restricted colonist cannot be relied on from native random pawn generation.
/// </summary>
internal static class RecoveryArea
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "recovery/area",
        Scope = "WorkSettingsIntent allowed area (disaster-recovery area) on Actions/Apply: a colonist "
            + "restricted to an outdoor area during a registered hazard is reassigned to a named roofed refuge, "
            + "observed via native readback; clearing under the hazard and a missing pawn are refused, a resent "
            + "key returns its first result and a fresh key applies again.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/recovery_area_prepare" },
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        // Fixture: one roofed refuge Area, one outdoor Area, and one existing colonist restricted to the outdoor area
        // under a registered hazard. Run this BEFORE acquiring authority: the fixture builds/restricts directly
        // outside any authority scope, and NativeControlAuthority.RevokeExternal revokes any held lease for such
        // external activity regardless of who holds it.
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        if (!Dyn.Contains(s.Names, "rimgovernor/operations_apply"))
        {
            throw new AcceptanceException("missing rimgovernor/operations_apply in discovery");
        }

        var pawnId = prepared["pawn"].AsString();
        var refugeId = prepared["refuge"].AsString();
        var outdoorId = prepared["outdoor"].AsString();
        if (pawnId.Length == 0 || refugeId.Length == 0 || outdoorId.Length == 0)
        {
            throw new AcceptanceException($"prepare: missing fixture pawn/refuge/outdoor ids: {prepared.ToJson()}");
        }

        report["fixture_pawn"] = pawnId;
        report["fixture_refuge"] = refugeId;
        report["fixture_outdoor"] = outdoorId;

        // The fixture's registered hazard is a timed ToxicFallout, so the colony-facts environment census must carry
        // it with its native remaining duration: the row Go's disaster review plans against.
        var facts = await h.WireAsync(
            "environment-census",
            "observations_read_colony_facts",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(facts, "observed");
        JsonObject? hazard = null;
        foreach (var raw in observed["environment"].AsSlice())
        {
            var row = raw.AsMap();
            if (row?["defName"].AsString() == "ToxicFallout")
            {
                hazard = row;
            }
        }

        if (hazard is null)
        {
            throw new AcceptanceException($"environment census lacks the fixture's ToxicFallout: {observed["environment"].ToJson()}");
        }

        var permanent = hazard["permanent"].AsBool() == true;
        if (permanent || hazard["ticksLeft"].AsNumber() <= 0 || hazard["id"].AsString().Length == 0)
        {
            throw new AcceptanceException($"environment census row lacks a timed remaining duration: {hazard.ToJson()}");
        }

        report["environment_hazard"] = hazard.DeepClone();

        // Actions/Apply needs current native authority; the allowed-area write is synchronous, so one acquire covers
        // the whole sequence.
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        // pawnArea reads the pawn's current allowedAreaId through observations_list_pawns. allowedAreaId is projected
        // only under the work detail flag.
        async Task<string> PawnArea(string label)
        {
            var reply = await h.WireAsync(
                label,
                "observations_list_pawns",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                    ["filter"] = new JsonObject { ["ids"] = new JsonArray(pawnId) },
                    ["details"] = new JsonObject { ["settings"] = false, ["work"] = true },
                },
                cancellationToken).ConfigureAwait(false);
            var (_, observedPawns) = Harness.Outcome(reply, "observed");
            var rows = observedPawns["pawns"].AsSlice();
            if (rows.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected exactly one observed pawn, got {observedPawns.ToJson()}");
            }

            var row = rows[0].AsMap() ?? [];
            if (row["pawn"].AsMap()?["id"].AsString() != pawnId)
            {
                throw new AcceptanceException($"{label}: unexpected pawn row: {row.ToJson()}");
            }

            return row["settings"].AsMap()?["allowedAreaId"].AsString() ?? "";
        }

        // apply sends one WorkSettingsIntent (bridge.workSettingsAction) and returns its single result.
        async Task<JsonObject> Apply(string label, string key, string pId, JsonObject area)
        {
            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject
                    {
                        ["key"] = key,
                        ["workSettings"] = new JsonObject { ["pawnId"] = pId, ["allowedArea"] = area },
                    }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            if (results.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected one result, got {reply.ToJson()}");
            }

            return results[0].AsMap() ?? [];
        }

        void RefusedWith(string label, JsonObject result, string code)
        {
            if (result["refused"].AsMap() is not { } refusal || refusal["code"].AsString() != code)
            {
                throw new AcceptanceException($"{label}: expected a {code} refusal, got {result.ToJson()}");
            }

            report[label] = refusal["reason"].AsString();
        }

        void Applied(string label, JsonObject result)
        {
            var settings = result["applied"].AsMap()?["applied"].AsMap()?["observed"].AsMap()?["settings"].AsMap();
            var fields = settings?["fields"].AsSlice() ?? [];
            if (fields.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected one applied allowed-area field, got {result.ToJson()}");
            }

            var field = fields[0].AsMap() ?? [];
            if (field["field"].AsString() != "SETTINGS_FIELD_ALLOWED_AREA" || field["outcome"].AsString() != "FIELD_OUTCOME_APPLIED")
            {
                throw new AcceptanceException($"{label}: unexpected allowed-area field evidence: {field.ToJson()}");
            }
        }

        var beforeArea = await PawnArea("pawn-before").ConfigureAwait(false);
        if (beforeArea != outdoorId)
        {
            throw new AcceptanceException($"pawn-before: expected the fixture colonist restricted to the outdoor area \"{outdoorId}\", got \"{beforeArea}\"");
        }

        report["pawn_before_area"] = beforeArea;

        // Refusals: clearing the restriction under the roof hazard, and a pawn that is not on the map.
        var result = await Apply("hazard-clear", "area-hazard-clear", pawnId, new JsonObject { ["clear"] = new JsonObject() }).ConfigureAwait(false);
        RefusedWith("hazard_clear_refused", result, "FAILURE_CODE_INVALID_REQUEST");
        string area;
        try
        {
            area = await PawnArea("hazard-clear-unchanged").ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"hazard clear changed restriction: , {e.Message}", e);
        }

        if (area != beforeArea)
        {
            throw new AcceptanceException($"hazard clear changed restriction: {area}, ");
        }

        result = await Apply("missing-pawn", "area-missing-pawn", "Human_missing_pawn", new JsonObject { ["entityId"] = refugeId }).ConfigureAwait(false);
        RefusedWith("missing_pawn_refused", result, "FAILURE_CODE_NOT_FOUND");

        // Apply: the colonist moves to the refuge, read back from the pawn.
        var first = await Apply("apply-area", "area-apply", pawnId, new JsonObject { ["entityId"] = refugeId }).ConfigureAwait(false);
        Applied("apply-area", first);
        var afterArea = await PawnArea("pawn-after-apply").ConfigureAwait(false);
        if (afterArea != refugeId)
        {
            throw new AcceptanceException($"pawn-after-apply: expected the native colonist to be reassigned to the refuge area \"{refugeId}\", got \"{afterArea}\"");
        }

        report["pawn_after_area"] = afterArea;
        report["area_reassigned"] = true;

        // Resend returns the first result; a fresh key applies again because the restriction already holds.
        var resent = await Apply("resend-area", "area-apply", pawnId, new JsonObject { ["entityId"] = refugeId }).ConfigureAwait(false);
        if (!Dyn.DeepEqual(resent, first))
        {
            throw new AcceptanceException($"resend-area: resend of the same key returned a different result: {resent.ToJson()}");
        }

        var again = await Apply("reapply-area", "area-reapply", pawnId, new JsonObject { ["entityId"] = refugeId }).ConfigureAwait(false);
        Applied("reapply-area", again);
    }
}
