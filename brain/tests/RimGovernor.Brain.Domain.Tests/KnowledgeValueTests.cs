using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3843: an Anomaly knowledge point is priced through the project graph, per knowledge category, the way a research point is. The
// research read, the catalog's projects and overflow categories go through the census and the review summary into the model's input
// (RoundsRead.FrameResearch, RoundsColonyModel.InputOf), the standing graph and the perturbation. #3848: the point is worth what the
// project unlocks that the colony value prices (a garment), and each other unlock is named.
public sealed class KnowledgeValueTests
{
    internal const string Basic = "Basic";

    internal const string Advanced = "Advanced";

    internal const string Bioferrite = "BioferriteExtraction";

    // What the colony gains by the knowledge project finishing: the stand-in valuation pays this for its completion inside the run.
    private const double Worth = 6;

    private static readonly Obs.ResearchSnapshot FreshRead = new()
    {
        Context = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 1 },
        Slots = [new() { Category = "", CurrentProject = "" }],
        Projects = [],
        Finished = [],
    };

    /// <summary>A valuation worth <see cref="Worth"/> when the run completes <paramref name="node"/>, else nothing.</summary>
    private sealed class Completion(string node) : ITrajectoryValue
    {
        public ColonyValueResult Price(ColonyTrajectory trajectory) =>
            new(trajectory.Events.Any(e => e.Kind == ModelEventKind.ProjectCompletion && e.Id == node) ? Worth : 0, []);

        public double GoalValue(GoalTerminal goal, ModelEventTime completionDay) => 0;

        public bool Reads(SkillTrack track) => false;
    }

    // The production way: the census of the read over the catalog, its review summary on the frame, the model input, the standing graph.
    private static (ColonyModelInput Input, ProjectMarginals Marginals) Review(Obs.ResearchSnapshot read, IReadOnlyDictionary<string, ResearchProjectDefinition> catalog,
        IReadOnlyDictionary<string, string> overflow)
    {
        var summary = EconomyRules.ResearchSummary(EconomyRules.ResearchCensusOf(read, catalog, overflow));
        var research = ColonyModelFixtures.ReadyOf(ColonyModelFixtures.Reading() with { Facts = ColonyModelFixtures.Reading().Facts with { Research = summary } }).Research;
        var input = ProjectGraphTests.Colony() with { Research = research };
        var policy = new ColonyModelPolicy { MedicineTendsPerDay = 0.0, Projects = ProjectGraph.Standing(input, [], ColonyModelFixtures.NoThreat) };
        return (input, ProjectMarginalValue.Of(input, policy, new Completion(ProjectGraph.ResearchId(new ResearchProjectId(Bioferrite)))));
    }

    internal static Obs.ResearchSnapshot AnomalyRead(params string[] locks) => FreshRead with
    {
        Slots = [new() { Category = "", CurrentProject = "" }, new() { Category = Advanced, CurrentProject = "" }, new() { Category = Basic, CurrentProject = "" }],
        Projects = [new() { Project = new() { DefName = Bioferrite }, ProgressFraction = 0.25, Finished = false, Current = false, LockReasons = [.. locks] }],
    };

    private static readonly Dictionary<string, ResearchProjectDefinition> AnomalyCatalog = new(StringComparer.Ordinal)
    {
        [Bioferrite] = new() { KnowledgeCategory = Basic, Cost = 4 },
    };

    [Fact]
    public void AnUnfinishedKnowledgeProjectGivesItsCategoryAndTheOneOverflowingIntoItAPositiveValueAPoint()
    {
        // Basic's slot is empty, so the research Need funds the startable Bioferrite into it; Advanced funds nothing and overflows into Basic.
        var (input, marginals) = Review(AnomalyRead(), AnomalyCatalog, new Dictionary<string, string>(StringComparer.Ordinal) { [Basic] = "", [Advanced] = Basic });

        Assert.Equal([Advanced, Basic], input.Research.Knowledge.Select(k => k.Category));
        Assert.Equal(Bioferrite, input.Research.Knowledge.Single(k => k.Category == Basic).Project.Value);
        // The node is the 3 knowledge points still to give (4 less the quarter given), and no researcher works it: study alone gives them.
        var node = Assert.Single(ProjectGraph.Standing(input, [], ColonyModelFixtures.NoThreat).Nodes, n => n.Kind == ProjectKind.Knowledge);
        Assert.Equal(3, node.Work, 9);
        Assert.Equal(0, marginals.Value);
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

        Assert.True(input.Research.Knowledge.All(k => k.Project.IsEmpty));
        Assert.DoesNotContain(ProjectGraph.Standing(input, [], ColonyModelFixtures.NoThreat).Nodes, n => n.Kind == ProjectKind.Knowledge);
        Assert.Equal(0, marginals.PerUnit[ProjectGraph.KnowledgeKey(Basic)]);
        Assert.Equal(0, marginals.PerUnit[ProjectGraph.KnowledgeKey(Advanced)]);
    }

    // #3848: what the project unlocks is priced through the terms that value the colony. A stand-in for an Anomaly garment, a duster of
    // cloth only BioferriteExtraction keeps from the colony: its bill waits on the project, and the wearer has a 0.3 duster on.
    internal const string Unlocked = "Apparel_BioferriteDuster";

    internal const string Platform = "HoldingPlatform";

    private static readonly Resource Cloth = new(ClothDef);

    internal const string ClothDef = "Cloth";

    private static readonly GearOption UnlockedBill = ForecastValueTests.Duster(0.8) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(ForecastValueTests.Duster(0.8)).Origin).Value) with { Id = $"bill:{Unlocked}/{Cloth}", DefName = Unlocked, Stuff = (Cloth).Value, Source = GearSource.Bill }), Slot = GearSlot.Outer, Research = [Bioferrite] };

    private static ProjectMarginals Priced(IReadOnlyList<GearOption> options, params string[] unlocks) =>
        Priced(AnomalyRead(), new Dictionary<string, ResearchProjectDefinition>(StringComparer.Ordinal) { [Bioferrite] = AnomalyCatalog[Bioferrite] with { Unlocks = [.. unlocks] } },
            new Dictionary<string, string>(StringComparer.Ordinal) { [Basic] = "", [Advanced] = Basic }, options);

    /// <summary>The production way as <see cref="Review"/>, with the colony value itself: the stock covers <see cref="Unlocked"/> and a recipe makes it.</summary>
    internal static ProjectMarginals Priced(Obs.ResearchSnapshot read, IReadOnlyDictionary<string, ResearchProjectDefinition> catalog, IReadOnlyDictionary<string, string> overflow, IReadOnlyList<GearOption> options)
    {
        var summary = EconomyRules.ResearchSummary(EconomyRules.ResearchCensusOf(read, catalog, overflow));
        var research = ColonyModelFixtures.ReadyOf(ColonyModelFixtures.Reading() with { Facts = ColonyModelFixtures.Reading().Facts with { Research = summary } }).Research;
        var recipe = new ProductionRecipeInput($"Make_{Unlocked}", EquatableArray.From(new[] { EquatableArray.From(new[] { new Amount(Cloth, 80) }) }), false)
        {
            Products = EquatableArray.From(new[] { new Amount(new Resource(Unlocked), 1) }),
            WorkAmount = 10_000.0,
        };
        var colony = ProjectGraphTests.Colony();
        var input = colony with
        {
            Research = research,
            Stocks = colony.Stocks with { Materials = EquatableArray.From(new[] { new Amount(Cloth, 80) }) },
            Recipes = EquatableArray.From(new[] { recipe }),
        };
        var worn = EquatableArray.From(new[] { ForecastValueTests.Duster(0.3) with { Slot = GearSlot.Outer } });
        var pawn = ForecastValueTests.HarmPawn() with { Pawn = "Anna" };
        var context = ForecastValueTests.Context(pawn, ForecastValueTests.HarmTables(), ForecastValueTests.RaidDay(), new Dictionary<string, EquatableArray<GearOption>>(StringComparer.Ordinal) { ["Anna"] = worn })
            with { Needs = NeedsFixtures.Empty with { Pawns = [pawn] } };
        var policy = new ColonyModelPolicy { MedicineTendsPerDay = 0.0, Projects = ProjectGraph.Standing(input, options, ColonyModelFixtures.NoThreat) };
        return ProjectMarginalValue.Of(input, policy, new ColonyValue(context, options, CalendarRules.YearDays, default, new ItemFacts()));
    }

    [Fact]
    public void AnAnomalyProjectWhoseGarmentTheColonyValuePricesGivesAKnowledgePointAPositivePrice()
    {
        var marginals = Priced([UnlockedBill], Unlocked);

        // The duster waits on the project, so the run as it is never makes it; with the points given now it is made and worn.
        Assert.DoesNotContain(marginals.Trajectory.Apparel, a => a.Products.Any(p => p.Resource.Value == Unlocked));
        Assert.True(marginals.PerUnit[ProjectGraph.KnowledgeKey(Basic)] > 0, string.Join(',', marginals.Findings));
        Assert.Equal(marginals.PerUnit[ProjectGraph.KnowledgeKey(Basic)], marginals.PerUnit[ProjectGraph.KnowledgeKey(Advanced)], 9);
        Assert.DoesNotContain(marginals.Findings, f => f.StartsWith(ProjectGraph.KnowledgeUnlockUnmodelled, StringComparison.Ordinal));
    }

    [Fact]
    public void AnAnomalyProjectWithOnlyUnpricedUnlocksIsWorthNothingAPointAndNamesThem()
    {
        // A holding platform has no term in the colony value; the duster is not one of the colony's options here.
        var marginals = Priced([], Platform, Unlocked);

        Assert.Equal(0, marginals.PerUnit[ProjectGraph.KnowledgeKey(Basic)]);
        Assert.Contains($"{ProjectGraph.KnowledgeUnlockUnmodelled}:{Bioferrite}:{Platform}", marginals.Findings);
        Assert.Contains($"{ProjectGraph.KnowledgeUnlockUnmodelled}:{Bioferrite}:{Unlocked}", marginals.Findings);
    }

    [Fact]
    public void AFreshColonyWithoutAnomalyHasNoKnowledgeCategoryAndThrowsNothing()
    {
        // Without Anomaly the read has the ordinary slot alone and the catalog no knowledge category.
        var (input, marginals) = Review(FreshRead, new Dictionary<string, ResearchProjectDefinition>(StringComparer.Ordinal) { ["Devilstrand"] = new() { Cost = 800 } },
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Empty(input.Research.Knowledge);
        Assert.DoesNotContain(marginals.PerUnit.Keys, k => k.StartsWith(ProjectGraph.KnowledgeKey(""), StringComparison.Ordinal));
    }
}
