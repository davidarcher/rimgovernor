namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/drug-policy</c> (Go <c>drug_policy.go</c>).</summary>
internal static class PawnDrugPolicy
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/drug-policy",
        Scope = "An adult with no chemical trait, tolerance or addiction and an eight-year-old child each get the drug policy labelled with their short name through a DrugPolicyIntent and PawnSettingsIntent.drug_policy (#1537): the adult's allows beer for joy, the child's allows nothing. Native contract: the policy write, the assignment and the policy facts' drug entries; the choice of entries itself is policy/drug_policy_test.go.",
        Start = new FixtureStart { Op = "test/drinker_and_child", On = new LabStart() },
        Serve = PawnSupport.WorkSpec,
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
        await PawnSupport.SettleAsync(
            s,
            async ct =>
            {
                var facts = await PawnSupport.PolicyFactsAsync(h, s, "drug-policy-wait", ct).ConfigureAwait(false);
                return PawnSupport.HoldsAssigned(facts, "drug", adult) && PawnSupport.HoldsAssigned(facts, "drug", child)
                    ? ("drug policies assigned", true)
                    : ("waiting for both drug policy assignments", false);
            },
            cancellationToken).ConfigureAwait(false);
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
    }
}
