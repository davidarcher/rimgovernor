using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>One open production bill's bench and recipe, the pair whose census row names the work type a pawn needs enabled for it.</summary>
internal readonly partial record struct RoundsProjectBill(string Bench, string Recipe);

partial record struct RoundsProjectBill
{
    /// <summary>
    /// Visits every open action of every live plan that is either admitted for the current world or belongs to the root plan
    /// (a cancelled, unresolved native order can still need a qualified builder).
    /// </summary>
    internal static void RoundsOpenActions(IEnumerable<Sto.PlanState> plans, GenerationSnapshot current, Action<DomainAction> visit)
    {
        foreach (var plan in plans)
        {
            if (plan.Retired)
            {
                continue;
            }
            var admissions = new Dictionary<ActionId, GenerationSnapshot>();
            foreach (var a in plan.Admissions)
            {
                admissions[a.Action] = a.Admission.Snapshot;
            }
            foreach (var progress in plan.Progress)
            {
                if (!WorkProgress.Open([progress]))
                {
                    continue;
                }
                var v = progress.View;
                var admitted = admissions.TryGetValue(v.Action, out var scope);
                var selected = v.Plan == current.Plan && v.Revision == current.Revision;
                if (!admitted && !selected)
                {
                    continue;
                }
                if (admitted && (scope.Colony != current.Colony || scope.Load != current.Load || scope.Map != current.Map))
                {
                    continue;
                }
                visit(progress.Action);
            }
        }
    }

