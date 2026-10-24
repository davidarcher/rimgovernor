using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3843: an Anomaly knowledge point is priced through the project graph, per knowledge category, the way a research point is. The
// research read, the catalog's projects and overflow categories go through the census and the review summary into the model's input
// (RoundsRead.FrameResearch, RoundsColonyModel.InputOf), the standing graph and the perturbation.
public sealed class KnowledgeValueTests
{
    private const string Basic = "Basic";

    private const string Advanced = "Advanced";

    private const string Bioferrite = "BioferriteExtraction";

    // What the colony gains by the knowledge project finishing: the stand-in valuation pays this for its completion inside the run.
    private const double Worth = 6;

    private static readonly Obs.ResearchSnapshot FreshRead = new()
    {
        Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 1 },
        Slots = [new() { Category = "", CurrentProject = "" }],
        Projects = [],
        Finished = [],
    };

    /// <summary>A valuation worth <see cref="Worth"/> when the run completes <paramref name="node"/>, else nothing.</summary>
    private sealed class Completion(string node) : ITrajectoryValue
    {
        public ColonyValueResult Price(ColonyTrajectory trajectory) =>
            new(Known(trajectory.Events.Any(e => e.Kind == ModelEventKind.ProjectCompletion && e.Id == node) ? Worth : 0), []);

        public double GoalValue(GoalTerminal goal, double? completionDay) => 0;

        public bool Reads(SkillTrack track) => false;
    }

    // The production way: the census of the read over the catalog, its review summary on the frame, the model input, the standing graph.
    private static (ColonyModelInput Input, ProjectMarginals Marginals) Review(Obs.ResearchSnapshot read, IReadOnlyDictionary<string, ResearchProjectFacts> catalog,
        IReadOnlyDictionary<string, string> overflow)
    {
        var summary = EconomyRules.ResearchSummary(EconomyRules.ResearchCensusOf(read, catalog, overflow));
        var research = RoundsColonyModel.InputOf(ColonyModelFixtures.Projection() with { Facts = ColonyModelFixtures.Projection().Facts with { Research = Known(summary) } }).Input.Research;
        var input = ProjectGraphTests.Colony() with { Research = research };
        var policy = new ColonyModelPolicy { MedicineTendsPerDay = Known(0.0), Projects = ProjectGraph.Standing(input, [], default) };
        return (input, ProjectMarginalValue.Of(input, policy, new Completion(ProjectGraph.ResearchId(new ResearchProjectId(Bioferrite)))));
    }

    private static Obs.ResearchSnapshot AnomalyRead(params string[] locks) => FreshRead with
    {
        Slots = [new() { Category = "", CurrentProject = "" }, new() { Category = Advanced, CurrentProject = "" }, new() { Category = Basic, CurrentProject = "" }],
        Projects = [new() { Project = new() { DefName = Bioferrite }, ProgressFraction = 0.25, Finished = false, Current = false, LockReasons = [.. locks] }],
    };

    private static readonly Dictionary<string, ResearchProjectFacts> AnomalyCatalog = new(StringComparer.Ordinal)
    {
        [Bioferrite] = new() { KnowledgeCategory = Basic, Cost = 4 },
    };

    [Fact]
    public void AnUnfinishedKnowledgeProjectGivesItsCategoryAndTheOneOverflowingIntoItAPositiveValueAPoint()
    {
        // Basic's slot is empty, so the research Need funds the startable Bioferrite into it; Advanced funds nothing and overflows into Basic.
        var (input, marginals) = Review(AnomalyRead(), AnomalyCatalog, new Dictionary<string, string>(StringComparer.Ordinal) { [Basic] = "", [Advanced] = Basic });

        Assert.Equal([Advanced, Basic], input.Research.Value.Knowledge.Select(k => k.Category));
        Assert.Equal(Bioferrite, input.Research.Value.Knowledge.Single(k => k.Category == Basic).Project.Value);
        // The node is the 3 knowledge points still to give (4 less the quarter given), and no researcher works it: study alone gives them.
        var node = Assert.Single(ProjectGraph.Standing(input, [], default).Nodes, n => n.Kind == ProjectKind.Knowledge);
        Assert.Equal(3, node.Work, 9);
        Assert.Equal(0, marginals.Value.Value);
        Assert.DoesNotContain(marginals.Trajectory.Events, e => e.Id == node.Id);
        // Finishing it now is worth Worth, so each of its 3 points is worth a third of that, in both categories.
        Assert.Equal(Worth / 3, marginals.PerUnit[ProjectGraph.KnowledgeKey(Basic)], 9);
        Assert.Equal(Worth / 3, marginals.PerUnit[ProjectGraph.KnowledgeKey(Advanced)], 9);
        Assert.DoesNotContain(Bioferrite, marginals.PerUnit.Keys);
    }

    [Fact]
    public void ACategoryWithNoReachableProjectIsWorthNothingAPoint()
    {
        // The one Basic project waits on a prerequisite, so the empty slot funds nothing; Basic has no overflow, so a point is lost.
        var (input, marginals) = Review(AnomalyRead("prerequisite:Other"), AnomalyCatalog, new Dictionary<string, string>(StringComparer.Ordinal) { [Basic] = "", [Advanced] = Basic });

        Assert.True(input.Research.Value.Knowledge.All(k => k.Project.IsEmpty));
        Assert.DoesNotContain(ProjectGraph.Standing(input, [], default).Nodes, n => n.Kind == ProjectKind.Knowledge);
        Assert.Equal(0, marginals.PerUnit[ProjectGraph.KnowledgeKey(Basic)]);
        Assert.Equal(0, marginals.PerUnit[ProjectGraph.KnowledgeKey(Advanced)]);
    }

    [Fact]
    public void AFreshColonyWithoutAnomalyHasNoKnowledgeCategoryAndThrowsNothing()
    {
        // Without Anomaly the read has the ordinary slot alone and the catalog no knowledge category.
        var (input, marginals) = Review(FreshRead, new Dictionary<string, ResearchProjectFacts>(StringComparer.Ordinal) { ["Devilstrand"] = new() { Cost = 800 } },
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Empty(input.Research.Value.Knowledge);
        Assert.DoesNotContain(marginals.PerUnit.Keys, k => k.StartsWith(ProjectGraph.KnowledgeKey(""), StringComparison.Ordinal));
    }
}
