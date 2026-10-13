namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>pawn/animal-food-policy</c> (Go <c>animal_food_policy.go</c>).</summary>
internal static class PawnAnimalFoodPolicy
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "pawn/animal-food-policy",
        Scope = "A named tame dog gets the food policy labelled with its name through FoodPolicyIntent and PawnSettingsIntent.food_policy (#1543): it allows kibble and never a meal. Native contract: the FoodEater read (race edibility), the kibble kind, the assignment to an animal's food tracker; the per-kind diets are policy/food_policy_test.go.",
        Start = new FixtureStart { Op = "test/tame_dog", On = new LabStart() },
        NoKeep = true,
        Serve = PawnSupport.WorkSpec,
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static readonly string[] Meals = [PawnKinds.MealAwful, PawnKinds.MealSimple, PawnKinds.MealFine, PawnKinds.MealLavish];

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var dog = PawnSupport.Fixture(s, "animal", "animal");
        s.Report["animal"] = dog;
        var h = s.Harness;
        await PawnSupport.SettleAsync(
            s,
            async ct =>
            {
                var facts = await PawnSupport.PolicyFactsAsync(h, s, "animal-food-policy-wait", ct).ConfigureAwait(false);
                return PawnSupport.HoldsAssigned(facts, "food", dog) ? ("animal food policy assigned", true) : ("waiting for the animal's food policy assignment", false);
            },
            cancellationToken).ConfigureAwait(false);
        h = s.Harness;
        var final = await PawnSupport.PolicyFactsAsync(h, s, "animal-food-policy", cancellationToken).ConfigureAwait(false);
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in PawnSupport.Policies(final, "food"))
        {
            if (!PawnSupport.Holders(policy).Contains(dog, StringComparer.Ordinal))
            {
                continue;
            }

            s.Report["policy"] = policy["label"].AsString();
            foreach (var def in PawnSupport.Allowed(policy))
            {
                _ = held.Add(PawnKinds.Food(def));
            }
        }

        s.Report["foods"] = PawnSupport.Joined(held);
        if (!held.Contains(PawnKinds.Kibble))
        {
            throw new AcceptanceException($"animal's food policy allows [{PawnSupport.Joined(held)}], want kibble");
        }

        if (Meals.Any(held.Contains))
        {
            throw new AcceptanceException($"animal's food policy allows [{PawnSupport.Joined(held)}], want no meal");
        }
    }
}
