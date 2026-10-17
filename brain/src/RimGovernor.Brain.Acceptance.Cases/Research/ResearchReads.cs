using RimGovernor.Brain.NativeFacade;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>research/reads</c>: observations_read_research reports the native research state the stage set (one project
/// finished, another part done), a name filter narrows the projects, and the full read repeated after the filtered
/// one is unchanged: the read never writes research.
/// </summary>
internal static class ResearchReads
{
    private const string Finished = "Stonecutting";

    // Deep in the tree: never finished or started on the lab.
    private const string Started = "MultiAnalyzer";
    private const float Progress = 0.25f;

    internal static TypedCase Case() => new()
    {
        Name = "research/reads",
        Scope = "observations_read_research reads the native ResearchManager: a staged finished project reads finished, a staged "
            + "quarter-done project reads its progress fraction, a name filter returns only matching projects, and the full read "
            + "repeated after the filtered read is equal (the read never mutates research).",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Research = [new Fx.StageResearch { Def = Finished }, new Fx.StageResearch { Def = Started, Progress = Progress }],
        },
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        async Task<Observations.ResearchSnapshot> ReadAsync(string label, string? nameContains)
        {
            var reply = await s.CallAsync(label, NativeOps.ReadResearch, new Observations.ResearchRequest
            {
                Scope = s.Scope,
                IncludeLocked = true,
                IncludeFinished = true,
                NameContains = nameContains,
            }, cancellationToken).ConfigureAwait(false);
            return TypedReads.Observed(label, NativeOps.ReadResearch, reply, reply.Outcome?.Observed);
        }

        var full = await ReadAsync("full", null).ConfigureAwait(false);
        var problems = new List<string>();
        var finished = full.Projects.FirstOrDefault(p => p.Project?.DefName == Finished);
        if (finished?.Finished != true || !full.Finished.Contains(Finished))
        {
            problems.Add($"{Finished} not read finished: {finished}");
        }

        var started = full.Projects.FirstOrDefault(p => p.Project?.DefName == Started);
        if (started is not { Finished: false, ProgressFraction: { } fraction } || Math.Abs(fraction - Progress) > 0.01)
        {
            problems.Add($"{Started} not read {Progress} done: {started}");
        }

        var filtered = await ReadAsync("filtered", Started).ConfigureAwait(false);
        if (filtered.Projects.Count == 0 || filtered.Projects.Any(p => p.Project?.DefName?.Contains(Started, StringComparison.OrdinalIgnoreCase) != true))
        {
            problems.Add($"filter \"{Started}\" returned [{string.Join(' ', filtered.Projects.Select(p => p.Project?.DefName))}]");
        }

        var again = await ReadAsync("full-again", null).ConfigureAwait(false);
        if (again != full)
        {
            problems.Add("the full read changed after the filtered read");
        }

        s.RecordWire("finished", finished);
        s.RecordWire("started", started);
        s.Record("projects", full.Projects.Count);
        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}
