using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>
/// One block of research work per (pawn, bench) at the current project. The block is one hour: the horizon is at most a
/// day and every review re-plans, so a longer commitment would only hold the bench against a better choice. The provider
/// never picks the project (the research Concern owns that) and values nothing: the block's progress rides on the task
/// as <see cref="ForecastProjectProgress"/> for the investment credit to read.
/// </summary>
internal sealed record ResearchMethodCandidates(ForecastActivity[] Tasks, ForecastActivityOption[] Options,
    ForecastCommitment[] Roots, IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings,
    EquatableArray<string> Findings, bool Truncated)
{
    internal const double ResearchBlockTicks = GameTime.TicksPerHour;
    const int MaxTasks = 64;

    internal static ResearchMethodCandidates Generate(ResearchActivityRead read, double horizon, WorkResourceClaims claims, long deadline)
    {
        var tasks = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var roots = new List<ForecastCommitment>(); var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        var findings = new SortedSet<string>(read.Findings, StringComparer.Ordinal); var truncated = false;
        foreach (var worker in read.Workers)
        {
            if (horizon < ResearchBlockTicks) { findings.Add("research_block_exceeds_horizon"); break; }
            if (claims.Pawns.Contains(worker.Pawn) || claims.Targets.Contains(worker.Bench)) continue;
            if (!(worker.PointsPerTick > 0)) { findings.Add("research_speed_none:" + worker.Pawn); continue; }
            if (worker.SkillWork is null) findings.Add("productive_learning_unknown:research:" + worker.Pawn);
            if (tasks.Count >= MaxTasks) { truncated = true; break; }
            var id = "research/" + worker.Pawn + "/" + worker.Bench;
            var location = ForecastLocations.Of(worker.Location);
            tasks.Add(new(id, location, location, []) { ChoiceIdentity = "research/" + worker.Pawn,
                ProjectProgress = new(worker.Project, worker.PointsPerTick) });
            options.Add(new(id, worker.Pawn, ResearchBlockTicks, 0) { Equipment = [worker.Bench], SkillWork = worker.SkillWork });
            bindings[(id, worker.Pawn)] = new(id, worker.Pawn, [], PawnActivityKind.Work, worker.Bench, worker.WorkGiver, worker.Job,
                0, deadline, worker.Location);
            roots.Add(new(id, []) { ActivityCompletions = [id], ExclusiveOutcome = "research/" + worker.Pawn });
        }
        return new(tasks.ToArray(), options.ToArray(), roots.ToArray(), bindings, EquatableArray.From(findings), truncated);
    }
}
