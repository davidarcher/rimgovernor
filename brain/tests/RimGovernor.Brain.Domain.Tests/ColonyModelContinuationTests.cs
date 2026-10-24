using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ColonyModelContinuationTests
{
    private static readonly Resource Steel = new("Steel");
    private const double Hour = GameTime.TicksPerHour;

    private static ColonyModelInput Input() => ProjectGraphTests.Colony() with
    {
        Climate = Known(ColonyModelFixtures.Climate() with { CurrentTwelfth = 0, TicksToNextTwelfth = 5 * GameTime.TicksPerDay }),
        Pawns = Known(EquatableArray.From(new[] { ProjectGraphTests.Pawn("Anna", 1, 1) with
        {
            Needs = EquatableArray.From(new[] { new ModelNeed { Kind = "food", Fall = 0d, Gain = 0, Threshold = .3 } }),
            Skills = Known(EquatableArray.From<WorkSkill>([])),
        } })),
        Stocks = new ModelStocks { Nutrition = Known(100d), Materials = Known(EquatableArray.From(new[] { new Amount(Steel, 20) })), Medicine = Known(EquatableArray.From<Amount>([])) },
    };

    private static PawnCycle[][] Cycles() => [Enumerable.Range(0, 12).Select(t => new PawnCycle
    {
        Pawn = "Anna", Twelfth = t, IndoorHours = Known(t == 0 ? 1d : 2d), OutdoorHours = Known(0d),
    }).ToArray()];

    private static ColonyModelPolicy Policy() => new()
    {
        HorizonDays = 8, MedicineTendsPerDay = Known(0d),
        Projects = new ProjectGraph { Nodes = EquatableArray.From(new[]
        {
            new ProjectNode { Id = "a", Kind = ProjectKind.Construction, Work = 6 * Hour, Ingredients = EquatableArray.From(new[] { new Amount(Steel, 5) }) },
            new ProjectNode { Id = "b", Kind = ProjectKind.Construction, Work = 0, Prerequisites = EquatableArray.From(["a"]), Ingredients = EquatableArray.From(new[] { new Amount(Steel, 5) }) },
        }) },
        Scheduled = EquatableArray.From(new[]
        {
            new ScheduledEffect { Day = 0, Kind = ModelEventKind.Harvest, Id = "origin", Stock = ModelStock.Nutrition, Amount = 2 },
            new ScheduledEffect { Day = 5, Kind = ModelEventKind.Harvest, Id = "harvest", Stock = ModelStock.Nutrition, Amount = 7 },
            new ScheduledEffect { Day = 5, Kind = ModelEventKind.ProjectCompletion, Id = "complete", Stock = ModelStock.Nutrition, Amount = 3 },
        }),
    };

    [Theory]
    [InlineData(4.5)]
    [InlineData(5)]
    [InlineData(5.25)]
    [InlineData(5.5)]
    [InlineData(6)]
    public void RestartAcrossSeasonAndSimultaneousCompletionKeepsOriginDatesStocksAndWork(double cut)
    {
        var input = Input();
        var policy = Policy();
        var whole = ColonyModel.Run(input, Cycles(), policy);
        var done = cut >= 5.5;
        var remaining = cut <= 5 ? (6 - cut) * Hour : (1 - 2 * (cut - 5)) * Hour;
        var state = ColonyModelState.Observed(input, policy) with
        {
            Day = cut, Nutrition = Known(cut >= 5 ? 112d : 102d),
            Materials = Known(EquatableArray.From(new[] { new ModelResourceStock(Steel, done ? 10 : 20) })),
            Projects = Known(EquatableArray.From(new[]
            {
                new ModelProjectState("a", done ? 0 : remaining, done ? ModelProjectStatus.Done : ModelProjectStatus.Waiting, done ? ModelProjectInputs.Consumed : ModelProjectInputs.Held, false),
                new ModelProjectState("b", 0, done ? ModelProjectStatus.Done : ModelProjectStatus.Waiting, done ? ModelProjectInputs.Consumed : ModelProjectInputs.Unpaid, false),
            })),
        };
        var resumed = ColonyModel.Run(input, state, Cycles(), policy);
        Assert.Equal(whole.Events.Where(e => e.Day > cut), resumed.Events);
        Assert.Equal(8, resumed.Events[^1].Day);
        Assert.Equal(10, resumed.Events[^1].Levels[(int)ModelStock.Materials]);
        Assert.Equal(["a", "b"], whole.Events.Where(e => e.Day == 5.5).Select(e => e.Id));
        Assert.Equal(["", "complete", "harvest"], whole.Events.Where(e => e.Day == 5).Select(e => e.Id));
    }

    [Fact]
    public void ObservedOriginStillIncludesTimeZeroEffectsAndUsesSameStateAsMarginalCallers()
    {
        var input = Input();
        var policy = Policy();
        Assert.Equal(ColonyModel.Run(input, Cycles(), policy), ColonyModel.Run(input, ColonyModelState.Observed(input, policy), Cycles(), policy));
        Assert.Equal("origin", ColonyModel.Run(input, Cycles(), policy).Events[0].Id);
    }

    [Theory]
    [InlineData(ModelProjectInputs.Held, 10, 5)]
    [InlineData(ModelProjectInputs.Consumed, 5, 5)]
    public void StartedProjectDoesNotReserveAgainOrDebitAlreadyConsumedInputs(ModelProjectInputs payment, double physical, double expected)
    {
        var input = Input();
        var policy = Policy() with { Scheduled = default, Projects = new ProjectGraph { Nodes = EquatableArray.From(new[] { Policy().Projects.Nodes[0] with { Work = Hour } }) } };
        var state = ColonyModelState.Observed(input, policy) with
        {
            Day = 1,
            Materials = Known(EquatableArray.From(new[] { new ModelResourceStock(Steel, physical) })),
            ExternalHolds = Known(EquatableArray.From(new[] { new ModelResourceStock(Steel, 5) })),
            Projects = Known(EquatableArray.From(new[] { new ModelProjectState("a", Hour / 2, ModelProjectStatus.Waiting, payment, false) })),
        };
        var run = ColonyModel.Run(input, state, Cycles(), policy);
        Assert.Contains(run.Events, e => e.Id == "a" && e.Day == 1.5);
        Assert.Equal(expected, run.Events[^1].Levels[(int)ModelStock.Materials]);

        var unpaid = state with { Projects = Known(EquatableArray.From(new[] { new ModelProjectState("a", Hour, ModelProjectStatus.Waiting, ModelProjectInputs.Unpaid, false) })), ExternalHolds = Known(EquatableArray.From(new[] { new ModelResourceStock(Steel, physical) })) };
        Assert.DoesNotContain(ColonyModel.Run(input, unpaid, Cycles(), policy).Events, e => e.Id == "a");
    }

    [Fact]
    public void MultipleProgressesAndCompletedPrerequisitesRemainIndependent()
    {
        var input = Input();
        var policy = Policy() with { Scheduled = default };
        var state = ColonyModelState.Observed(input, policy) with
        {
            Day = 1,
            Projects = Known(EquatableArray.From(new[]
            {
                new ModelProjectState("a", 0, ModelProjectStatus.Done, ModelProjectInputs.Consumed, false),
                new ModelProjectState("b", Hour / 2, ModelProjectStatus.Waiting, ModelProjectInputs.Consumed, false),
            })),
        };
        var run = ColonyModel.Run(input, state, Cycles(), policy);
        Assert.DoesNotContain(run.Events, e => e.Id == "a");
        Assert.Contains(run.Events, e => e.Id == "b" && e.Day == 1.5);
        Assert.Equal(20, run.Events[^1].Levels[(int)ModelStock.Materials]);
    }

    [Theory]
    [InlineData(.1)]
    [InlineData(.2)]
    [InlineData(.3)]
    public void ExistingGrowthKeepsItsRemainingFractionAcrossTheCalendarBoundary(double cut)
    {
        var input = Input() with { Climate = Known(ProjectGraphTests.Colony().Climate.Value) };
        var crop = input.Crops.Value.Single(c => c.Name == ColonyModelFixtures.Crop);
        var policy = Policy() with
        {
            HorizonDays = 40, Scheduled = default,
            Projects = new ProjectGraph { Nodes = EquatableArray.From(new[]
            {
                new ProjectNode { Id = "grow", Kind = ProjectKind.Grow, Crop = crop.Name, Field = "zone1" },
                new ProjectNode { Id = "harvest", Kind = ProjectKind.Harvest, Crop = crop.Name, Field = "zone1", Cells = 1, Prerequisites = EquatableArray.From(["grow"]) },
            }) },
        };
        var first = CropGrowthModel.DailyGrowth(crop, 1, input.Climate.Value.Temperatures[4]).Value;
        var second = CropGrowthModel.DailyGrowth(crop, 1, input.Climate.Value.Temperatures[5]).Value;
        var remaining = crop.HarvestMinGrowth.Value - first * Math.Min(cut, .2) - second * Math.Max(0, cut - .2);
        var state = ColonyModelState.Observed(input, policy) with
        {
            Day = cut,
            Projects = Known(EquatableArray.From(new[]
            {
                new ModelProjectState("grow", remaining, ModelProjectStatus.Waiting, ModelProjectInputs.Unpaid, true),
                new ModelProjectState("harvest", 0, ModelProjectStatus.Waiting, ModelProjectInputs.Unpaid, false),
            })),
        };
        var whole = ColonyModel.Run(input, Cycles(), policy);
        var resumed = ColonyModel.Run(input, state, Cycles(), policy);
        Assert.Equal(whole.Events.Where(e => e.Day > cut).Select(e => (e.Kind, e.Id)), resumed.Events.Select(e => (e.Kind, e.Id)));
        foreach (var pair in whole.Events.Where(e => e.Day > cut).Zip(resumed.Events))
        {
            Assert.Equal(pair.First.Day, pair.Second.Day, 9);
            Assert.Equal(pair.First.Levels, pair.Second.Levels);
        }
    }

    [Fact]
    public void CarriedSkillXpPreservesFractionalDecayAndOriginDatedLevelChanges()
    {
        var mechanics = SkillStockTests.Mechanics(2, 0) with
        {
            DecayInterval = GameTime.TicksPerDay, LossThreshold = -100d,
            Levels = EquatableArray.From(new[] { new SkillLevelMechanics(0, 1000, 0), new SkillLevelMechanics(1, 1000, 100), new SkillLevelMechanics(2, 1000, 200) }),
        };
        var input = Input();
        input = input with { Pawns = Known(EquatableArray.From(new[] { SkillStockTests.WithMedicine(input.Pawns.Value[0], mechanics) })) };
        var policy = Policy() with { Projects = new ProjectGraph(), Scheduled = default };
        var whole = ColonyModel.Run(input, Cycles(), policy);
        var state = ColonyModelState.Observed(input, policy) with
        {
            Day = .25,
            Pawns = Known(EquatableArray.From(new[] { SkillStockTests.WithMedicine(input.Pawns.Value[0], mechanics with { Xp = -50d }) })),
        };
        Assert.Equal(whole.Skills, ColonyModel.Run(input, state, Cycles(), policy).Skills);
    }

    [Fact]
    public void ObservedOriginKeepsPartialTrajectoryWhenOptionalCensusIsUnavailable()
    {
        var input = Input() with { Stocks = new ModelStocks { Nutrition = Known(100d) } };
        var run = ColonyModel.Run(input, Cycles(), Policy() with { Projects = new ProjectGraph() });
        Assert.Equal(ModelEventKind.HorizonEnd, run.Events[^1].Kind);
        Assert.Contains("colony_model_materials_unknown", run.Findings);
        Assert.Contains("colony_model_medicine_unknown", run.Findings);
    }

    [Fact]
    public void InvalidOrUnavailableStartDoesNotProduceACompleteTrajectory()
    {
        var input = Input();
        var policy = Policy();
        var start = ColonyModelState.Observed(input, policy);
        foreach (var state in new[] { start with { Day = 9 }, start with { Day = double.NaN }, start with { Day = 1, Materials = default }, start with { Projects = default }, start with { ExternalHolds = Known(EquatableArray.From(new[] { new ModelResourceStock(Steel, 21) })) } })
        {
            var run = ColonyModel.Run(input, state, Cycles(), policy);
            Assert.Empty(run.Events);
            Assert.Contains(run.Findings, f => f.StartsWith("colony_model_", StringComparison.Ordinal) && f != ColonyTrajectory.MoodUnassessedFinding);
        }
        var truncated = ColonyModel.Run(input, start with { Day = 1 }, Cycles(), policy with { MaxEvents = 1 });
        Assert.True(truncated.Truncated);
        Assert.DoesNotContain(truncated.Events, e => e.Kind == ModelEventKind.HorizonEnd);
    }
}
