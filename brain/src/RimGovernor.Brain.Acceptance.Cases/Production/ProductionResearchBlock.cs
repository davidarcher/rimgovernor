using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/research-block</c> (#3527): the coupled path installs a one-hour research block for a researcher at a
/// powered bench, and the current project's progress rises. The researcher's Research priority is off, so vanilla work
/// scheduling never researches: only an installed block can move the project. A goal makes the project current.
/// </summary>
internal static class ProductionResearchBlock
{
    // A Neolithic project with no prerequisite that Crashlanded does not start finished (Stonecutting is ClassicStart),
    // half done: progress cannot finish it inside the budget.
    private const string Project = "Brewing";
    private const float Staged = 0.5f;

    internal static TypedCase Case() => new()
    {
        Name = "production/research-block",
        Scope = "Under the served brain, a colonist whose Research priority is off, at a staged bench with a current project, is given "
            + "a research block by the coupled path, and the project's native progress rises above its staged value. Vanilla "
            + "scheduling cannot research for this colonist. A unit test cannot prove the native job, reservation or progress.",
        Ticks = 12_000,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Research = [new Fx.StageResearch { Def = Project, Progress = Staged }],
            Site = new Fx.StageSite
            {
                Width = 9,
                Height = 7,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unfogged],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 9, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
            Buildings = [new Fx.StageBuilding { Name = "bench", Def = "SimpleResearchBench", Stuff = "WoodLog", Cells = [new(4, 3)] }],
            Pawns = [new Fx.StagePawn { Name = "researcher", Colonist = 0, Work = new Dictionary<string, int> { ["Research"] = 0 } }],
        },
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["goals", "research", "work"], Prefix = "research-block" },
        Run = RunAsync,
    };

    private static async Task<RimGovernor.Brain.GameFacts.Observations.ResearchProject?> ReadAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadResearch, new RimGovernor.Brain.GameOrders.Observations.ResearchRequest
        {
            Scope = s.Scope,
            IncludeLocked = true,
            IncludeFinished = true,
            NameContains = Project,
        }, cancellationToken).ConfigureAwait(false);
        return reply.Projects.FirstOrDefault(p => p.Project.DefName == Project);
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var before = await ReadAsync(s, "before", cancellationToken).ConfigureAwait(false);
        if (before is not { Finished: false })
        {
            throw new AcceptanceException($"{Project} is not staged unfinished: {before}");
        }

        await GoalReviews.SeedAsync(s, GoalReviews.Empty(s).WithNewGoal(new GoalRecord { Kind = GoalKind.ResearchProject, Target = Project }), cancellationToken).ConfigureAwait(false);

        var advanced = false;
        _ = await s.ServeUntilAsync("research-block", s.TicksLeft, async ct =>
        {
            var project = await ReadAsync(s, "observe", ct).ConfigureAwait(false);
            advanced = project is { Current: true } && project.ProgressFraction > before.ProgressFraction;
            return (string.Create(CultureInfo.InvariantCulture, $"{project?.ProgressFraction} {project?.Current}"), advanced);
        }, cancellationToken).ConfigureAwait(false);

        if (!advanced)
        {
            throw new AcceptanceException($"{Project} never advanced past {before.ProgressFraction}");
        }
    }
}
