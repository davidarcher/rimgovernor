using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>medical/surgery-intent</c> (Go <c>surgeryintent.go</c>).</summary>
internal static class SurgeryIntentCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "medical/surgery-intent",
        Scope = "A medical ProductionBillIntent (patient set) on Actions/Apply (#1162): a peg-leg bill on a colonist missing a leg is QUEUED past an "
            + "unrelated bill, a resent intent applies again with the same Bill_Medical, a surgeon-restricted resend (#1253) applies again twice, and organ removal on a "
            + "prisoner is refused without acknowledge_violation.",
        Start = new FixtureStart { Op = "test/surgery_intent_prepare", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(2),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var report = s.Report;
        var patient = prepared["patientId"].AsString();
        var prisoner = prepared["prisonerId"].AsString();
        var surgeon = prepared["surgeonId"].AsString();
        if (patient.Length == 0 || prisoner.Length == 0 || surgeon.Length == 0)
        {
            throw new AcceptanceException($"prepare: missing patient or prisoner: {prepared.ToJson()}");
        }

        // The fixture edits health outside authority, so authority is granted after it.
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> Apply(string key, JsonObject surgery)
        {
            var reply = await h.WireAsync(
                key,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = key, ["productionBill"] = surgery }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            if (results.Count != 1)
            {
                throw new AcceptanceException($"{key}: expected one result, got {reply.ToJson()}");
            }

            return results[0].AsMap() ?? [];
        }

        async Task<string> Queued(string key, string by)
        {
            var intent = new JsonObject
            {
                ["patient"] = new JsonObject { ["id"] = patient },
                ["recipeDef"] = "InstallPegLeg",
                ["partIndex"] = prepared["part"].Copy(),
            };
            if (by.Length > 0)
            {
                intent["surgeon"] = new JsonObject { ["id"] = by };
            }

            var result = await Apply(key, intent).ConfigureAwait(false);
            var effect = CaseSupport.AppliedEffect(result, "surgeryBill") ?? [];
            var bill = effect["bill"].RefId();
            if (effect["state"].AsString() != "SURGERY_STATE_QUEUED" || bill.Length == 0 || effect["pawnId"].AsString() != patient)
            {
                throw new AcceptanceException($"{key}: expected a QUEUED surgery bill, got {result.ToJson()}");
            }

            return bill;
        }

        var first = await Queued("surgery-queue", "").ConfigureAwait(false);
        var again = await Queued("surgery-requeue", "").ConfigureAwait(false);
        if (again != first)
        {
            throw new AcceptanceException($"re-apply queued a second bill {again} beside {first}");
        }

        // A surgeon restricts the queued bill; the same surgeon again applies again with the same bill.
        foreach (var key in new[] { "surgery-surgeon", "surgery-surgeon-again" })
        {
            var bill = await Queued(key, surgeon).ConfigureAwait(false);
            if (bill != first)
            {
                throw new AcceptanceException($"{key}: surgeon re-apply queued bill {bill} beside {first}");
            }
        }

        report["bill"] = first;
        var result2 = await Apply(
            "surgery-violation",
            new JsonObject
            {
                ["patient"] = new JsonObject { ["id"] = prisoner },
                ["recipeDef"] = "RemoveBodyPart",
                ["partIndex"] = prepared["kidney"].Copy(),
            }).ConfigureAwait(false);
        if (result2["refused"].AsMap() is not { } refused || !refused["reason"].AsString().Contains("violation", StringComparison.Ordinal))
        {
            throw new AcceptanceException($"unacknowledged organ removal: expected a violation refusal, got {result2.ToJson()}");
        }

        report["violation_refusal"] = refused["reason"].Copy();
    }
}
