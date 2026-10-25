using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3526: the pawn cycle per twelfth of the tier-2 colony model.
public sealed class PawnCycleTests
{
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
        Skills = EquatableArray.From(new[] { new WorkSkill { Name = "Plants", Level = 10 } }),
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
        var cycle = PawnCycle.Of(Input(pawn), pawn, 5);

        Assert.Equal(8, cycle.SleepHours.Value, 9);
        Assert.Equal(0.5, cycle.EatingHours.Value, 9);
        Assert.Equal(1.5, cycle.RecreationHours.Value, 9);
        Assert.Equal(14, cycle.FreeHours.Value, 9);
        Assert.Empty(cycle.Findings);
        Assert.True(cycle.Valid());
    }

    [Fact]
    public void AWinterTwelfthWithSixHoursOfDaylightHasSixOutdoorCapableHours()
    {
        var pawn = Pawn();
        var winter = PawnCycle.Of(Input(pawn), pawn, 0);
        var summer = PawnCycle.Of(Input(pawn), pawn, 6);

        Assert.Equal(6, winter.DaylightHours.Value);
        Assert.Equal(6, winter.OutdoorHours.Value, 9);
        Assert.Equal(8, winter.IndoorHours.Value, 9);
        Assert.Equal(14, summer.OutdoorHours.Value, 9);
        Assert.Equal(0, summer.IndoorHours.Value, 9);
    }

    [Fact]
    public void AJoyNeedNothingServesNowTakesNoHoursAndIsNamed()
    {
        // #3825: no recreation building and no walk the giver allows (JoyGiver.CanBeGivenTo): the read's joy gain is 0, a real state.
        var pawn = Pawn();
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "joy" ? n with { Gain = 0d } : n)) };

        var cycle = PawnCycle.Of(Input(pawn), pawn, 5);

        Assert.Equal(0, cycle.RecreationHours.Value);
        Assert.Equal(15.5, cycle.FreeHours.Value, 9);
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

        var summer = PawnCycle.Of(Input(pawn), pawn, 5);
        var winter = PawnCycle.Of(Input(pawn), pawn, 0);

        Assert.Equal(0.6 * 1.5, summer.RecreationHours.Value, 9);
        Assert.Equal(15.5 - (0.6 * 1.5), summer.FreeHours.Value, 9);
        Assert.DoesNotContain("joy_unserved:Anna", summer.Findings);
        Assert.Equal(0, winter.RecreationHours.Value);
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

        var cycle = PawnCycle.Of(Input(pawn), pawn, 5);

        Assert.Equal((0.5 * 1.5) + (0.5 * 4), cycle.RecreationHours.Value, 9);
    }

    [Fact]
    public void ABuildingAtLeastAsGoodAsTheWalkServesInAnyWeather()
    {
        var pawn = Pawn();
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "joy" ? n with { OutdoorShares = EquatableArray.From(new double[12]), ShelteredGain = 0.00015 } : n)) };

        Assert.Equal(1.5, PawnCycle.Of(Input(pawn), pawn, 5).RecreationHours.Value, 9);
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
    public void EveryUnreadRateIsNamed()
    {
        var pawn = Pawn() with { Needs = default, Skills = default };

        var cycle = PawnCycle.Of(Input(pawn), pawn, 5);

        Assert.Equal(
            ["food_fall_unknown:Anna", "joy_fall_unknown:Anna", "rest_fall_unknown:Anna"],
            cycle.Findings);
        Assert.False(cycle.OutdoorThermal.IsKnown);
    }

    [Fact]
    public void ADayOverbookedByItsOverheadsHasNoFreeHours()
    {
        var pawn = Pawn();
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "rest" ? n with { Fall = 0.001 } : n)) };

        var cycle = PawnCycle.Of(Input(pawn), pawn, 5);

        Assert.False(cycle.FreeHours.IsKnown);
        Assert.Contains("overheads_exceed_day:Anna", cycle.Findings);
    }

    [Fact]
    public void OutdoorHoursCarryTheTwelfthsTemperatureAgainstTheComfortRange()
    {
        var pawn = Pawn();
        var input = Input(pawn);

        var span = PawnCycle.Of(input, pawn, 0).OutdoorThermal.Value;

        Assert.Equal(new ForecastThermalSpan(0, 6 * 2500, -10, -10, 16, 26), span);
    }

    [Fact]
    public void PlantsEightIsGatedOutOfDevilstrandSowingAndPlantsTenIsIn()
    {
        var crop = ColonyModelFixtures.Devilstrand().Crops.Single(c => c.Name == ColonyModelFixtures.Crop);
        Assert.Equal(10, crop.SowMinSkill.Value);

        (bool Available, string Finding) Gate(int level, bool disabled = false) =>
            PawnCycle.SowGate(Pawn() with { Skills = EquatableArray.From(new[] { new WorkSkill { Name = "Plants", Level = level, Disabled = disabled } }) }, crop);

        Assert.Equal((false, ""), Gate(8));
        Assert.Equal((true, ""), Gate(10));
        Assert.Equal((false, "plants_disabled:Anna"), Gate(12, disabled: true));
    }

    [Fact]
    public void AnUnreadSkillMakesSowingUnavailableAndReported()
    {
        var crop = ColonyModelFixtures.Devilstrand().Crops.Single(c => c.Name == ColonyModelFixtures.Crop);
        var pawn = Pawn() with { Skills = default };

        Assert.Equal((false, "plants_skill_unknown:Anna"), PawnCycle.SowGate(pawn, crop));
        Assert.Equal((false, "sow_skill_requirement_unknown:Plant_Devilstrand"), PawnCycle.SowGate(Pawn(), crop with { SowMinSkill = default }));
    }

    [Fact]
    public void ARealReviewFrameYieldsTwelveCyclesPerPawn()
    {
        var input = ColonyModelFixtures.Devilstrand();
        var pawn = input.Pawns.Single();

        var year = PawnCycle.Year(input, pawn);

        Assert.Equal(Enumerable.Range(0, 12), year.Select(c => c.Twelfth));
        Assert.All(year, c => Assert.True(c.Valid(), string.Join(",", c.Findings)));
        Assert.True(year[0].DaylightHours.Value < year[6].DaylightHours.Value);
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
