using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;
using SleepingTemperature = RimGovernor.Contracts.Wire.Observations.SleepingTemperature;

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
        var observed = ColonyFactsCoreTests.Reading() with { Identity = new ObservationIdentity { Context = ColonyFactsCoreTests.Snapshot().Context with { Tick = 100 } } };
        var laid = observed with { Facts = observed.Facts with { FoodDays = new FoodRunway.Days(3.0) } };
        inputs.Begin(World);
        inputs.Left("observe", observed);
        inputs.Left("layout", laid);
        inputs.Left("d_owed", laid);
        Assert.Null(inputs.Changed());

        // The next review: the observation moved on (its tick), the layout lap left the same food days but a
        // different mood census, and the owed lap left its reading untouched.
        inputs.Begin(World);
        var observedAgain = observed with { Identity = new ObservationIdentity { Context = ColonyFactsCoreTests.Snapshot().Context with { Tick = 700 } } };
        inputs.Left("observe", observedAgain);
        var laidAgain = laid with { Identity = observedAgain.Identity, Facts = laid.Facts with { MoodPawns = [MoodFixtures.Has("pawn", .5, .4)] } };
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
        var observed = ColonyFactsCoreTests.Reading() with { Identity = new ObservationIdentity { Context = ColonyFactsCoreTests.Snapshot().Context with { Tick = 100 } } };
        inputs.Begin(World);
        inputs.Left("observe", observed);
        inputs.Left("layout", observed);
        inputs.Begin(World);
        // A structurally equal reading, not the same instance.
        inputs.Left("observe", ColonyFactsCoreTests.Reading() with { Identity = new ObservationIdentity { Context = ColonyFactsCoreTests.Snapshot().Context with { Tick = 100 } } });
        inputs.Left("layout", observed with { Facts = observed.Facts with { } });
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
        Assert.Empty(RoundsLapInputs.Differences(ColonyFactsCoreTests.Reading(), ColonyFactsCoreTests.Reading()));
        Assert.Contains("Facts.Shapes", RoundsLapInputs.Uncompared);
        Assert.Contains("Facts.Items", RoundsLapInputs.Uncompared);
        Assert.Contains("Facts.CurrentConstruction", RoundsLapInputs.Uncompared);
        Assert.Contains("Facts.Gear", RoundsLapInputs.Uncompared);
        Assert.True(RoundsLapInputs.HasValueEquality(typeof(EquatableArray<MoodPawn>)));
        Assert.True(RoundsLapInputs.HasValueEquality(typeof(ObservationIdentity)));
        Assert.False(RoundsLapInputs.HasValueEquality(typeof(List<string>)));
        Assert.False(RoundsLapInputs.HasValueEquality(typeof(IReadOnlyList<string>)));
    }

    [Fact]
    public void AnotherWorldDropsThePreviousReview()
    {
        var inputs = new RoundsLapInputs();
        inputs.Begin(World);
        inputs.Left("observe", ColonyFactsCoreTests.Reading());
        inputs.Begin(World with { Map = new(2) });
        inputs.Left("observe", ColonyFactsCoreTests.Reading());
        Assert.Null(inputs.Changed());
        Assert.Empty(inputs.Same);
    }

    [Fact]
    public void ManyChangedPropertiesAreCappedWithACount()
    {
        var was = ColonyFactsCoreTests.Reading();
        var facts = was.Facts with
        {
            CurrentConstruction = CurrentConstruction.Empty,
            Gear = ColonyModelFixtures.NoGear(),
            FoodDays = new FoodRunway.Days(1.0), FieldCoverage = 1.0,
            Census = was.Facts.Census with { ColonistCount = 1, BedCapacity = 1, IndoorSleepingCapacity = 1, SleepingTemperature = new SleepingTemperature.Indoor(1.0, 1.0), OutdoorTemperatureC = 1.0, Resources = [new() { DefName = ConstructionDemandRules.ConstructionWoodLog.Value, Units = 1 }] },
            GrowingCells = 1L, Armed = 1L, Unarmed = 1L, Hostiles = HostileCensus.Of(1L),
            CriticalPatients = 1L, UrgentPatients = 1L,
            PowerHeadroom = 1.0, OutdoorsDark = true, Tick = new Tick(5), DisasterTick = new Tick(5), BedroomsOwed = true,
            MealClosetOwed = true, CampfireRetireOwed = true, TemperatureOwed = true, ButcherTableAffordable = true,
            CleanupPawns = true, WorkCoverage = WorkCoverageState.Matched,
            SculptureRoomsOwed = true, ShellsShort = true, HostilityOwed = true, MedicalCareOwed = true,
            MedicineCarryOwed = true, NamesOwed = true, Cooking = true, PowerRequired = true,
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
