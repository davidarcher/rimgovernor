namespace RimGovernor.Brain.Policy;

/// <summary>An open bill's native bench and recipe identity.</summary>
public readonly record struct ProjectBill(string Bench, string Recipe);

/// <summary>One owner for the work requirements of accepted and prospective colony work.</summary>
public static class WorkDemandRules
{
    /// <summary>
    /// Resolves each open bill against the fresh bench census: the recipe row on the bill's bench names the
    /// work type and skill floor. A bill whose bench or recipe the census no longer describes, or whose work is
    /// absent, fails the planner boundary with a RequiredFactException.
    /// </summary>
    public static IReadOnlyList<WorkRequirement> BillWork(IReadOnlyList<ProjectBill> bills, IReadOnlyList<GearBench> census)
    {
        if (bills.Count == 0)
        {
            return Array.Empty<WorkRequirement>();
        }
        var benches = new Dictionary<string, GearBench>(StringComparer.Ordinal);
        foreach (var row in census)
        {
            benches[row.Id] = row;
        }
        var output = new List<WorkRequirement>();
        foreach (var bill in bills)
        {
            var exists = benches.TryGetValue(bill.Bench, out var bench);
            if (!exists)
            {
                throw new RequiredFactException("bill bench " + bill.Bench);
            }
            var recipes = bench!.Recipes;
            var found = false;
            foreach (var recipe in recipes)
            {
                if (recipe.Definition != bill.Recipe)
                {
                    continue;
                }
                output = MergeWorkRequirements(output, recipe.RequiredWork);

                found = true;
            }
            if (!found)
            {
                throw new RequiredFactException("bill recipe " + bill.Recipe + " on " + bill.Bench);
            }
        }
        return output;
    }

    /// <summary>Folds rows by work type keeping the highest skill floor, in first-seen order.</summary>
    public static List<WorkRequirement> MergeWorkRequirements(List<WorkRequirement> into, IEnumerable<WorkRequirement> rows)
    {
        foreach (var row in rows)
        {
            var merged = false;
            for (var i = 0; i < into.Count; i++)
            {
                if (into[i].Work == row.Work)
                {
                    into[i] = into[i] with
                    {
                        Minimum = Math.Max(into[i].Minimum, row.Minimum),
                        Skill = into[i].Skill == "" ? row.Skill : into[i].Skill,
                    };
                    merged = true;
                }
            }
            if (!merged)
            {
                into.Add(row);
            }
        }
        return into;
    }

    public static IReadOnlyList<WorkRequirement> ProjectWork(IReadOnlyList<string> names, IReadOnlyList<RimGovernor.Brain.Policy.PlanningDefinition> definitions)
    {
        var byName = new Dictionary<string, RimGovernor.Brain.Policy.PlanningDefinition>(StringComparer.Ordinal);
        foreach (var d in definitions)
        {
            byName[d.Name] = d;
        }
        var minimum = 0;
        foreach (var name in names)
        {
            var exists = byName.TryGetValue(name, out var d);

            if (!exists)
            {
                throw new RequiredFactException("project definition " + name);
            }
            minimum = Math.Max(minimum, (int)d!.ConstructionSkill);
        }
        if (names.Count == 0)
        {
            return Array.Empty<WorkRequirement>();
        }
        return [new WorkRequirement { Work = WorkType.Construction, Minimum = minimum }];
    }

    /// <summary>
    /// Covers the work a pending resource deficit will need before its bill exists: native bill admission
    /// refuses a bill until some colonist has the bench's work type enabled, and BillWork only sees bills
    /// already open, so a freshly built workshop bench would never get a worker. Every standing bench recipe
    /// that is available on its bench and produces a targeted resource contributes its work requirement; a
    /// bench whose recipes are unread is skipped (it cannot host a bill yet either), but a producing recipe
    /// with absent work fails the boundary rather than assigning
    /// against a guess.
    /// </summary>
    public static IReadOnlyList<WorkRequirement> DeficitWork(IReadOnlyDictionary<Resource, long> targets, IReadOnlyList<GearBench> census, Resource wort, Resource beer)
    {
        var output = new List<WorkRequirement>();
        foreach (var row in census)
        {
            foreach (var recipe in row.Recipes)
            {
                if (!recipe.AvailableOn)
                {
                    continue;
                }
                var produces = false;
                foreach (var product in recipe.Products)
                {
                    if (product == wort && targets.GetValueOrDefault(beer) > 0)
                    {
                        produces = true;
                    }
                    if (targets.ContainsKey(product))
                    {
                        produces = true;
                        break;
                    }
                }
                if (!produces)
                {
                    continue;
                }
                output = MergeWorkRequirements(output, recipe.RequiredWork);

            }
        }
        return output;
    }

    /// <summary>
    /// The set of definitions DeficitWork covers benches for: the operator's resource targets while
    /// the economy/resource Need is in deficit, and the replacement needs the equipment census reports (a shirt for a
    /// tattered or missing layer) while the military/equipment Need is unmet. The gear batch previews its bill
    /// against native admission, which refuses a bench nobody works, so the tailoring work type must be enabled
    /// before the bill is proposed, exactly as a resource deficit's bench work is.
    /// </summary>
    public static Dictionary<Resource, long> DeficitTargets(IReadOnlyDictionary<Resource, long> resources, bool resourceDeficit, GearObservation gear)
    {
        var output = new Dictionary<Resource, long>();
        if (resourceDeficit)
        {
            foreach (var (r, n) in resources)
            {
                output[r] = n;
            }
        }
        foreach (var need in GearRules.GearReplacementNeeds(gear))
        {
            output[need] = output.GetValueOrDefault(need) + 1;
        }
        // A useful clothing alternative needs native tailoring enabled before its bill exists.
        // These are work-type requirements, not stock reservations or authority to craft every option.
        foreach (var product in ClothingActivitiesRules.ClothingActivityRequirements(gear).Select(n => new Resource(n.Product)).Distinct())
            output.TryAdd(product, 1);
        return output;
    }

    /// <summary>
    /// The researcher an unfinished research target needs, the same way a bill needs its bench work type:
    /// without it AssignWork leaves Research unowned and the development rank reports labor_unavailable. Empty
    /// when no target is owed or it is already finished.
    /// </summary>
    public static IReadOnlyList<WorkRequirement> ResearchWork(IReadOnlyList<string> needs, ResearchFacts? research)
    {
        var (target, _) = ResearchRules.ResearchConcern(needs, research);
        if (target == "")
        {
            return [];
        }
        // The goal recovers as soon as the project is current; the researcher is owed until it is finished.
        if (research is { } facts)
        {
            foreach (var name in facts.Finished)
            {
                if (name.ToString() == target)
                {
                    return [];
                }
            }
        }
        return [new WorkRequirement { Work = WorkType.Research }];
    }
    public static IReadOnlyList<string> ResearchNeeds(FilingKey concern, IReadOnlyList<string> research, bool equipmentOwed) =>
        concern == MilitaryFamily.Equipment.FilingKey && !equipmentOwed ? [] : research;
}
