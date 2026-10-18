using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>goals/research-goal</c> (#3504): a <c>ResearchProject</c> goal in the save's <c>family/goals</c> blob makes the served
/// brain select that project at the staged research bench, and the goal's <c>goal_review</c> rows go Pursuing then Met.
/// </summary>
internal static class GoalsResearch
{
    // A Neolithic project with no prerequisite: cheap, and left nearly done so the colonist's work finishes it inside the budget.
    private const string Project = "Stonecutting";
    private const float Staged = 0.9f;

    private const int GoalId = 1;

    internal static TypedCase Case() => new()
    {
        Name = "goals/research-goal",
        Scope = "Under the served brain, a ResearchProject goal in the save's goals blob makes the brain select that unfinished "
            + "project (a native research read shows it current), and the goal's goal_review rows go Pursuing and then Met "
            + "when the colonist finishes it at the staged bench. A unit test cannot prove the native selection or the finish.",
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
            Pawns = [new Fx.StagePawn { Name = "researcher", Colonist = 0, Work = new Dictionary<string, int> { ["Research"] = 1 } }],
        },
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["goals", "research", "work"], Prefix = "goals-research" },
        Run = RunAsync,
    };

    private static async Task<Observations.ResearchSnapshot> ReadAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadResearch, new Observations.ResearchRequest
        {
            Scope = s.Scope,
            IncludeLocked = true,
            IncludeFinished = true,
            NameContains = Project,
        }, cancellationToken).ConfigureAwait(false);
        return TypedReads.Observed(label, NativeOps.ReadResearch, reply, reply.Outcome?.Observed);
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        // The precondition: the project is unfinished and nothing is being researched, so any selection is the brain's.
        var before = await ReadAsync(s, "before", cancellationToken).ConfigureAwait(false);
        var row = before.Projects.FirstOrDefault(p => p.Project?.DefName == Project);
        if (row is not { Finished: false, Current: false })
        {
            throw new AcceptanceException($"{Project} is not staged unfinished and idle: {row}");
        }

        if (before.Slots.Any(slot => !string.IsNullOrEmpty(slot.CurrentProject)))
        {
            throw new AcceptanceException($"a project is already current: {string.Join(' ', before.Slots.Select(slot => slot.CurrentProject))}");
        }

        await GoalReviews.SeedAsync(s, GoalReviews.Empty(s).WithNewGoal(new GoalRecord { Kind = GoalKind.ResearchProject, Target = Project }), cancellationToken).ConfigureAwait(false);

        var selected = false;
        _ = await s.ServeUntilAsync("research-goal", s.TicksLeft, async ct =>
        {
            var read = await ReadAsync(s, "observe", ct).ConfigureAwait(false);
            var project = read.Projects.FirstOrDefault(p => p.Project?.DefName == Project);
            selected |= project is { Current: true } || project is { Finished: true };
            var met = GoalReviews.Of(s, GoalId).Any(r => r.Verdict == "met");
            return (string.Create(CultureInfo.InvariantCulture, $"{project?.ProgressFraction} {project?.Current} {read.Finished.Contains(Project)} {met}"), project is { Finished: true } && met);
        }, cancellationToken).ConfigureAwait(false);

        var rows = GoalReviews.Of(s, GoalId);
        var verdicts = string.Join(' ', rows.Select(r => r.Verdict));
        s.Record("verdicts", verdicts);
        var pursuing = rows.ToList().FindIndex(r => r.Verdict == "pursuing");
        var met = rows.ToList().FindIndex(r => r.Verdict == "met");
        if (!selected)
        {
            throw new AcceptanceException($"the brain never selected {Project}: verdicts {verdicts}");
        }

        if (pursuing < 0 || met < pursuing)
        {
            throw new AcceptanceException($"the goal did not go Pursuing then Met: verdicts {verdicts}");
        }
    }
}
