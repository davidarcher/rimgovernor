using RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>
/// Sowing as coupled work (#3528): one task per (pawn, zone) covering the cells that pawn can sow now, as many as fit the horizon
/// at its speed. A zone is sown by one pawn at a time (the root's exclusive outcome). The zone's crop is read, never chosen; a
/// zone with no crop set has no worker row. Nothing here values the sowing: a root carries no consequence until the project
/// graph credits it.
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
        if (!read.Known) findings.Add("sowing_observation_unknown");
        var deadline = checked(tick + (long)horizon);
        foreach (var worker in read.Workers.Where(w => w.Cells.Count != 0 && !claims.Pawns.Contains(w.Pawn) && !claims.Targets.Contains(w.Zone))
            .OrderBy(w => w.Zone, StringComparer.Ordinal).ThenBy(w => w.Pawn, StringComparer.Ordinal))
        {
            if (!worker.ApproachTicks.IsKnown || !worker.TicksPerCell.TryGetValue(out var perCell) || !double.IsFinite(perCell) || perCell <= 0)
            { findings.Add("sowing_mechanics_unknown:" + worker.Pawn + ":" + worker.Zone); continue; }
            var fitting = (int)Math.Min(worker.Cells.Count, Math.Floor(horizon / perCell));
            if (fitting == 0) { findings.Add("sowing_cells_truncated"); continue; }
            if (fitting < worker.Cells.Count) findings.Add("sowing_cells_truncated");
            var first = worker.Cells[0];
            var location = ForecastLocations.Of(first);
            var id = "sow/" + worker.Zone + "/" + worker.Pawn;
            tasks.Add(new(id, location, location, []));
            options.Add(new(id, worker.Pawn, Known(Math.Min(fitting * perCell, horizon)), Known(0d)));
            routes.Add(new(worker.Pawn, "start", location, worker.ApproachTicks));
            bindings[(id, worker.Pawn)] = new(id, worker.Pawn, [], PawnActivityKind.Work, worker.Zone, worker.WorkGiver, "Sow",
                fitting, deadline, first) { Destination = first };
            roots.Add(new(id, []) { ActivityCompletions = [id], ExclusiveOutcome = "sow/" + worker.Zone });
        }
        return new(tasks.ToArray(), options.ToArray(), routes.ToArray(), roots.ToArray(), bindings, EquatableArray.From(findings));
    }
}
