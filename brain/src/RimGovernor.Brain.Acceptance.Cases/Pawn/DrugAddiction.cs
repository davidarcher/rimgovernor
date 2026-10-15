namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/drug-addiction</c> (Go <c>drug_addiction.go</c>).</summary>
internal static class PawnDrugAddiction
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/drug-addiction",
        Scope = "Explicitly configure a luciferium policy on the prepared addicted pawn, verify native assignment and scheduled/addiction permissions, and observe unsupported voluntary-state evidence. This preserves native operation/readback coverage without an automatic dose scheduler or a claim of successful treatment.",
        Start = new FixtureStart { Op = "test/luciferium_addict", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var pawn = PawnSupport.Fixture(s, "pawn", "pawn");
        s.Report["pawn"] = pawn;
        var h = s.Harness;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "drug-addiction-authority", s.Identity, cancellationToken).ConfigureAwait(false);
        await ExplicitDrugPolicy.AssignAsync(s, pawn, "Explicit addiction", [new() { DrugDef = "Luciferium", AllowScheduled = true, AllowedForAddiction = true, DaysFrequency = 4, OnlyIfMoodBelow = 1, OnlyIfJoyBelow = 1 }], cancellationToken).ConfigureAwait(false);
        await ExplicitDrugPolicy.RequireUnsupportedAsync(s, pawn, "Luciferium", cancellationToken).ConfigureAwait(false);
        h = s.Harness;
        var final = await PawnSupport.PolicyFactsAsync(h, s, "drug-addiction", cancellationToken).ConfigureAwait(false);
        foreach (var policy in PawnSupport.Policies(final, "drug"))
        {
            if (!PawnSupport.Holders(policy).Contains(pawn, StringComparer.Ordinal))
            {
                continue;
            }

            s.Report["policy"] = policy["label"].AsString();
            foreach (var raw in policy["drugEntries"].AsSlice())
            {
                var entry = raw.AsMap() ?? [];
                if (entry["drugDef"].AsString() != "Luciferium")
                {
                    continue;
                }

                s.Report["luciferium"] = entry.ToJson();
                if (entry["allowScheduled"].AsBool() != true || entry["allowedForAddiction"].AsBool() != true)
                {
                    throw new AcceptanceException($"luciferium entry {entry.ToJson()} does not schedule and allow for addiction");
                }

                return;
            }

            throw new AcceptanceException($"drug policy \"{policy["label"].AsString()}\" has no luciferium entry");
        }

        throw new AcceptanceException($"{pawn} holds no observed drug policy");
    }
}
