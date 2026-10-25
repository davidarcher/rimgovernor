using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ObservedShelterTests
{
    private static readonly EquatableArray<Cell> Interior = [new(1, 1), new(2, 1), new(1, 2), new(2, 2), new(1, 3), new(2, 3)];
    private static readonly (RoomState Room, IEnumerable<Cell> Cells) Physical = (RoomFixtures.State("room-7", enclosed: false, roofed: false, beds: ["bed"]), Interior);

    private static ReviewFacts Facts()
    {
        var facts = ForecastCoupledTests.ConstructionReview();
        var cells = Enumerable.Range(0, 4).SelectMany(x => Enumerable.Range(0, 5).Select(z => new Cell(x, z)));
        return facts with
        {
            Upkeep = facts.Upkeep with { Rooms = RoomFixtures.Census(Physical) },
            PlannedShelters = [new() { Id = 7, Region = RegionRules.RectRegion(new(20, 20, 2, 3)) }],
            ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with
            {
                Food = facts.ForecastObserved.Coupled!.Food with { Options = [] }, FoodUses = [],
                Construction = facts.ForecastObserved.Coupled!.Construction with { Options = [] },
                Thermal = CoupledThermalTests.NoThermal with
                {
                    Outdoors = -10d, ThinRoofExchangePerTick = .00005, WallExchangePerTick = .000017,
                    Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, -10d,
                        Interior.Contains(c) && c.Z == 1 ? "none" : "thin", !Interior.Contains(c), false,
                        "", false, false, 0, Interior.Contains(c) && c.Z == 1))),
                    Workers = [new("pawn", 5d)],
                    RoofRoutes = [new("pawn", new(1, 1), new RoofApproach.Path(1d)), new("pawn", new(2, 1), new RoofApproach.Path(1d))],
                    Sources = [new("heater", new(1, 2), true, .35, 20d, CoupledThermalTests.Powered(1000d))]
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
        observed.Estimated();
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
    public void APlannedRoomsLastRoofCellIsExemptFromTheReliefGateButNotBuiltForDevelopmentWhenTheRoomHoldsItsHeat()
    {
        // One designated cell of six is open and the heater holds the room at 20°: closing it relieves no exposure.
        static ReviewFacts Warm(ReviewFacts facts) => facts with { ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with
        {
            Thermal = facts.ForecastObserved.Coupled!.Thermal! with
            {
                Cells = EquatableArray.From(facts.ForecastObserved.Coupled!.Thermal!.Cells.Select(c => c with
                {
                    Temperature = Interior.Contains(c.Cell) ? 20d : c.Temperature,
                    Roof = c.Cell == new Cell(1, 1) || !Interior.Contains(c.Cell) ? c.Roof : "thin", BuildRoof = c.Cell == new Cell(1, 1)
                })),
                RoofRoutes = [new("pawn", new(1, 1), new RoofApproach.Path(1d))]
            }
        } } };
        ForecastCoupledSelection Select(ReviewFacts facts)
        {
            facts = facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300) };
            return ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new(100), 300d / GameTime.TicksPerDay, facts,
                new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []), facts.CoupledAlternatives!);
        }

        var planned = Warm(Facts() with { PlannedShelters = [new() { Id = 7, Region = RegionRules.RectRegion(new(1, 1, 2, 3)) }] });
        var result = Select(planned);
        Assert.Equal(7, Assert.Single(ForecastCoupledCandidates.GenerateCoupledActivities(planned, 300)!.Context.Shelters).PlannedRoom);
        // The planned room's roof cell passes the relief gate and is evaluated, but the roof unit it completes is a planned
        // room's, which counts no development (#3828), and the unpriced room has no credit: every evaluated portfolio ties the
        // baseline's development and the roof is not built.
        Assert.DoesNotContain(result.Rejected, r => r.Reason.StartsWith("construction_without_supported_service_relief", StringComparison.Ordinal));
        Assert.Contains(result.Rejected, r => r.Consequences is not null);
        Assert.DoesNotContain(result.Selected.Plan.Allocations, a => a.Activity.StartsWith("roof/observed/room-7/1/1", StringComparison.Ordinal));

        // An observed room no planned room matches keeps the relief gate: no exposure relieved, no roof work.
        var unplanned = Select(Warm(Facts() with { PlannedShelters = [] }));
        Assert.DoesNotContain(unplanned.Selected.Plan.Allocations, a => a.Activity.StartsWith("roof/", StringComparison.Ordinal));
        Assert.Contains(unplanned.Rejected, r => r.Reason.StartsWith("construction_without_supported_service_relief", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOpenRoofRoomAsItStandsTrendsToOutdoorsAtTheGamesNoRoofRate()
    {
        // A 20° room at -10° outdoors with an idle heater; two of its six cells are open, or every cell is roofed.
        static ForecastShelterPlan Standing(bool open)
        {
            var facts = Facts() with { PlannedShelters = [] };
            var thermal = facts.ForecastObserved.Coupled!.Thermal! with
            {
                NoRoofExchangePerTick = .0007,
                Cells = EquatableArray.From(facts.ForecastObserved.Coupled!.Thermal!.Cells.Select(c => Interior.Contains(c.Cell)
                    ? c with { Temperature = 20d, Roof = open && c.Cell.Z == 1 ? "none" : "thin", BuildRoof = open && c.Cell.Z == 1 } : c)),
                Sources = [new("heater", new(1, 2), true, 0d, 20d, CoupledThermalTests.Powered(1e6))],
            };
            facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with { Thermal = thermal } } };
            return ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300)!.Context.Shelters.Single(r => r.Room.Source == ForecastShelterSource.Observed);
        }
        static double Enclosed(ForecastShelterPlan room) => Assert.IsType<ForecastShelterStanding.Enclosed>(room.Estimated().Standing).ExchangePerTick;
        var leaky = Standing(true); var roofed = Standing(false);
        // RoomTempTracker's roof coverage terms: an open cell exchanges at 0.0007 a tick against 0.00005 under thin roof.
        Assert.Equal(2d / 6 * (.0007 - .00005), Enclosed(leaky) - Enclosed(roofed), 12);
        // The finished shelter is thin-roofed either way.
        Assert.Equal(roofed.Estimated().ExchangePerTick, leaky.Estimated().ExchangePerTick, 12);
        var horizon = 2400d;
        foreach (var room in new[] { leaky, roofed })
            Assert.Equal(-10 + 30 * Math.Exp(-Enclosed(room) * horizon),
                ForecastShelterPolicy.Standing(room, horizon)[^1].Temperature, 9);
        Assert.True(ForecastShelterPolicy.Standing(leaky, horizon)[^1].Temperature < ForecastShelterPolicy.Standing(roofed, horizon)[^1].Temperature - 1);
    }

    [Fact]
    public void MissingObservedGeometryOrHeatSourceRemainsExplicit()
    {
        var facts = Facts();
        var missing = facts with { Upkeep = facts.Upkeep with { Rooms = RoomFixtures.Census(Physical with { Cells = [] }) } };
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(missing, 300));
        Assert.Contains("unknown_observed_shelter_geometry:room-7", generated.Findings);
        Assert.DoesNotContain(generated.Context.Shelters, r => r.Room.Source == ForecastShelterSource.Observed);
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with
            { Thermal = facts.ForecastObserved.Coupled!.Thermal! with { Sources = [] } } } };
        generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300));
        var room = generated.Context.Shelters.Single(r => r.Room.Source == ForecastShelterSource.Observed);
        Assert.IsType<ForecastShelterThermal.Estimated>(room.Thermal);
        Assert.DoesNotContain("unknown_heat_source", room.Findings);
    }

    [Fact]
    public void PhysicalRoomsHaveABoundedDeterministicCensus()
    {
        var facts = Facts();
        var rooms = Enumerable.Range(0, 5).Select(i => (Physical.Room with { Id = "room-" + i },
            Interior.Select(c => new Cell(c.X + i * 10, c.Z)))).Reverse();
        facts = facts with { PlannedShelters = [], Upkeep = facts.Upkeep with { Rooms = RoomFixtures.Census([.. rooms]) } };
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300));
        Assert.True(generated.Truncated);
        Assert.Contains("shelter_geometry_truncated", generated.Findings);
        Assert.Equal(4, generated.Context.Shelters.Count);
        Assert.Equal("room-0", generated.Context.Shelters[0].Room.Value);
    }
}
