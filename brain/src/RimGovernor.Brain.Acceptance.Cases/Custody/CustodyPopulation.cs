using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>custody/population</c> (Go <c>population.go</c>): population custody: the Capture and Rescue GiveJobIntents on
/// Actions/Apply, the same intents bridge's captureAction and rescueAction send. A downed hostile is actually captured
/// into prisoner custody and a downed, unadmitted friendly guest is actually rescued into an ordinary bed -- real native
/// roster/status change observed via ticks, not just a receipt. Between the two, the captured prisoner takes
/// PrisonerInteractionIntents on Actions/Apply: the ReduceResistance and AttemptRecruit modes apply and read back, and
/// once the fixture recruits the prisoner a further intent is refused against live custody state. Uses the disposable
/// test/population_setup fixture (PopulationFixture.cs) since a deterministic downed candidate and guest near a ready
/// prison and spare beds cannot be relied on from native random pawn generation and colony state.
/// </summary>
internal static class CustodyPopulation
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "custody/population",
        Scope = "Population custody through GiveJobIntents on Actions/Apply: an actual Capture job carries a downed hostile "
            + "into prisoner custody and an actual Rescue job carries a downed unadmitted guest into an ordinary bed, "
            + "with live refusals (capture of a non-hostile guest, a missing patient), a resend that applies again without "
            + "a second job, and real roster/status change observed via native ticks. The captured prisoner then "
            + "takes ReduceResistance and AttemptRecruit interaction intents on Actions/Apply that read back, and a "
            + "further intent is refused once a native recruit has taken the pawn out of custody.",
        Start = new FixtureStart
        {
            Op = "test/population_setup",
            Args = new JsonObject { ["candidateKind"] = "Villager" },
            On = new LabStart(),
        },
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        // --- Fixture: one disposable downed hostile candidate (capture target, a ready prisoner bed) and one downed
        // unadmitted friendly guest (rescue target, spare sleeping spots). The fixture ran before authority is acquired:
        // its direct spawns would revoke a held grant.
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var candidateId = prepared["candidate"].AsString();
        var visitorId = prepared["visitor"].AsString();
        var prisonId = prepared["prison"].AsString();
        if (candidateId.Length == 0 || visitorId.Length == 0 || prisonId.Length == 0)
        {
            throw new AcceptanceException($"setup: missing fixture ids: {prepared.ToJson()}");
        }

        report["fixture_candidate"] = candidateId;
        report["fixture_visitor"] = visitorId;
        var capturerId = "";
        foreach (var entry in prepared["bedChecks"].AsSlice())
        {
            var row = entry.AsMap();
            if (row?["usable"].AsBool() == true && row["reachable"].AsBool() == true && row["reservable"].AsBool() == true
                && row["nativeBed"].AsString() == prisonId)
            {
                capturerId = row["pawn"].AsString();
                break;
            }
        }

        if (capturerId.Length == 0)
        {
            throw new AcceptanceException($"setup: no fixture worker can reach/reserve the prisoner bed: {prepared["bedChecks"].ToJson()}");
        }

        report["fixture_capturer"] = capturerId;

        // acquire (re)issues SetMode(Auto) at the freshest native generation. Native simulation outside an owned scope
        // revokes authority, so this is called again after any such stretch.
        async Task Acquire(string label) => _ = await Authority.GrantAutoAsync(h.WireFunc(), label, identity, cancellationToken).ConfigureAwait(false);

        await Acquire("acquire").ConfigureAwait(false);

        async Task<JsonObject> PawnRow(string label, string pawnId)
        {
            var reply = await h.WireAsync(
                label,
                "observations_list_pawns",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                    ["filter"] = new JsonObject { ["ids"] = new JsonArray(pawnId), ["includeDead"] = true },
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

            return row;
        }

        JsonObject Order(string targetId, string kind) => ApplyIntents.GiveJob(capturerId, kind, targetId);

        // ordered applies one custody intent, checks it issued jobDef, then resends it and checks the resend applies
        // without a second job.
        async Task Ordered(string label, string targetId, string kind, string jobDef)
        {
            var result = await ApplyIntents.ApplyOneAsync(h, "apply-" + label, identity, label, Order(targetId, kind), cancellationToken).ConfigureAwait(false);
            var job = ApplyIntents.AppliedJob(label, result);
            if (job["issued"].AsBool() != true || job["pawnId"].AsString() != capturerId || job["jobDef"].AsString() != jobDef)
            {
                throw new AcceptanceException($"{label}: expected an issued {jobDef} job, got {job.ToJson()}");
            }

            var again = await ApplyIntents.ApplyOneAsync(h, "resend-" + label, identity, label + "-again", Order(targetId, kind), cancellationToken).ConfigureAwait(false);
            if (ApplyIntents.AppliedJob(label + " resend", again)["issued"].AsBool() == true)
            {
                throw new AcceptanceException($"{label} resend issued a second job: {again.ToJson()}");
            }
        }

        // until runs game time until the pawn's row satisfies done.
        async Task Until(string label, string pawnId, Func<JsonObject, bool> done)
        {
            _ = await RunAt.RunUntilAsync(
                h,
                label,
                2 * Wait.TicksPerDay,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var row = await PawnRow(label, pawnId).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return (Wait.Signature(row["prisoner"].ToJson(), row["inBed"].ToJson(), row["downed"].ToJson()), done(row));
                },
                cancellationToken).ConfigureAwait(false);
        }

        // =================================================================
        // Capture: the downed hostile candidate.
        // =================================================================
        var candidateRow = await PawnRow("candidate-before", candidateId).ConfigureAwait(false);
        if (candidateRow["downed"].AsBool() != true)
        {
            throw new AcceptanceException($"candidate-before: expected the fixture candidate to be downed: {candidateRow.ToJson()}");
        }

        if (candidateRow["prisoner"].AsBool() == true)
        {
            throw new AcceptanceException($"candidate-before: expected the fixture candidate not yet a prisoner: {candidateRow.ToJson()}");
        }

        // Refusals, checked live at apply: a non-hostile guest cannot be captured, and an absent patient is not found.
        var friendly = await ApplyIntents.ApplyOneAsync(h, "apply-capture-guest", identity, "custody-capture-guest", Order(visitorId, "Capture"), cancellationToken).ConfigureAwait(false);
        ApplyIntents.Refused("capture-guest", friendly, "Capture refused");
        var missing = await ApplyIntents.ApplyOneAsync(h, "apply-capture-missing", identity, "custody-capture-missing", Order("Thing_NoSuchPatient", "Capture"), cancellationToken).ConfigureAwait(false);
        if (missing["refused"].AsMap()?["code"].AsString() != "FAILURE_CODE_NOT_FOUND")
        {
            throw new AcceptanceException($"apply-capture-missing: expected a NOT_FOUND refusal, got {missing.ToJson()}");
        }

        await Ordered("custody-capture", candidateId, "Capture", "Capture").ConfigureAwait(false);

        // Observe: run real game time forward until the candidate is actually a colony prisoner; the applied order alone
        // never proves capture.
        await Until("observe-capture", candidateId, row => row["prisoner"].AsBool() == true).ConfigureAwait(false);
        report["candidate_captured"] = true;

        // =================================================================
        // Prisoner interaction: the captured candidate.
        // =================================================================
        await Acquire("re-acquire-before-interaction").ConfigureAwait(false);

        // personRow reads the prisoner through observations_read_population, the same read bridge.ReadRoundsPopulation
        // decodes the current interaction from.
        async Task<JsonObject> PersonRow(string label)
        {
            var reply = await h.WireAsync(
                label,
                "observations_read_population",
                new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
                cancellationToken).ConfigureAwait(false);
            var observed = Harness.Outcome(reply, "observed").Value;
            foreach (var entry in observed["persons"].AsSlice())
            {
                if (entry.AsMap() is { } person && PawnTable.PawnRef(person) == candidateId)
                {
                    // The custody facts ride the pawn table row.
                    await h.JoinPawnAsync(label, identity, person, cancellationToken).ConfigureAwait(false);
                    return person;
                }
            }

            throw new AcceptanceException($"{label}: candidate {candidateId} missing from the population census");
        }

        var prisonerBefore = await PersonRow("prisoner-before").ConfigureAwait(false);
        if (prisonerBefore["pawn"].AsMap()?["prisoner"].AsBool() != true)
        {
            throw new AcceptanceException($"prisoner-before: expected the captured candidate to be a prisoner: {prisonerBefore.ToJson()}");
        }

        // The routine release path's facts: a current prisoner carries native's recruit resistance and the
        // TimeAsPrisoner record, which ProtoJSON renders as a decimal string for int64.
        if (!prisonerBefore.ContainsKey("resistance"))
        {
            throw new AcceptanceException($"prisoner-before: expected resistance on the captured prisoner: {prisonerBefore.ToJson()}");
        }

        if (!prisonerBefore.ContainsKey("prisonerTicks"))
        {
            throw new AcceptanceException($"prisoner-before: expected prisonerTicks on the captured prisoner: {prisonerBefore.ToJson()}");
        }

        report["prisoner_ticks"] = prisonerBefore["prisonerTicks"]?.DeepClone();

        // applyInteraction sends one PrisonerInteractionIntent on Actions/Apply and returns the action's result: applied
        // carries the prisoner evidence, refused the native reason.
        async Task<JsonObject> ApplyInteraction(string label, string key, string pawn, string mode)
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
                        ["prisoner"] = new JsonObject { ["pawnId"] = pawn, ["interaction"] = mode },
                    }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            return results.Count != 1
                ? throw new AcceptanceException($"{label}: expected one result, got {reply.ToJson()}")
                : results[0].AsMap() ?? [];
        }

        async Task AppliedInteraction(string label, string key, string mode, string def)
        {
            var result = await ApplyInteraction(label, key, candidateId, mode).ConfigureAwait(false);
            var effect = result["applied"].Member("applied").Member("observed").Member("prisoner").AsMap();
            if (effect is null || effect["outcome"].AsString() != "held" || effect["interactionDef"].AsString() != def)
            {
                throw new AcceptanceException($"{label}: expected applied held {def} evidence, got {result.ToJson()}");
            }
        }

        // Refusal: an absent prisoner is refused at apply time, before any write.
        missing = await ApplyInteraction("apply-interaction-missing", "prisoner-interaction-missing", "Thing_NoSuchPrisoner", "PRISONER_INTERACTION_REDUCE_RESISTANCE").ConfigureAwait(false);
        if (missing["refused"].AsMap()?["code"].AsString() != "FAILURE_CODE_NOT_FOUND")
        {
            throw new AcceptanceException($"apply-interaction-missing: expected a NOT_FOUND refusal, got {missing.ToJson()}");
        }

        // ReduceResistance: a Core mode beyond the routine Recruit/Maintain pair. Resending the same intent applies again
        // as it stands.
        foreach (var label in new[] { "apply-reduce-resistance", "resend-reduce-resistance" })
        {
            await AppliedInteraction(label, "prisoner-interaction-reduce", "PRISONER_INTERACTION_REDUCE_RESISTANCE", "ReduceResistance").ConfigureAwait(false);
        }

        var afterReduce = await PersonRow("prisoner-after-reduce").ConfigureAwait(false);
        if (afterReduce["interaction"].AsString() != "ReduceResistance")
        {
            throw new AcceptanceException($"prisoner-after-reduce: expected the native interaction to read back ReduceResistance, got {afterReduce.ToJson()}");
        }

        report["reduce_resistance_held"] = true;
        await AppliedInteraction("apply-recruit", "prisoner-interaction-recruit", "PRISONER_INTERACTION_ATTEMPT_RECRUIT", "AttemptRecruit").ConfigureAwait(false);
        var afterSet = await PersonRow("prisoner-after-recruit-order").ConfigureAwait(false);
        if (afterSet["interaction"].AsString() != "AttemptRecruit")
        {
            throw new AcceptanceException($"prisoner-after-recruit-order: expected AttemptRecruit, got {afterSet.ToJson()}");
        }

        // Actual outcome: the fixture recruits through the native recruit-success path; the census reports the admitted
        // colonist, and a further interaction intent is refused because the pawn is no longer a prisoner.
        var recruited = await h.CallAsync("fixture-recruit", "test/population_recruit", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (recruited["success"].AsBool() != true)
        {
            throw new AcceptanceException($"fixture-recruit: population_recruit refused: {recruited.ToJson()}");
        }

        var afterRecruit = await PersonRow("prisoner-after-recruit").ConfigureAwait(false);
        if (afterRecruit["pawn"].AsMap()?["prisoner"].AsBool() == true)
        {
            throw new AcceptanceException($"prisoner-after-recruit: expected the candidate to have left prisoner custody: {afterRecruit.ToJson()}");
        }

        if (afterRecruit["admitted"].AsBool() != true)
        {
            throw new AcceptanceException($"prisoner-after-recruit: expected the candidate to be an admitted colonist: {afterRecruit.ToJson()}");
        }

        var gone = await ApplyInteraction("apply-after-recruit", "prisoner-interaction-after-recruit", candidateId, "PRISONER_INTERACTION_MAINTAIN_ONLY").ConfigureAwait(false);
        if (gone["refused"].AsMap() is null)
        {
            throw new AcceptanceException($"apply-after-recruit: expected a refusal for a recruited colonist, got {gone.ToJson()}");
        }

        report["candidate_recruited"] = true;

        // =================================================================
        // Rescue: the downed, unadmitted friendly guest.
        // =================================================================
        // Re-acquire: the Fast-speed run to observe the capture complete let the colony's own AI issue jobs and run its
        // usual simulation outside this tool's authority scope, which revokes whatever lease was held (see the acquire
        // closure's comment above). The prior lease is gone by now; a fresh one is required before the real rescue
        // dispatch below.
        await Acquire("re-acquire-before-rescue").ConfigureAwait(false);
        var visitorRowBefore = await PawnRow("visitor-before", visitorId).ConfigureAwait(false);
        if (visitorRowBefore["downed"].AsBool() != true)
        {
            throw new AcceptanceException($"visitor-before: expected the fixture guest to be downed: {visitorRowBefore.ToJson()}");
        }

        if (visitorRowBefore["inBed"].AsBool() == true)
        {
            throw new AcceptanceException($"visitor-before: expected the fixture guest not yet admitted to a bed: {visitorRowBefore.ToJson()}");
        }

        await Ordered("custody-rescue", visitorId, "Rescue", "Rescue").ConfigureAwait(false);
        await Until("observe-rescue", visitorId, row => row["inBed"].AsBool() == true).ConfigureAwait(false);
        report["visitor_rescued"] = true;
    }
}
