using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ObservedShelterTests
{
    private static readonly EquatableArray<Cell> Interior = [new(1, 1), new(2, 1), new(1, 2), new(2, 2), new(1, 3), new(2, 3)];
    private static readonly Room Physical = new() { Id = "room-7", Cells = Interior, Beds = ["bed"], Enclosed = Known(false), Roofed = Known(false) };

    private static RoundsFacts Facts()
    {
        var facts = ForecastCoupledTests.ConstructionReview();
        var cells = Enumerable.Range(0, 4).SelectMany(x => Enumerable.Range(0, 5).Select(z => new Cell(x, z)));
        return facts with
        {
            Upkeep = facts.Upkeep with { Rooms = Known(new RoomObservation { Rooms = [Physical] }) },
            PlannedShelters = [new() { Id = 7, Region = RegionRules.RectRegion(new(20, 20, 2, 3)) }],
            ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with
            {
                Food = facts.ForecastObserved.Coupled!.Food with { Options = [] }, FoodUses = [],
                Construction = facts.ForecastObserved.Coupled!.Construction with { Options = [] },
                Thermal = new()
                {
                    Outdoors = Known(-10d), ThinRoofExchangePerTick = Known(.00005), WallExchangePerTick = Known(.000017),
                    Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, Known(-10d),
                        Interior.Contains(c) && c.Z == 1 ? "none" : "thin", !Interior.Contains(c), false, false,
                        "", "", false, false, 0, Interior.Contains(c) && c.Z == 1))),
                    Workers = [new("pawn", Known(5d))],
                    RoofRoutes = [new("pawn", new(1, 1), Known(1d)), new("pawn", new(2, 1), Known(1d))],
                    Sources = [new("heater", new(1, 2), true, Known(.35), Known(20d), Known(1000d))]
                }
            } }
        };
    }

    [Fact]
    public void LayoutRelocationKeepsActualLeakyRoomAsARoofRepairAlternative()
    {
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(Facts(), 300));
        var observed = generated.Context.Shelters.Single(r => r.Room.Source == ForecastShelterSource.Observed);
        Assert.Equal("room-7", observed.Room.Value);
        Assert.True(observed.InitialTemperature.IsKnown);
        Assert.Equal(2, observed.AlternativePrerequisites.Count);
        Assert.Contains(generated.Alternatives, a => a.ExclusiveOutcome == "shelter/observed/room-7");
        Assert.Contains(generated.Context.Shelters, r => r.Room == new ForecastShelterId(ForecastShelterSource.Planned, "7"));
    }

    [Fact]
    public void MatchingPlannedAndPhysicalFootprintsProduceOneShelter()
    {
        var facts = Facts() with { PlannedShelters = [new() { Id = 7, Region = RegionRules.RectRegion(new(1, 1, 2, 3)) }] };
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300));
        var room = Assert.Single(generated.Context.Shelters);
        Assert.Equal(ForecastShelterSource.Observed, room.Room.Source);
        Assert.Equal(2, room.AlternativePrerequisites.Count);
    }

    [Fact]
    public void MissingObservedGeometryOrHeatSourceRemainsExplicit()
    {
        var facts = Facts();
        var missing = facts with { Upkeep = facts.Upkeep with { Rooms = Known(new RoomObservation { Rooms = [Physical with { Cells = [] }] }) } };
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(missing, 300));
        Assert.Contains("unknown_observed_shelter_geometry:room-7", generated.Findings);
        Assert.DoesNotContain(generated.Context.Shelters, r => r.Room.Source == ForecastShelterSource.Observed);
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with
            { Thermal = facts.ForecastObserved.Coupled!.Thermal! with { Sources = [] } } } };
        generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300));
        var room = generated.Context.Shelters.Single(r => r.Room.Source == ForecastShelterSource.Observed);
        Assert.False(room.InitialTemperature.IsKnown);
        Assert.Contains("unknown_heat_source", room.Findings);
    }

    [Fact]
    public void PhysicalRoomsHaveABoundedDeterministicCensus()
    {
        var facts = Facts();
        var rooms = Enumerable.Range(0, 5).Select(i => Physical with { Id = "room-" + i,
            Cells = EquatableArray.From(Interior.Select(c => new Cell(c.X + i * 10, c.Z))) }).Reverse();
        facts = facts with { PlannedShelters = [], Upkeep = facts.Upkeep with { Rooms = Known(new RoomObservation { Rooms = EquatableArray.From(rooms) }) } };
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300));
        Assert.True(generated.Truncated);
        Assert.Contains("shelter_geometry_truncated", generated.Findings);
        Assert.Equal(4, generated.Context.Shelters.Count);
        Assert.Equal("room-0", generated.Context.Shelters[0].Room.Value);
    }
}