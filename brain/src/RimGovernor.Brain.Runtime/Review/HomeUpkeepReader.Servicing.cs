using RimGovernor.Brain.Policy;
using ServicingActivityRead = RimGovernor.Brain.GameFacts.Observations.ServicingActivityRead;
using ServicingKind = RimGovernor.Brain.GameFacts.Observations.ServicingKind;
using BuildingRow = RimGovernor.Brain.GameFacts.Observations.BuildingState;

namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The maintenance choice's facts (task 5.2), re-derived each review with no owner store (design Decision 4): the servicing
/// census of the colony read, the repairs Need's open and refused orders from the journal, and the pawns' running repair and
/// fix jobs from the pawn census. The power family reads its refuels the same way (task 5.3).
/// </summary>
partial class HomeUpkeepReader
{
    /// <summary>
    /// The native job defs a running repair, breakdown fix and refuel carry (JobDefOf.Repair, JobDefOf.FixBrokenDownBuilding,
    /// JobDefOf.Refuel).
    /// </summary>
    internal const string RepairJob = "Repair", FixJob = "FixBrokenDownBuilding", RefuelJob = "Refuel";

    /// <summary>The read's options joined to the canonical rows, with the stock of each resource a target's work consumes.</summary>
    internal static ServicingCensus ServicingCensusOf(ServicingActivityRead read, Func<string, BuildingRow?> row)
    {
        var component = read.Mechanics.Component;
        return new ServicingCensus([.. ServicingRules.Survey(read, row)])
        {
            Components = read.Stock.SingleOrDefault(s => s.Resource == component) is { } stock
                ? new ServicingComponents.Counted(stock.Count) : new ServicingComponents.Unread("component stock:" + component),
            Truncated = read.Findings.Contains(ServicingRules.Truncated),
            Component = component,
            Stock = read.Stock.ToDictionary(s => s.Resource, s => s.Count, StringComparer.Ordinal),
        };
    }

    /// <summary>The repairs Need's servicing history: its repair and fix Methods, and the pawns' running repair and fix jobs.</summary>
    internal static (IReadOnlyList<ServicingUnderway> Underway, IReadOnlyList<RefusedServicing> Refused) ServicingHistory(
        StoreJournal journal, GenerationSnapshot world, IReadOnlyList<WorkPawn> pawns, CancellationToken call) =>
        ServicingHistory(journal, world, HomeUpkeepFamily.Repairs, [HomeUpkeepFamily.RepairMethod, HomeUpkeepFamily.FixMethod], pawns,
            [(RepairJob, ServicingKind.Repair), (FixJob, ServicingKind.FixBreakdown)], call);

    /// <summary>
    /// The servicing underway and refused in <paramref name="world"/>: the latest attempt of each of <paramref name="need"/>'s Methods
    /// named by one of <paramref name="prefixes"/> is underway while its action is open, and refused while the refusal budget
    /// refuses it (with the budget's reason); then each pawn's running job of one of <paramref name="jobs"/> on a thing. An issued
    /// order is listed before a running job.
    /// </summary>
    internal static (IReadOnlyList<ServicingUnderway> Underway, IReadOnlyList<RefusedServicing> Refused) ServicingHistory(
        StoreJournal journal, GenerationSnapshot world, NeedId need, IReadOnlyList<string> prefixes, IReadOnlyList<WorkPawn> pawns,
        IReadOnlyList<(string Job, ServicingKind Kind)> jobs, CancellationToken call)
    {
        var owner = journal.LoadNeed(need, world, call);
        var underway = new List<ServicingUnderway>();
        var refused = new List<RefusedServicing>();
        foreach (var method in journal.OwnerMethodHistory(owner, call).Select(m => m.Method).Distinct().OrderBy(m => m.Value, StringComparer.Ordinal))
        {
            if (!prefixes.Any(prefix => method.Value.StartsWith(prefix, StringComparison.Ordinal))) continue;
            if (journal.OwnerMethodPlan(owner, method, call) is not { } latest) continue;
            var plan = journal.LoadPlan(latest, call);
            if (plan.Progress.FirstOrDefault(p => p.Action.Repair is not null || p.Action.RecoveryService is not null) is not { } progress) continue;
            var (target, kind, pawn) = progress.Action.Repair is { } repair
                ? (repair.Structure, ServicingKind.Repair, repair.Pawn.Value)
                : (progress.Action.RecoveryService!.Thing, progress.Action.RecoveryService.Method == RecoveryMethod.Refuel ? ServicingKind.Refuel : ServicingKind.FixBreakdown,
                    progress.Action.RecoveryService.Pawn.Value);
            if (!plan.Retired && WorkProgress.Open([progress]))
            {
                underway.Add(new(target, kind, pawn, true));
            }
            else if (progress.View.Stage == Stage.Unsuccessful)
            {
                var budget = ReviewRefusalBudget.RefusalLedger(journal, [latest], _ => method.Value, call);
                var (allowed, why) = budget.Allowed(method.Value, ReviewRefusalBudget.BudgetWorld(world));
                if (!allowed) refused.Add(new(target, kind, pawn, ReviewRefusalBudget.ReasonToken(why.Reason)));
            }
        }
        foreach (var pawn in pawns)
        {
            var job = pawn.Job;
            if (jobs.FirstOrDefault(j => j.Job == job.Def) is { Job: not null } running && job.Target.Thing is { Length: > 0 } thing)
            {
                underway.Add(new(thing, running.Kind, pawn.Id.Value, false));
            }
        }
        return (underway, refused);
    }
}
