using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3526: the pawn cycle per twelfth of the tier-2 colony model.
public sealed class PawnCycleTests
{
    private static PawnCycle.Scheduled Scheduled(ColonyModelInput input, ModelPawn pawn, int twelfth) =>
        Assert.IsType<PawnCycle.Scheduled>(PawnCycle.Of(input, pawn, twelfth));
    private static readonly double[] Daylight = [6, 7.5, 10, 12.5, 14, 15, 16, 15, 12.5, 10, 7.5, 6];

    // 8 h asleep (gain twice the fall), 2.5 meals of 0.6 nutrition a day at 400 + 100 ticks each (0.5 h), 1.5 h of recreation (gain 15x the fall).
    private static ModelPawn Pawn(string name = "Anna") => new()
    {
        Pawn = name,
        Needs = EquatableArray.From(new[]
        {
            new ModelNeed { Kind = "rest", Fall = 0.00001, Gain = 0.00002, Threshold = 0.28, Service = new ModelService.NoneObserved() },
            new ModelNeed { Kind = "food", Fall = 0.000025, Threshold = 0.3, Service = new ModelService.Observed(0.6, 400.0, 100.0), Gain = 0 },
            new ModelNeed { Kind = "joy", Fall = 0.00001, Gain = 0.00015, Threshold = 0.3, Service = new ModelService.NoneObserved() },
        }),
        Skills = EquatableArray.From(new[] { new RimGovernor.Brain.GameFacts.Observations.Skill { Passion = RimGovernor.Brain.GameFacts.Observations.Passion.None, DefName = "Plants", Level = 10 } }),
        ComfortableMin = 16.0,
        ComfortableMax = 26.0,
        Consciousness = 1.0,
        Moving = 1.0,
        Manipulation = 1.0,
        Sight = 1.0,
        Talking = 1.0,
        ResearchSpeed = 1.0,
        PlantWorkSpeed = 1.0,
        Hunts = false,
    };

    private static ColonyModelInput Input(ModelPawn pawn)
    {
        var input = ColonyModelFixtures.Devilstrand();
        var climate = input.Climate with { DaylightHours = EquatableArray.From(Daylight) };
        return input with { Climate = climate, Pawns = EquatableArray.From(new[] { pawn }) };
    }

    [Fact]
    public void EightHoursAsleepHalfAnHourEatingAndAnHourAndAHalfOfRecreationLeaveFourteenFreeHours()
    {
        var pawn = Pawn();
        var cycle = Scheduled(Input(pawn), pawn, 5);

        Assert.Equal(8, cycle.SleepHours, 9);
        Assert.Equal(0.5, cycle.EatingHours, 9);
        Assert.Equal(1.5, cycle.RecreationHours, 9);
        Assert.Equal(14, cycle.FreeHours, 9);
        Assert.Empty(cycle.Findings);
        Assert.True(cycle.Valid());
    }

    [Fact]
    public void AWinterTwelfthWithSixHoursOfDaylightHasSixOutdoorCapableHours()
    {
        var pawn = Pawn();
        var winter = Scheduled(Input(pawn), pawn, 0);
        var summer = Scheduled(Input(pawn), pawn, 6);

        Assert.Equal(6, winter.DaylightHours);
        Assert.Equal(6, winter.OutdoorHours, 9);
        Assert.Equal(8, winter.IndoorHours, 9);
        Assert.Equal(14, summer.OutdoorHours, 9);
        Assert.Equal(0, summer.IndoorHours, 9);
    }

