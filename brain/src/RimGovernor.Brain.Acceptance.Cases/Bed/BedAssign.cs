using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>bed/assign</c> (Go <c>assign.go</c>): the AssignIntent on Actions/Apply. AssignActionHandler drives the same
/// CompAssignableToPawn.TryAssignPawn write the Assign tab uses. A colonist who genuinely owns one bed actually has their
/// bed ownership reassigned to a different, real, previously-unclaimed compliant bed, observed via a real
/// observations_list_pawns readback (not just a result). TryAssignPawn is synchronous, so the case does not poll game
/// ticks. Uses the disposable test/bed_assign_prepare fixture since a deterministic pre-owned bed and a second,
/// unclaimed, compliant target bed cannot be relied on from native random colony bed layout.
/// </summary>
internal static class BedAssign
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "bed/assign",
        Scope = "Native CompAssignableToPawn.TryAssignPawn (AssignIntent on Actions/Apply): a colonist who "
            + "genuinely owns one bed actually has their bed ownership reassigned to a different real, previously-"
            + "unclaimed compliant bed, a drifted previous-bed expectation is refused without mutation, the real "
            + "bed-ownership change is observed via native readback (not just a result), and a resent intent "
            + "applies again.",

        // Sited on the audited baseline, not a fresh random world.
        Start = new FixtureStart { Op = "test/bed_assign_prepare", On = new LabStart() },
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;

        // The fixture edits pawn/building state directly outside any authority scope, so authority is granted after it ran.
        var h = s.Harness;
        var identity = s.Identity;
        var names = s.Names;
        var prepared = s.Prepared ?? [];
        if (!Dyn.Contains(names, "rimgovernor/operations_apply"))
        {
            throw new AcceptanceException("missing rimgovernor/operations_apply in discovery");
        }

        if (!Dyn.Contains(names, "rimgovernor/observations_list_pawns"))
        {
            throw new AcceptanceException("missing rimgovernor/observations_list_pawns in discovery");
        }

        var pawnId = prepared["pawn"].AsString();
        var previousBedId = prepared["previousBed"].AsString();
        var bedId = prepared["bed"].AsString();
        if (pawnId.Length == 0 || previousBedId.Length == 0 || bedId.Length == 0)
        {
            throw new AcceptanceException($"prepare: missing fixture pawn/previousBed/bed ids: {prepared.ToJson()}");
        }

        report["fixture_pawn"] = pawnId;
        report["fixture_previous_bed"] = previousBedId;
        report["fixture_bed"] = bedId;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        // ownedBed reads the pawn's current ownedBedId through observations_list_pawns.
        async Task<string> OwnedBed(string label)
        {
            var reply = await h.WireAsync(
                label,
                "observations_list_pawns",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                    ["filter"] = new JsonObject { ["ids"] = new JsonArray(pawnId) },
                    ["details"] = new JsonObject(),
                },
                cancellationToken).ConfigureAwait(false);
            var observed = Harness.Outcome(reply, "observed").Value;
            var rows = observed["pawns"].AsSlice();
            if (rows.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected exactly one observed pawn, got {observed.ToJson()}");
            }

            var row = rows[0].AsMap();
            if (row?["pawn"].AsMap()?["id"].AsString() != pawnId)
            {
                throw new AcceptanceException($"{label}: unexpected pawn row: {row.ToJson()}");
            }

            return row!["ownedBed"].RefId();
        }

        // apply sends one AssignIntent and returns the action's result.
        async Task<JsonObject> Apply(string label, string key, string expectedPrevious)
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
                        ["assign"] = new JsonObject
                        {
                            ["pawnId"] = pawnId,
                            ["thingId"] = bedId,
                            ["expectedPrevious"] = new JsonObject { ["entityId"] = expectedPrevious },
                        },
                    }),
                },
                cancellationToken).ConfigureAwait(false);
            return OneResult(label, reply);
        }

        var before = await OwnedBed("pawn-before").ConfigureAwait(false);
        if (before != previousBedId)
        {
            throw new AcceptanceException($"pawn-before: expected the fixture colonist to own \"{previousBedId}\", got \"{before}\"");
        }

        report["pawn_before_owned_bed"] = before;

        // Refusal: a previous-bed expectation that no longer holds is refused and leaves ownership alone.
        var drifted = await Apply("apply-drifted-previous", "bed-drifted", "Thing_NotThePreviousBed").ConfigureAwait(false);
        if (drifted["refused"].AsMap() is null)
        {
            throw new AcceptanceException($"apply-drifted-previous: expected a refusal, got {drifted.ToJson()}");
        }

        var owned = await OwnedBed("pawn-after-refusal").ConfigureAwait(false);
        if (owned != previousBedId)
        {
            throw new AcceptanceException($"pawn-after-refusal: expected an unchanged owned bed, got \"{owned}\"");
        }

        // Apply, then resend: both apply, the second as the ownership stands.
        foreach (var label in new[] { "apply-bed", "resend-bed" })
        {
            var result = await Apply(label, "bed-assign", previousBedId).ConfigureAwait(false);
            var effect = result["applied"].Member("applied").Member("observed").Member("assign").AsMap();
            if (effect is null || effect["pawnId"].AsString() != pawnId || effect["thingId"].AsString() != bedId)
            {
                throw new AcceptanceException($"{label}: expected applied bed evidence, got {result.ToJson()}");
            }

            if (effect["assigned"].AsBool() != true)
            {
                throw new AcceptanceException($"{label}: expected assigned=true, got {effect.ToJson()}");
            }

            var after = await OwnedBed("pawn-after-" + label).ConfigureAwait(false);
            if (after != bedId)
            {
                throw new AcceptanceException($"pawn-after-{label}: expected the native colonist to now own \"{bedId}\", got \"{after}\"");
            }

            report["pawn_after_owned_bed"] = after;
        }

        report["bed_reassigned"] = true;

        // Swap: another colonist's plain claim on the now-owned bed is refused as already assigned; flagged as a swap it
        // evicts the owner.
        var listed = await h.WireAsync(
            "list-colonists",
            "observations_list_pawns",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["filter"] = new JsonObject { ["colonist"] = true, ["downed"] = false, ["drafted"] = false },
                ["details"] = new JsonObject(),
            },
            cancellationToken).ConfigureAwait(false);
        var observedColonists = Harness.Outcome(listed, "observed").Value;
        var otherId = "";
        var otherBed = "";
        foreach (var raw in observedColonists["pawns"].AsSlice())
        {
            var row = raw.AsMap();
            var id = row?["pawn"].AsMap()?["id"].AsString() ?? "";
            if (id.Length > 0 && id != pawnId)
            {
                otherId = id;
                otherBed = row!["ownedBed"].RefId();
                break;
            }
        }

        if (otherId.Length == 0)
        {
            throw new AcceptanceException($"list-colonists: no second colonist to swap in: {observedColonists.ToJson()}");
        }

        report["swap_pawn"] = otherId;

        async Task<JsonObject> Claim(string label, string key, bool swap)
        {
            var previous = otherBed.Length > 0
                ? new JsonObject { ["entityId"] = otherBed }
                : new JsonObject { ["clear"] = new JsonObject() };
            var intent = new JsonObject { ["pawnId"] = otherId, ["thingId"] = bedId, ["expectedPrevious"] = previous };
            if (swap)
            {
                intent["swap"] = true;
            }

            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = key, ["assign"] = intent }),
                },
                cancellationToken).ConfigureAwait(false);
            return OneResult(label, reply);
        }

        var plain = await Claim("apply-owned-plain", "bed-owned-plain", swap: false).ConfigureAwait(false);
        if (plain["refused"].AsMap() is null)
        {
            throw new AcceptanceException($"apply-owned-plain: expected an already-assigned refusal, got {plain.ToJson()}");
        }

        var swapped = await Claim("apply-owned-swap", "bed-owned-swap", swap: true).ConfigureAwait(false);
        if (swapped["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"apply-owned-swap: expected the swap to apply, got {swapped.ToJson()}");
        }

        if (await OwnedBed("pawn-after-swap").ConfigureAwait(false) == bedId)
        {
            throw new AcceptanceException($"pawn-after-swap: expected the evicted colonist to lose \"{bedId}\"");
        }

        report["swap_evicted"] = true;
    }

    private static JsonObject OneResult(string label, JsonObject reply)
    {
        var results = reply["results"].AsSlice();
        return results.Count != 1
            ? throw new AcceptanceException($"{label}: expected one result, got {reply.ToJson()}")
            : results[0].AsMap() ?? [];
    }
}
