using RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>
/// Sowing as coupled work (#3528): one task per (pawn, zone) covering the cells that pawn can sow now, as many as fit the horizon
/// at its speed. A zone is sown by one pawn at a time (the root's exclusive outcome). The zone's crop is read, never chosen; a
/// zone with no crop set has no worker row. Nothing here values the sowing: each task carries the cells its ticks sow as
/// <see cref="ForecastProjectProgress"/> for the investment credit (#3531).
/// </summary>
internal sealed record SowingMethodCandidates(ForecastActivity[] Tasks, ForecastActivityOption[] Options,
    ForecastActivityRoute[] Routes, ForecastCommitment[] Roots,
    IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings, EquatableArray<string> Findings)
{
    internal static SowingMethodCandidates Generate(SowingActivityRead read, long tick, double horizon, WorkResourceClaims claims)
    {
        var tasks = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new List<ForecastActivityRoute>(); var roots = new List<ForecastCommitment>();
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        var findings = new SortedSet<string>(read.Findings, StringComparer.Ordinal);
        var deadline = checked(tick + (long)horizon);
        foreach (var worker in read.Workers.Where(w => w.Cells.Count != 0 && !claims.Pawns.Contains(w.Pawn) && !claims.Targets.Contains(w.Zone))
            .OrderBy(w => w.Zone, StringComparer.Ordinal).ThenBy(w => w.Pawn, StringComparer.Ordinal))
        {
            var perCell = worker.TicksPerCell;
            if (!double.IsFinite(perCell) || perCell <= 0)
            { findings.Add("sowing_mechanics_invalid:" + worker.Pawn + ":" + worker.Zone); continue; }
            var fitting = (int)Math.Min(worker.Cells.Count, Math.Floor(horizon / perCell));
            if (fitting == 0) { findings.Add("sowing_cells_truncated"); continue; }
            if (fitting < worker.Cells.Count) findings.Add("sowing_cells_truncated");
            var first = worker.Cells[0];
            var location = ForecastLocations.Of(first);
            var id = "sow/" + worker.Zone + "/" + worker.Pawn;
            tasks.Add(new(id, location, location, []) { ProjectProgress = new(ForecastProjectProgress.SowPrefix + worker.Zone, 1 / perCell) });
            options.Add(new(id, worker.Pawn, Math.Min(fitting * perCell, horizon), 0) { SkillWork = worker.SkillWork with { WorkAmount = fitting, WorkQuantum = 1 } });
            routes.Add(new(worker.Pawn, "start", location, new RouteTicks.Reachable(worker.ApproachTicks)));
            bindings[(id, worker.Pawn)] = new(id, worker.Pawn, [], PawnActivityKind.Work, worker.Zone, worker.WorkGiver, "Sow",
                fitting, deadline, first) { Destination = first };
            roots.Add(new(id, []) { ActivityCompletions = [id], ExclusiveOutcome = "sow/" + worker.Zone });
        }
        return new(tasks.ToArray(), options.ToArray(), routes.ToArray(), roots.ToArray(), bindings, EquatableArray.From(findings));
    }
}
