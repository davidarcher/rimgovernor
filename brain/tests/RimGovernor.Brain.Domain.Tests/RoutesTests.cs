using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The routes census reads whole (#3903): each pawn's travel to a facility is a named state. The game's reachable
/// answer holds whether or not its path was measured, so only an unreachable pair leaves a facility without travel.
/// </summary>
public sealed class RoutesTests
{
    private static RimGovernor.Brain.GameFacts.Observations.RoutesFacts Census(params RouteTravel[] travel) => new()
    {
        PawnIds = [.. travel.Select(t => t.Pawn)],
        Facilities =
        [
            new()
            {
                Id = "store",
                Kind = RouteFacilityKind.Storage,
                Cell = new(4, 4),
                Room = "room-1",
                Travel = [.. travel],
                Breaches =
                [
                    new() { Cell = new(6, 4), Edifice = "Thing_Wall2", Distance = 3 },
                    new() { Cell = new(4, 6), Edifice = "Thing_Wall1", Pending = "Thing_Door1", Distance = 1 },
                    new() { Cell = new(2, 4), Edifice = "Thing_Wall3", Distance = 2 },
                ],
            },
        ],
        Traffic = [], TrafficSamples = 0,
    };

    [Fact]
    public void ASkippedOrUnpathedPairStillReachesTheFacility()
    {
        var p = RoutesRules.DefaultRoutesPolicy();
        Assert.False(RoutesRules.ReviewRoutes(Census(new RouteTravel.Skipped { Pawn = "a" }, new RouteTravel.Unreachable { Pawn = "b" }), p).Active);
        Assert.False(RoutesRules.ReviewRoutes(Census(new RouteTravel.Unpathed { Pawn = "a" }), p).Active);
        Assert.False(RoutesRules.ReviewRoutes(Census(new RouteTravel.Measured { Pawn = "a", Cost = 40, Cells = 12 }), p).Active);
    }

    [Fact]
    public void AFacilityNoPawnReachesIsADeficitWithItsUnorderedBreachesNearestFirst()
    {
        var p = RoutesRules.DefaultRoutesPolicy();
        var review = RoutesRules.ReviewRoutes(Census(new RouteTravel.Unreachable { Pawn = "a" }, new RouteTravel.Unreachable { Pawn = "b" }), p);
        var deficit = Assert.Single(review.Deficits);
        Assert.True(review.Active);
        Assert.Equal(1, deficit.Pending);
        Assert.Equal([new Cell(2, 4), new Cell(6, 4)], deficit.Breaches.Select(b => b.Cell));

        var proposal = RoutesRules.SelectRoutesMethod(review, new RoutesFacts { DoorAvailable = true}, p);
        Assert.Equal((RoutesMethod.Build, new Cell(2, 4)), (proposal.Method, proposal.Breaches[0]));
    }

    [Fact]
    public void AnUnavailableDoorCannotOpenTheRoute()
    {
        var p = RoutesRules.DefaultRoutesPolicy();
        var review = RoutesRules.ReviewRoutes(Census(new RouteTravel.Unreachable { Pawn = "a" }), p);
        Assert.Equal(RoutesMethod.DoorUnavailable, RoutesRules.SelectRoutesMethod(review, new RoutesFacts { DoorAvailable = false }, p).Method);
    }

    [Fact]
    public void AMeasuredPathWithANegativeCostIsAContractError()
    {
        var census = Census(new RouteTravel.Measured { Pawn = "a", Cost = -1, Cells = 3 });
        Assert.Throws<DomainException>(() => RoutesRules.ValidateRoutes(census));
    }
}
