namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/drug-policy</c> (Go <c>drug_policy.go</c>).</summary>
internal static class PawnDrugPolicy
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/drug-policy",
        Scope = "Explicit native drug-policy writes and assignments preserve distinct adult joy permission and empty child policy. A separately configured preventive schedule remains an observed unsupported voluntary state. No automatic drug-policy generator runs; pure tests cannot prove native assignment/readback or blockers.",
        Start = new FixtureStart { Op = "test/drinker_and_child", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var adult = PawnSupport.Fixture(s, "adult", "pawns");
        var child = PawnSupport.Fixture(s, "child", "pawns");
        s.Report["adult"] = adult;
        s.Report["child"] = child;
        var h = s.Harness;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "drug-policy-authority", s.Identity, cancellationToken).ConfigureAwait(false);
        await ExplicitDrugPolicy.AssignAsync(s, adult, "Explicit adult", [new() { DrugDef = "Beer", AllowedForJoy = true, DaysFrequency = 1, OnlyIfMoodBelow = 1, OnlyIfJoyBelow = 1 }], cancellationToken).ConfigureAwait(false);
        await ExplicitDrugPolicy.AssignAsync(s, child, "Explicit child", [], cancellationToken).ConfigureAwait(false);
        h = s.Harness;
        var final = await PawnSupport.PolicyFactsAsync(h, s, "drug-policy", cancellationToken).ConfigureAwait(false);
        var joy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in PawnSupport.Policies(final, "drug"))
        {
            foreach (var pawn in PawnSupport.Holders(policy))
            {
                _ = held.Add(pawn);
                foreach (var raw in policy["drugEntries"].AsSlice())
                {
                    var entry = raw.AsMap() ?? [];
                    if (entry["allowedForJoy"].AsBool() == true)
                    {
                        if (!joy.TryGetValue(pawn, out var list))
                        {
                            list = [];
                            joy[pawn] = list;
                        }

                        list.Add(entry["drugDef"].AsString());
                    }

                    if (entry["allowedForAddiction"].AsBool() == true || entry["allowScheduled"].AsBool() == true || PawnSupport.Count(entry["takeToInventory"]) != 0)
                    {
                        throw new AcceptanceException($"{pawn}'s drug policy \"{policy["label"].AsString()}\" allows more than joy: {entry.ToJson()}");
                    }
                }

                s.Report["policy_" + pawn] = policy["label"].AsString();
            }
        }

        var adultJoy = joy.GetValueOrDefault(adult) ?? [];
        var childJoy = joy.GetValueOrDefault(child) ?? [];
        s.Report["adult_joy"] = string.Join(',', adultJoy);
        s.Report["child_joy"] = string.Join(',', childJoy);
        if (!held.Contains(adult) || !adultJoy.Contains("Beer", StringComparer.Ordinal))
        {
            throw new AcceptanceException($"adult's drug policy allows [{string.Join(',', adultJoy)}] for joy, want beer");
        }

        if (!held.Contains(child) || childJoy.Count != 0)
        {
            throw new AcceptanceException($"child's drug policy allows [{string.Join(',', childJoy)}] for joy, want none");
        }
        await ExplicitDrugPolicy.AssignAsync(s, adult, "Explicit prevention", [new() { DrugDef = "Penoxycyline", AllowScheduled = true, DaysFrequency = 5, OnlyIfMoodBelow = 1, OnlyIfJoyBelow = 1 }], cancellationToken).ConfigureAwait(false);
        await ExplicitDrugPolicy.RequireUnsupportedAsync(s, adult, "Penoxycyline", cancellationToken).ConfigureAwait(false);
    }
}
