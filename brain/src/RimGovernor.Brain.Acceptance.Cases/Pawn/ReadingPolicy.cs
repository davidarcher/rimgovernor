namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/reading-policy</c> (Go <c>reading_policy.go</c>).</summary>
internal static class PawnReadingPolicy
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/reading-policy",
        Scope = "A researcher and an eight-year-old child each get the reading policy labelled with their short name through a ReadingPolicyIntent and PawnSettingsIntent.reading_policy (#1306): the researcher's allows schematics, the child's textbooks only, and neither allows a tome. Native contract: the policy write, the assignment and the policy facts' book kinds and allowed definitions; the choice of books itself is policy/reading_policy_test.go.",
        Start = new FixtureStart { Op = "test/reader_and_child", On = new LabStart() },
        Serve = PawnSupport.WorkSpec,
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var researcher = PawnSupport.Fixture(s, "researcher", "pawns");
        var child = PawnSupport.Fixture(s, "child", "pawns");
        s.Report["researcher"] = researcher;
        s.Report["child"] = child;
        var h = s.Harness;
        await PawnSupport.SettleAsync(
            s,
            async ct =>
            {
                var facts = await PawnSupport.PolicyFactsAsync(h, s, "reading-policy-wait", ct).ConfigureAwait(false);
                return PawnSupport.HoldsAssigned(facts, "reading", researcher) && PawnSupport.HoldsAssigned(facts, "reading", child)
                    ? ("reading policies assigned", true)
                    : ("waiting for both reading policy assignments", false);
            },
            cancellationToken).ConfigureAwait(false);
        h = s.Harness;
        var final = await PawnSupport.PolicyFactsAsync(h, s, "reading-policy", cancellationToken).ConfigureAwait(false);
        var held = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var policy in PawnSupport.Policies(final, "reading"))
        {
            foreach (var pawn in PawnSupport.Holders(policy))
            {
                if (!held.TryGetValue(pawn, out var kinds))
                {
                    kinds = [];
                    held[pawn] = kinds;
                }

                kinds.AddRange(PawnSupport.Allowed(policy).Select(PawnKinds.Book));
                s.Report["policy_" + pawn] = policy["label"].AsString();
            }
        }

        var researcherBooks = held.GetValueOrDefault(researcher) ?? [];
        var childBooks = held.GetValueOrDefault(child) ?? [];
        s.Report["researcher_books"] = string.Join(',', researcherBooks);
        s.Report["child_books"] = string.Join(',', childBooks);
        if (!researcherBooks.Contains(PawnKinds.Schematic) || researcherBooks.Contains(PawnKinds.Tome))
        {
            throw new AcceptanceException($"researcher's reading policy allows [{string.Join(',', researcherBooks)}], want schematics and no tome");
        }

        if (childBooks.Count == 0)
        {
            throw new AcceptanceException("child's reading policy allows no book");
        }

        if (childBooks.Any(kind => kind != PawnKinds.Textbook))
        {
            throw new AcceptanceException($"child's reading policy allows [{string.Join(',', childBooks)}], want textbooks only");
        }
    }
}