    internal static List<string> RoundsProjectDefinitions(IEnumerable<Sto.PlanState> plans, GenerationSnapshot current)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        RoundsOpenActions(plans, current, a =>
        {
            if (a.Building is { } building)
            {
                names.Add(building.Definition);
            }
        });
        var result = names.ToList();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    internal static List<RoundsProjectBill> RoundsProjectBills(IEnumerable<Sto.PlanState> plans, GenerationSnapshot current)
    {
        var seen = new HashSet<RoundsProjectBill>();
        var result = new List<RoundsProjectBill>();
        RoundsOpenActions(plans, current, a =>
        {
            if (a.ProductionBill is not { } bill)
            {
                return;
            }
            var row = new RoundsProjectBill(bill.Bench, bill.Recipe);
            if (seen.Add(row))
            {
                result.Add(row);
            }
        });
        return [.. result.OrderBy(b => b.Bench, StringComparer.Ordinal).ThenBy(b => b.Recipe, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Resolves each open bill against the fresh bench census: the recipe row on the bill's bench names the
    /// work type and skill floor. A bill whose bench or recipe the census no longer describes, or whose work is
    /// absent, fails the planner boundary with a RequiredFactException.
    /// </summary>
    internal static IReadOnlyList<Pol.WorkRequirement> RoundsBillWork(IReadOnlyList<RoundsProjectBill> bills, IReadOnlyList<GearBenchRead> census)
    {
        if (bills.Count == 0)
        {
            return Array.Empty<Pol.WorkRequirement>();
        }
        var benches = new Dictionary<string, Pol.GearBench>(StringComparer.Ordinal);
        foreach (var row in census)
        {
            benches[row.Bench.Id] = row.Bench;
        }
        var output = new List<Pol.WorkRequirement>();
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
    internal static List<Pol.WorkRequirement> MergeWorkRequirements(List<Pol.WorkRequirement> into, IEnumerable<Pol.WorkRequirement> rows)
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

    internal static IReadOnlyList<Pol.WorkRequirement> RoundsProjectWork(IReadOnlyList<string> names, IReadOnlyList<Observation.PlanningDefinition> definitions)
    {
        var byName = new Dictionary<string, Observation.PlanningDefinition>(StringComparer.Ordinal);
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
            return Array.Empty<Pol.WorkRequirement>();
        }
        return [new Pol.WorkRequirement { Work = WorkType.Construction, Minimum = minimum }];
    }

    /// <summary>
    /// Covers the work a pending resource deficit will need before its bill exists: native bill admission
    /// refuses a bill until some colonist has the bench's work type enabled, and RoundsBillWork only sees bills
    /// already open, so a freshly built workshop bench would never get a worker. Every standing bench recipe
    /// that is available on its bench and produces a targeted resource contributes its work requirement; a
    /// bench whose recipes are unread is skipped (it cannot host a bill yet either), but a producing recipe
    /// with absent work fails the boundary rather than assigning
    /// against a guess.
    /// </summary>
    internal static IReadOnlyList<Pol.WorkRequirement> RoundsDeficitWork(IReadOnlyDictionary<Pol.Resource, long> targets, IReadOnlyList<GearBenchRead> census, Pol.Resource wort)
    {
        var output = new List<Pol.WorkRequirement>();
        foreach (var row in census)
        {
            foreach (var recipe in row.Bench.Recipes)
            {
                if (!recipe.AvailableOn)
                {
                    continue;
                }
                var produces = false;
                foreach (var product in recipe.Products)
                {
                    if (product == wort && targets.GetValueOrDefault(new Pol.Resource("Beer")) > 0)
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
    /// The set of definitions RoundsDeficitWork covers benches for: the operator's resource targets while
    /// the economy/resource Need is in deficit, and the replacement needs the equipment census reports (a shirt for a
    /// tattered or missing layer) while the military/equipment Need is unmet. The gear batch previews its bill
    /// against native admission, which refuses a bench nobody works, so the tailoring work type must be enabled
    /// before the bill is proposed, exactly as a resource deficit's bench work is.
    /// </summary>
    internal static Dictionary<Pol.Resource, long> RoundsDeficitTargets(IReadOnlyDictionary<Pol.Resource, long> resources, bool resourceDeficit, Pol.GearObservation gear)
    {
        var output = new Dictionary<Pol.Resource, long>();
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
        return output;
    }

    /// <summary>
    /// The bench-hosted work the review and the work planner both fold into the construction requirement: open
    /// bills (RoundsBillWork) and, while a resource target is in deficit, the standing benches that could
    /// produce it (RoundsDeficitWork). One census read serves both; no bill and no deficit reads nothing. The
    /// review needs the same rows so the admin/work-assignments Need is owed for a deficit the planner will then cover.
    /// </summary>
    internal static async Task<IReadOnlyList<Pol.WorkRequirement>> RoundsBenchWorkAsync(IRoundsWorkBenchSource? benches, GenerationSnapshot snapshot, IReadOnlyList<Sto.PlanState> plans, IReadOnlyDictionary<Pol.Resource, long> targets, bool deficit, Pol.Resource wort, CancellationToken ct)
    {
        var none = Array.Empty<Pol.WorkRequirement>();
        var bills = RoundsProjectBills(plans, snapshot);
        deficit = deficit && targets.Count > 0;
        if (bills.Count == 0 && !deficit)
        {
            return none;
        }
        if (benches is null) throw new RequiredFactException("work bench census");
        var census = await benches.ReadGearBenchesAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false);
        var output = RoundsBillWork(bills, census);
        var merged = output.ToList();
        if (deficit)
        {
            var work = RoundsDeficitWork(targets, census, wort);
            merged = MergeWorkRequirements(merged, work);
        }
        return merged;
    }

    /// <summary>
    /// The research the workshop ladder recorded as gating a bench for a still-targeted resource or active
    /// equipment deficit: the economy/research Need's target. An equipment rung is owed only while the last review
    /// in <paramref name="snapshot"/>'s world found the military/equipment Need unmet (<paramref name="equipmentOwed"/>).
    /// </summary>
    internal static IReadOnlyList<string> RoundsResearchNeeds(StoreJournal journal, Pol.RoundsPolicy p, Pol.ItemFacts items, GenerationSnapshot snapshot, bool equipmentOwed, CancellationToken ct)
    {
        var ladder = journal.LoadProductionLadder(new Sto.World(snapshot.Colony, snapshot.Load, snapshot.Map), ct);
        if (ladder is null)
        {
            return [];
        }
        if (ladder.Concern == MilitaryFamily.Equipment.FilingKey)
        {
            return equipmentOwed ? ladder.Research : [];
        }
        return ladder.Research;
    }

    /// <summary>
    /// The researcher an unfinished research target needs, the same way a bill needs its bench work type:
    /// without it AssignWork leaves Research unowned and the development rank reports labor_unavailable. Empty
    /// when no target is owed or it is already finished.
    /// </summary>
    internal static IReadOnlyList<Pol.WorkRequirement> RoundsResearchWork(IReadOnlyList<string> needs, Pol.ResearchFacts? research)
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
        return [new Pol.WorkRequirement { Work = Pol.WorkType.Research }];
    }
}
