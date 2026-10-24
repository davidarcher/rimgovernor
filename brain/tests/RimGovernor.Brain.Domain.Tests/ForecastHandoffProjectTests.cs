using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastHandoffProjectTests
{
    [Theory]
    [InlineData(22.5, false)]
    [InlineData(.1, true)]
    public void GeneratedPartialSowingRetainsEachCellsGrowthAndHarvestsBeforeTheUnsownRemainder(double growDays, bool mature)
    {
        var input = ProjectGraphTests.Colony();
        input = input with { Crops = Known(EquatableArray.From(input.Crops.Value.Select(c => c with { GrowDays = Known(growDays) }))) };
        var graph = ProjectGraph.CropChain(input, ColonyModelFixtures.Crop, "zone1", []);
        var learning = ProductiveLearningTests.Work() with { Recipe = new("Plants", Known(.085), true), WorkAmount = Known(2d),
            Rates = [new(0, Known(.005)), new(1, Known(.005)), new(2, Known(.005))], LearnBeforeWork = true };
        var native = CoupledFixtures.NoSowing with { Workers = [new("Bo", "zone1", "Grower", 0d, 200d, [new(1, 1), new(1, 2)]) { SkillWork = learning }] };
        var generated = SowingMethodCandidates.Generate(native, 0, GameTime.TicksPerDay, new([], [], [], []));
        var task = Assert.Single(generated.Tasks);
        var duration = ForecastProductiveWorkPolicy.ForecastProductiveWork(Assert.Single(generated.Options), [], 0);
        Assert.True(duration.WorkTicks.IsKnown, duration.Reason);
        Assert.NotNull(duration.Evidence);
        var end = duration.WorkTicks.Value;
        var plan = new ForecastActivityPlan([new(task.Id, "Bo", 0, 0, 0, end, 0, 0, end, 0, 0) { Learning = duration.Evidence }], end, 0);
        var context = Context(generated.Tasks) with { Bindings = generated.Bindings };
        var result = ForecastHandoffProjects.Project(new(input, new() { Projects = graph }), context,
            new(plan, Known(true), new Dictionary<string, ForecastNeedsResult>(), []), new Dictionary<string, double> { [task.Id] = end }, 1, []);
        var sow = result.States[ProjectGraph.SowId("zone1")];
        Assert.True(sow.Remaining > 0);
        var grown = result.Graph.Nodes.Where(n => n.Kind == ProjectKind.Grow && n.Prerequisites.Count == 0).ToArray();
        Assert.Equal(2, grown.Length);
        if (mature)
        {
            var crop = input.Crops.Value.Single(c => c.Name == ColonyModelFixtures.Crop);
            var rate = CropGrowthModel.DailyGrowth(crop, 1, input.Climate.Value.Temperatures[input.Climate.Value.CurrentTwelfth]).Value;
            Assert.Equal(200d / GameTime.TicksPerDay + crop.HarvestMinGrowth.Value / rate,
                Assert.Single(result.Completed, d => d.Project == grown[0].Id).Day, 8);
        }
        else Assert.True(result.States[grown[0].Id].Remaining < result.States[grown[1].Id].Remaining);
        Assert.All(grown, g => Assert.True(result.States[g.Id].GrowthInitialized));
        var state = ColonyModelState.Observed(input, new() { Projects = result.Graph }) with { Day = 1, Projects = Known(EquatableArray.From(result.States.Values)) };
        var cycles = input.Pawns.Value.Select(p => PawnCycle.Year(input, p).ToArray()).ToArray();
        var continuation = ColonyModel.Run(input, state, cycles, new() { Projects = result.Graph, MedicineTendsPerDay = Known(0d) });
        Assert.False(continuation.Truncated);
        var first = Assert.Single(continuation.Events, e => e.Id == "harvest:zone1/cohort/0");
        var residual = Assert.Single(continuation.Events, e => e.Id == "harvest:zone1/cohort/2");
        Assert.True(first.Day < residual.Day);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedCraftGraphDoesNotChargeInputsOrGrantOutputAgainAfterDetailedCompletion(bool unfinished)
    {
        var input = ProjectGraphTests.Colony();
        var graph = ProjectGraph.Craft(input, ColonyModelFixtures.Recipe, [], false);
        var node = Assert.Single(graph.Nodes);
        var task = new ForecastActivity("craft", "bench", "bench", [])
        { Inputs = unfinished ? [] : EquatableArray.From(node.Ingredients.Select(a => new ForecastActivityYield(a.Resource, Known((double)a.Count)))),
            Outputs = EquatableArray.From(node.Products.Select(a => new ForecastActivityYield(a.Resource, Known((double)a.Count)))) };
        var binding = new RimGovernor.Brain.Policy.Methods.ActivityTaskCandidate("craft", "Anna", [], PawnActivityKind.Recipe, "bench", "", "DoBill", 1, 60000, new(1, 1))
        { Recipe = new("bill", ColonyModelFixtures.Recipe, []) { Unfinished = unfinished ? "native-unfinished-item" : "" } };
        var context = Context([task]) with { Bindings = new Dictionary<(string, string), RimGovernor.Brain.Policy.Methods.ActivityTaskCandidate> { [("craft", "Anna")] = binding } };
        var outcome = new ForecastCoupledOutcome(new([new("craft", "Anna", 0, 0, 0, 100, 0, 0, 100, 0, 0)], 100, 0), Known(true), new Dictionary<string, ForecastNeedsResult>(), []);
        var projected = ForecastHandoffProjects.Project(new(input, new() { Projects = graph }), context, outcome,
            new Dictionary<string, double> { ["craft"] = 100 }, 1, []);
        Assert.Equal(ModelProjectStatus.Done, projected.States[node.Id].Status);
        Assert.Equal(ModelProjectInputs.Consumed, projected.States[node.Id].Inputs);
        Assert.Empty(Assert.Single(projected.Graph.Nodes).Ingredients);
        Assert.Empty(Assert.Single(projected.Graph.Nodes).Products);
        Assert.Equal(100d / GameTime.TicksPerDay, Assert.Single(projected.Completed).Day);
    }

    private static ForecastCoupledContext Context(ForecastActivity[] tasks) => new()
    { Activities = new() { Activities = tasks }, Needs = NeedsFixtures.Empty, Resources = new(new(), new([], [], [], []), default) };

    [Fact]
    public void HarvestUsesNativeFieldCellIdentityAndRemovesItsAlreadyPaidCropCycle()
    {
        var input = ProjectGraphTests.Colony();
        input = input with { Land = Known(EquatableArray.From(input.Land.Value.Select(f => f with
        { Cells = EquatableArray.From(Enumerable.Range(0, 12).Select(x => new Cell(x, 1))) }))) };
        var graph = ProjectGraph.CropChain(input, ColonyModelFixtures.Crop, "zone1", []);
        var task = new ForecastActivity("harvest", "field", "field", []);
        var binding = new RimGovernor.Brain.Policy.Methods.ActivityTaskCandidate("harvest", "Bo", [], PawnActivityKind.Work, "plant-7", "Grower", "Harvest", 0, 60000, new(7, 1));
        var context = Context([task]) with { Bindings = new Dictionary<(string, string), RimGovernor.Brain.Policy.Methods.ActivityTaskCandidate> { [("harvest", "Bo")] = binding } };
        var result = ForecastHandoffProjects.Project(new(input, new() { Projects = graph }), context,
            new(new([new("harvest", "Bo", 0, 0, 0, 100, 0, 0, 100, 0, 0)], 100, 0), Known(true), new Dictionary<string, ForecastNeedsResult>(), []),
            new Dictionary<string, double> { ["harvest"] = 100 }, 1, []);
        Assert.All(result.Graph.Nodes.Where(n => n.Field == "zone1"), n => Assert.Equal(11, n.Cells));
        var original = Assert.Single(graph.Nodes, n => n.Kind == ProjectKind.Sow);
        Assert.Equal(original.Work * 11 / 12, result.States[original.Id].Remaining, 8);
    }
}
