using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using SleepingTemperature = RimGovernor.Contracts.Wire.Observations.SleepingTemperature;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The review's per-lap input comparison (#3564): the rounds_review row names, per lap, the properties of the reading
/// it entered on that differ from the previous review's, so a day's ring says which laps a continuation could skip.
/// </summary>
public sealed class RoundsLapInputsTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    [Fact]
    public void NamesTheLapsWhoseEnteringReadingChangedAndTheProperties()
    {
        var inputs = new RoundsLapInputs();
        var observed = ColonyFactsCoreTests.Projection() with { Identity = new Identity { Tick = new Tick(100) } };
        var laid = observed with { Facts = observed.Facts with { FoodDays = new FoodRunway.Days(3.0) } };
        inputs.Begin(World);
        inputs.Left("observe", observed);
        inputs.Left("layout", laid);
        inputs.Left("d_owed", laid);
        Assert.Null(inputs.Changed());

        // The next review: the observation moved on (its tick), the layout lap left the same food days but a
        // different mood census, and the owed lap left its reading untouched.
        inputs.Begin(World);
        var observedAgain = observed with { Identity = new Identity { Tick = new Tick(700) } };
        inputs.Left("observe", observedAgain);
        var laidAgain = laid with { Identity = observedAgain.Identity, Facts = laid.Facts with { MoodPawns = Known<EquatableArray<MoodPawn>>([]) } };
        inputs.Left("layout", laidAgain);
        inputs.Left("d_owed", laidAgain);
        var changed = inputs.Changed();

        Assert.NotNull(changed);
        Assert.Equal("Identity", changed["layout"]);
        Assert.Equal("Identity,Facts.MoodPawns", changed["d_owed"]);
        Assert.Equal("Identity,Facts.MoodPawns", changed["d_reads"]);
        Assert.False(changed.ContainsKey("d_stock"), "no lap left a reading for d_stock to enter on");
        Assert.Empty(inputs.Same);
        Assert.True(inputs.CompareMs >= 0);
    }

    [Fact]
    public void ALapEnteringOnAnIdenticalReadingIsSame()
    {
        var inputs = new RoundsLapInputs();
        var observed = ColonyFactsCoreTests.Projection() with { Identity = new Identity { Tick = new Tick(100) } };
        inputs.Begin(World);
        inputs.Left("observe", observed);
        inputs.Left("layout", observed);
        inputs.Begin(World);
        // A structurally equal reading, not the same instance.
        inputs.Left("observe", ColonyFactsCoreTests.Projection() with { Identity = new Identity { Tick = new Tick(100) } });
        inputs.Left("layout", observed with { Facts = new RoundsFacts() });
        var changed = inputs.Changed();

        Assert.NotNull(changed);
        Assert.Empty(changed);
        Assert.Equal(["layout", "d_owed"], inputs.Same);
    }

    [Fact]
    public void PropertiesWithoutValueEqualityAreLeftOutAndNamed()
    {
        // Two default projections differ in nothing the comparison sees; the properties whose types compare by
        // reference (the catalog tables a review rebuilds) are named instead.
        Assert.Empty(RoundsLapInputs.Differences(ColonyFactsCoreTests.Projection(), ColonyFactsCoreTests.Projection()));
        Assert.Contains("Shapes", RoundsLapInputs.Uncompared);
        Assert.Contains("Facts.Items", RoundsLapInputs.Uncompared);
        Assert.Contains("Facts.CurrentConstruction", RoundsLapInputs.Uncompared);
        Assert.True(RoundsLapInputs.HasValueEquality(typeof(Fact<EquatableArray<MoodPawn>>)));
        Assert.True(RoundsLapInputs.HasValueEquality(typeof(Identity)));
        Assert.False(RoundsLapInputs.HasValueEquality(typeof(List<string>)));
        Assert.False(RoundsLapInputs.HasValueEquality(typeof(Fact<IReadOnlyList<string>>)));
    }

    [Fact]
    public void AnotherWorldDropsThePreviousReview()
    {
        var inputs = new RoundsLapInputs();
        inputs.Begin(World);
        inputs.Left("observe", ColonyFactsCoreTests.Projection());
        inputs.Begin(World with { Map = new(2) });
        inputs.Left("observe", ColonyFactsCoreTests.Projection());
        Assert.Null(inputs.Changed());
        Assert.Empty(inputs.Same);
    }

    [Fact]
    public void ManyChangedPropertiesAreCappedWithACount()
    {
        var was = ColonyFactsCoreTests.Projection();
        var facts = new RoundsFacts
        {
            FoodDays = new FoodRunway.Days(1.0), FieldCoverage = Known(1.0), Colonists = 1L, BedCapacity = 1L,
            IndoorCapacity = 1L, GrowingCells = Known(1L), Armed = Known(1L), Unarmed = Known(1L), Wood = 1L, Hostiles = Known(1L),
            CriticalPatients = Known(1L), UrgentPatients = Known(1L), SleepingTemperature = new SleepingTemperature.Indoor(1.0, 1.0), OutdoorTemperature = 1.0,
            PowerHeadroom = 1.0, OutdoorsDark = true, Tick = new Tick(5), DisasterTick = new Tick(5), BedroomsOwed = true,
            MealClosetOwed = Known(true), CampfireRetireOwed = Known(true), TemperatureOwed = true, ButcherTableAffordable = Known(true),
            CleanupPawns = true, WorkCoverage = WorkCoverageState.Matched,
        };
        var names = RoundsLapInputs.Differences(was, was with { Facts = facts });
        Assert.True(names.Count > RoundsLapInputs.NamesCap, $"{names.Count} differences");

        var inputs = new RoundsLapInputs();
        inputs.Begin(World);
        inputs.Left("observe", was);
        inputs.Begin(World);
        inputs.Left("observe", was with { Facts = facts });
        var text = Assert.IsType<string>(inputs.Changed()!["layout"]);
        Assert.Equal(RoundsLapInputs.NamesCap + 1, text.Split(',').Length);
        Assert.EndsWith(",+" + (names.Count - RoundsLapInputs.NamesCap), text);
    }
}
