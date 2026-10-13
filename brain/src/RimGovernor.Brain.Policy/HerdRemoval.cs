namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>ReconcileHerdRemoval</c>: compares durable native flags with today's objectives. It has no
    /// controller-history dependency, so Manual edits and save/load use the same rule. Existing valid removals
    /// consume the budget before new work.
    /// </summary>
    public static HusbandryChoice ReconcileHerdRemoval(Fact<EquatableArray<UpkeepAnimal>> animals, HerdPolicy herd, Fact<FoodPlan> food)
    {
        if (!animals.TryGetValue(out var unsorted))
        {
            return new HusbandryChoice { Reason = HusbandryPlanReason.Unknown };
        }
        var rows = unsorted.OrderBy(a => a.Id).ToList();
        var counts = new Dictionary<Resource, long>();
        var sexes = new Dictionary<Resource, long>();
        foreach (var a in rows)
        {
            if (!a.Release.IsKnown || !a.Slaughter.IsKnown)
            {
                return new HusbandryChoice { Reason = HusbandryPlanReason.Unknown };
            }
            counts[a.Definition] = counts.GetValueOrDefault(a.Definition) + 1;
            if (HerdFertile(a))
            {
                var key = SexKey(a);
                sexes[key] = sexes.GetValueOrDefault(key) + 1;
            }
        }
        var foodKnown = food.TryGetValue(out var plan);
        var foodWanted = new HashSet<PawnId>();
        foreach (var e in plan?.Portfolio ?? [])
        {
            if (e.Channel.Kind == CandidateKind.Slaughter && e.Decision == FoodPlanDecision.Open)
            {
                foreach (var a in rows)
                {
                    if (e.Channel.Id == "slaughter:" + a.Id.Value)
                    {
                        foodWanted.Add(a.Id);
                    }
                }
            }
        }
        var unknown = false;
        foreach (var a in rows)
        {
            var release = a.Release.ValueOrDefault;
            var slaughter = a.Slaughter.ValueOrDefault;
            if (!release && !slaughter)
            {
                continue;
            }
            var floor = Math.Max(herd.PopulationMin.GetValueOrDefault(a.Definition), HerdPairSize);
            var capped = herd.PopulationMax.TryGetValue(a.Definition, out var ceiling);
            var sex = SexKey(a);
            // A sterilized animal is no part of a breeding pair.
            var fertile = HerdFertile(a);
            // A layer race keeps its rooster ratio (malesKept); an unknown ratio keeps every fertile male.
            var (_, floorMales, mk) = herd.MalesKept(a.Definition, sexes.GetValueOrDefault(new Resource(a.Definition.Value + "/Female")));
            var pair = fertile
                && ((a.Gender == "Male" && (!mk || sexes.GetValueOrDefault(sex) <= floorMales))
                    || (a.Gender == "Female" && sexes.GetValueOrDefault(sex) <= HerdPairFemales));
            if (herd.Retired.GetValueOrDefault(a.Definition))
            {
                floor = 0;
                pair = false;
            }
            var room = counts.GetValueOrDefault(a.Definition) > floor && !pair;
            var surplus = capped && counts.GetValueOrDefault(a.Definition) > Math.Max(floor, ceiling);
            var keepRelease = release && !slaughter && surplus && !pair;
            var keepSlaughter = slaughter && !release && room && (surplus || foodWanted.Contains(a.Id));
            // An unknown cap (wealth unread) cannot revoke a surplus removal.
            if (!capped && room && !keepSlaughter)
            {
                unknown = true;
                counts[a.Definition]--;
                if (fertile)
                {
                    sexes[sex]--;
                }
                continue;
            }
            if (release && !keepRelease)
            {
                return new HusbandryChoice { Animal = a.Id, Method = HusbandryMethod.CancelRelease };
            }
            if (slaughter && !keepSlaughter)
            {
                // Unknown food demand cannot revoke an otherwise allowed removal.
                if (room && !foodKnown)
                {
                    unknown = true;
                }
                else
                {
                    return new HusbandryChoice { Animal = a.Id, Method = HusbandryMethod.CancelSlaughter };
                }
            }
            counts[a.Definition]--;
            if (fertile)
            {
                sexes[sex]--;
            }
        }
        return new HusbandryChoice { Reason = unknown ? HusbandryPlanReason.Unknown : HusbandryPlanReason.NoDeficit };
    }

    /// <summary>The race-and-sex key the pair counts are kept under (Go <c>a.Definition+"/"+Resource(a.Gender)</c>).</summary>
    private static Resource SexKey(UpkeepAnimal a) => new(a.Definition.Value + "/" + a.Gender);
}