    [Fact]
    public void AJoyNeedNothingServesNowTakesNoHoursAndIsNamed()
    {
        // #3825: no recreation building and no walk the giver allows (JoyGiver.CanBeGivenTo): the read's joy gain is 0, a real state.
        var pawn = Pawn();
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "joy" ? n with { Gain = 0d } : n)) };

        var cycle = Scheduled(Input(pawn), pawn, 5);

        Assert.Equal(0, cycle.RecreationHours);
        Assert.Equal(15.5, cycle.FreeHours, 9);
        Assert.True(cycle.Valid());
        Assert.Contains("joy_unserved:Anna", cycle.Findings);
    }

    [Fact]
    public void AWalkServesOnlyTheTwelfthsShareOfEnjoyableWeather()
    {
        // #3825 D7: the walk is the only recreation; JoyUtility.EnjoyableOutsideNow holds 0.6 of twelfth 5 and none of twelfth 0.
        var shares = new double[12];
        shares[5] = 0.6;
        var pawn = Pawn();
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "joy" ? n with { OutdoorShares = EquatableArray.From(shares) } : n)) };

        var summer = Scheduled(Input(pawn), pawn, 5);
        var winter = Scheduled(Input(pawn), pawn, 0);

        Assert.Equal(0.6 * 1.5, summer.RecreationHours, 9);
        Assert.Equal(15.5 - (0.6 * 1.5), summer.FreeHours, 9);
        Assert.DoesNotContain("joy_unserved:Anna", summer.Findings);
        Assert.Equal(0, winter.RecreationHours);
        Assert.Contains("joy_unserved:Anna", winter.Findings);
        Assert.True(winter.Valid());
    }

    [Fact]
    public void InBadWeatherAPawnTakesItsBuildingsSlowerRecreation()
    {
        // The walk (gain 15x the fall) serves half of twelfth 5; the other half a building at 5x the fall: 1.5 h and 4 h days mixed.
        var shares = Enumerable.Repeat(0.5, 12).ToArray();
        var pawn = Pawn();
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "joy" ? n with { OutdoorShares = EquatableArray.From(shares), ShelteredGain = 0.00005 } : n)) };

        var cycle = Scheduled(Input(pawn), pawn, 5);

        Assert.Equal((0.5 * 1.5) + (0.5 * 4), cycle.RecreationHours, 9);
    }

    [Fact]
    public void ABuildingAtLeastAsGoodAsTheWalkServesInAnyWeather()
    {
        var pawn = Pawn();
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "joy" ? n with { OutdoorShares = EquatableArray.From(new double[12]), ShelteredGain = 0.00015 } : n)) };

        Assert.Equal(1.5, Scheduled(Input(pawn), pawn, 5).RecreationHours, 9);
    }

    [Fact]
    public void FreeHoursAreTheHorizonsMeanOverTheTwelfthsItReaches()
    {
        // The walk serves all of one twelfth and none of the others: a horizon of the current twelfth's rest and a whole next one
        // weighs the two days by their days.
        var pawn = Pawn();
        var input = Input(pawn);
        var climate = input.Climate;
        var shares = new double[12];
        shares[climate.CurrentTwelfth] = 1;
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "joy" ? n with { OutdoorShares = EquatableArray.From(shares) } : n)) };
        input = input with { Pawns = EquatableArray.From(new[] { pawn }) };
        var first = climate.TicksToNextTwelfth / (double)GameTime.TicksPerDay;
        var horizon = first + (CalendarRules.YearDays / 12.0);

        var free = PawnCycle.FreeHoursByPawn(input, horizon)["Anna"];

        Assert.Equal(((first * 14) + (CalendarRules.YearDays / 12.0 * 15.5)) / horizon, free, 9);
    }

    [Fact]
    public void MissingNeedFailsTheCycle()
    {
        var pawn = Pawn() with { Needs = default, Skills = default };

        Assert.Equal("rest need:Anna", Assert.Throws<RequiredFactException>(() => PawnCycle.Of(Input(pawn), pawn, 5)).Fact);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("food")]
    [InlineData("joy")]
    public void UnreadRateFailsTheCycle(string kind)
    {
        var pawn = Pawn();
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == kind ? n with { Fall = double.NaN } : n)) };
        Assert.Throws<RequiredFactException>(() => PawnCycle.Of(Input(pawn), pawn, 5));
    }

    [Fact]
    public void ADayOverbookedByItsOverheadsHasNoFreeHours()
    {
        var pawn = Pawn();
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "rest" ? n with { Fall = 0.001 } : n)) };

        var cycle = Assert.IsType<PawnCycle.Overbooked>(PawnCycle.Of(Input(pawn), pawn, 5));
        Assert.Contains("overheads_exceed_day:Anna", cycle.Findings);
        Assert.Empty(PawnCycle.FreeHoursByPawn(Input(pawn), 30));
        AssertNoLabor(pawn);
    }

    [Fact]
    public void OutdoorHoursCarryTheTwelfthsTemperatureAgainstTheComfortRange()
    {
        var pawn = Pawn();
        var input = Input(pawn);

        var span = Scheduled(input, pawn, 0).OutdoorThermal;

        Assert.Equal(new ForecastThermalSpan(0, 6 * 2500, -10, -10, 16, 26), span);
    }

    [Fact]
    public void PlantsEightIsGatedOutOfDevilstrandSowingAndPlantsTenIsIn()
    {
        var crop = ColonyModelFixtures.Devilstrand().Crops.Single(c => c.Name == ColonyModelFixtures.Crop);
        Assert.Equal(10, crop.SowMinSkill);

        (bool Available, string Finding) Gate(int level, bool disabled = false) =>
            PawnCycle.SowGate(Pawn() with { Skills = EquatableArray.From(new[] { new RimGovernor.Brain.GameFacts.Observations.Skill { Passion = RimGovernor.Brain.GameFacts.Observations.Passion.None, DefName = "Plants", Level = level, Disabled = disabled } }) }, crop);

        Assert.Equal((false, ""), Gate(8));
        Assert.Equal((true, ""), Gate(10));
        Assert.Equal((false, "plants_disabled:Anna"), Gate(12, disabled: true));
    }

    [Fact]
    public void MissingSowingFactsFail()
    {
        var crop = ColonyModelFixtures.Devilstrand().Crops.Single(c => c.Name == ColonyModelFixtures.Crop);
        var pawn = Pawn() with { Skills = default };

        Assert.Throws<RequiredFactException>(() => PawnCycle.SowGate(pawn, crop));
        Assert.Equal((true, ""), PawnCycle.SowGate(Pawn(), crop with { SowMinSkill = 0 }));
    }

    [Fact]
    public void MissingMealServiceHasNoLabor()
    {
        var pawn = Pawn();
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "food" ? n with { Service = new ModelService.NoneObserved() } : n)) };
        Assert.IsType<PawnCycle.NoMealService>(PawnCycle.Of(Input(pawn), pawn, 5));
        Assert.Empty(PawnCycle.FreeHoursByPawn(Input(pawn), 30));
        AssertNoLabor(pawn);
    }

    private static void AssertNoLabor(ModelPawn pawn)
    {
        var input = Input(pawn);
        var policy = new ColonyModelPolicy { MedicineTendsPerDay = 0, HorizonDays = 1,
            Projects = new ProjectGraph { Nodes = [new ProjectNode { Id = "research", Kind = ProjectKind.Research, Work = 1 }] } };
        var run = ColonyModel.Run(input, [PawnCycle.Year(input, pawn).ToArray()], policy);
        Assert.DoesNotContain(run.Events, e => e.Kind == ModelEventKind.ProjectCompletion);
        Assert.Contains("project_unstaffed:research", run.Findings);
    }

    [Fact]
    public void AbsentSeasonalReadFailsTheCycle()
    {
        var pawn = Pawn();
        var input = Input(pawn);
        Assert.Throws<RequiredFactException>(() => PawnCycle.Of(input with { Climate = input.Climate with { Temperatures = [] } }, pawn, 5));
    }

    [Fact]
    public void ARealReviewFrameYieldsTwelveCyclesPerPawn()
    {
        var input = ColonyModelFixtures.Devilstrand();
        var pawn = input.Pawns.Single();

        var year = PawnCycle.Year(input, pawn);

        Assert.Equal(Enumerable.Range(0, 12), year.Select(c => c.Twelfth));
        Assert.All(year, c => Assert.True(c.Valid(), string.Join(",", c.Findings)));
        Assert.True(Assert.IsType<PawnCycle.Scheduled>(year[0]).DaylightHours < Assert.IsType<PawnCycle.Scheduled>(year[6]).DaylightHours);
        Assert.Empty(input.Gaps());
    }

    [Fact]
    public void DaylightHoursMustBeTwelveValuesWithinADay()
    {
        var climate = ColonyModelFixtures.Climate();

        climate.Validate();
        (climate with { DaylightHours = EquatableArray.From(Daylight) }).Validate();
        Assert.Throws<DomainException>(() => (climate with { DaylightHours = EquatableArray.From(Daylight.Take(11)) }).Validate());
        Assert.Throws<DomainException>(() => (climate with { DaylightHours = EquatableArray.From(Daylight.Select((h, i) => i == 3 ? 25 : h)) }).Validate());
    }
}
