namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/food-policy</c> (Go <c>food_policy.go</c>).</summary>
internal static class PawnFoodPolicy
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/food-policy",
        Scope = "A cannibal and a vegetarian each get the food policy labelled with their short name through a FoodPolicyIntent and PawnSettingsIntent.food_policy (#1541): the cannibal's allows human meat, the vegetarian's no meat, and they differ; neither allows the travel reserve (Pemmican, #1542). Native contract: the policy write, the assignment, the policy facts' food kinds and allowed definitions, and the Ideology precept read; the diets themselves are policy/food_policy_test.go.",
        Start = new FixtureStart { Op = "test/cannibal_and_vegetarian", On = new LabStart() },
        Expansions = ["ludeon.rimworld.ideology"],
        NoKeep = true,
        Serve = PawnSupport.WorkSpec,
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static readonly string[] Meats = [PawnKinds.HumanMeat, PawnKinds.RawMeat, PawnKinds.InsectMeat];

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var cannibal = PawnSupport.Fixture(s, "cannibal", "pawns");
        var vegetarian = PawnSupport.Fixture(s, "vegetarian", "pawns");
        s.Report["cannibal"] = cannibal;
        s.Report["vegetarian"] = vegetarian;
        var h = s.Harness;
        await PawnSupport.SettleAsync(
            s,
            async ct =>
            {
                var facts = await PawnSupport.PolicyFactsAsync(h, s, "food-policy-wait", ct).ConfigureAwait(false);
                return PawnSupport.HoldsAssigned(facts, "food", cannibal) && PawnSupport.HoldsAssigned(facts, "food", vegetarian)
                    ? ("food policies assigned", true)
                    : ("waiting for both food policy assignments", false);
            },
            cancellationToken).ConfigureAwait(false);
        h = s.Harness;
        var final = await PawnSupport.PolicyFactsAsync(h, s, "food-policy", cancellationToken).ConfigureAwait(false);
        var held = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var policyOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var reserve = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var policy in PawnSupport.Policies(final, "food"))
        {
            foreach (var pawn in PawnSupport.Holders(policy))
            {
                var kinds = new HashSet<string>(StringComparer.Ordinal);
                held[pawn] = kinds;
                foreach (var def in PawnSupport.Allowed(policy))
                {
                    _ = kinds.Add(PawnKinds.Food(def));
                    if (def is "Pemmican" or "MealSurvivalPack")
                    {
                        reserve[pawn] = def;
                    }
                }

                policyOf[pawn] = policy["id"].AsString();
                s.Report["policy_" + pawn] = policy["label"].AsString();
            }
        }

        var cannibalKinds = held.GetValueOrDefault(cannibal) ?? [];
        var vegetarianKinds = held.GetValueOrDefault(vegetarian) ?? [];
        s.Report["cannibal_foods"] = PawnSupport.Joined(cannibalKinds);
        s.Report["vegetarian_foods"] = PawnSupport.Joined(vegetarianKinds);
        var cannibalPolicy = policyOf.GetValueOrDefault(cannibal, "");
        if (cannibalPolicy.Length == 0 || cannibalPolicy == policyOf.GetValueOrDefault(vegetarian, ""))
        {
            throw new AcceptanceException($"cannibal and vegetarian share food policy \"{cannibalPolicy}\"");
        }

        if (reserve.Count > 0)
        {
            throw new AcceptanceException($"home food policies allow the travel reserve: {string.Join(',', reserve.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value))}");
        }

        if (!cannibalKinds.Contains(PawnKinds.HumanMeat))
        {
            throw new AcceptanceException($"cannibal's food policy allows [{PawnSupport.Joined(cannibalKinds)}], want human meat");
        }

        if (Meats.Any(vegetarianKinds.Contains))
        {
            throw new AcceptanceException($"vegetarian's food policy allows [{PawnSupport.Joined(vegetarianKinds)}], want no meat");
        }

        if (!vegetarianKinds.Contains(PawnKinds.MealSimple))
        {
            throw new AcceptanceException($"vegetarian's food policy allows [{PawnSupport.Joined(vegetarianKinds)}], want simple meals");
        }
    }
}
