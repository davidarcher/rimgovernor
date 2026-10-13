namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/drug-addiction</c> (Go <c>drug_addiction.go</c>).</summary>
internal static class PawnDrugAddiction
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/drug-addiction",
        Scope = "A colonist addicted to luciferium, with luciferium in stock, gets a drug policy that schedules luciferium and allows it for the addiction (#1538). Native contract: the colony item census feeding the planner, the chemical state read and the written scheduled entry; maintain-or-wean choices are policy/drug_policy_test.go.",
        Start = new FixtureStart { Op = "test/luciferium_addict", On = new LabStart() },
        Serve = PawnSupport.WorkSpec,
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var pawn = PawnSupport.Fixture(s, "pawn", "pawn");
        s.Report["pawn"] = pawn;
        var h = s.Harness;
        await PawnSupport.SettleAsync(
            s,
            async ct =>
            {
                var facts = await PawnSupport.PolicyFactsAsync(h, s, "drug-addiction-wait", ct).ConfigureAwait(false);
                return PawnSupport.HoldsAssigned(facts, "drug", pawn) ? ("drug policy assigned", true) : ("waiting for the drug policy assignment", false);
            },
            cancellationToken).ConfigureAwait(false);
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
