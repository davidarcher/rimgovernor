using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>Reads open work from the journal and its native bench census.</summary>
internal static class ProjectBillReads
{
    /// <summary>
    /// Visits every open action of every live plan that is either admitted for the current world or belongs to the root plan
    /// (a cancelled, unresolved native order can still need a qualified builder).
    /// </summary>
    internal static void OpenActions(IEnumerable<Sto.PlanState> plans, GenerationSnapshot current, Action<DomainAction> visit)
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

    internal static List<string> ProjectDefinitions(IEnumerable<Sto.PlanState> plans, GenerationSnapshot current)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        OpenActions(plans, current, a =>
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

    internal static List<ProjectBill> ProjectBills(IEnumerable<Sto.PlanState> plans, GenerationSnapshot current)
    {
        var seen = new HashSet<ProjectBill>();
        var result = new List<ProjectBill>();
        OpenActions(plans, current, a =>
        {
            if (a.ProductionBill is not { } bill)
            {
                return;
            }
            var row = new ProjectBill(bill.Bench, bill.Recipe);
            if (seen.Add(row))
            {
                result.Add(row);
            }
        });
        return [.. result.OrderBy(b => b.Bench, StringComparer.Ordinal).ThenBy(b => b.Recipe, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The bench-hosted work the review and the work planner both fold into the construction requirement: open
    /// bills (BillWork) and, while a resource target is in deficit, the standing benches that could
    /// produce it (DeficitWork). One census read serves both; no bill and no deficit reads nothing. The
    /// review needs the same rows so the admin/work-assignments Need is owed for a deficit the planner will then cover.
    /// </summary>
    internal static async Task<IReadOnlyList<Pol.WorkRequirement>> BenchWorkAsync(IReviewWorkBenchSource? benches, GenerationSnapshot snapshot, IReadOnlyList<Sto.PlanState> plans, IReadOnlyDictionary<Pol.Resource, long> targets, bool deficit, Pol.Resource wort, CancellationToken ct)
    {
        var none = Array.Empty<Pol.WorkRequirement>();
        var bills = ProjectBills(plans, snapshot);
        deficit = deficit && targets.Count > 0;
        if (bills.Count == 0 && !deficit)
        {
            return none;
        }
        if (benches is null) throw new RequiredFactException("work bench census");
        var read = await benches.ReadGearBenchesAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false);
        var census = read.Select(row => row.Bench).ToArray();
        var output = WorkDemandRules.BillWork(bills, census);
        var merged = output.ToList();
        if (deficit)
        {
            var work = WorkDemandRules.DeficitWork(targets, census, wort, new Resource(nameof(RimWorld.ThingDefOf.Beer)));
            merged = WorkDemandRules.MergeWorkRequirements(merged, work);
        }
        return merged;
    }

    /// <summary>
    /// The research the workshop ladder recorded as gating a bench for a still-targeted resource or active
    /// equipment deficit: the economy/research Need's target. An equipment rung is owed only while the last review
    /// in <paramref name="snapshot"/>'s world found the military/equipment Need unmet (<paramref name="equipmentOwed"/>).
    /// </summary>
    internal static IReadOnlyList<string> ResearchNeeds(StoreJournal journal, GenerationSnapshot snapshot, bool equipmentOwed, CancellationToken ct)
    {
        var ladder = journal.LoadProductionLadder(new Sto.World(snapshot.Colony, snapshot.Load, snapshot.Map), ct);
        if (ladder is null)
        {
            return [];
        }
        return WorkDemandRules.ResearchNeeds(ladder.Concern, ladder.Research, equipmentOwed);
    }

}
