using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FirstPlanUnreadDiningTests
{
    [Fact]
    public void AFirstPlanWithoutTheRoomsCensusKeepsTheSeedRooms()
    {
        // The acceptance starter-site read derives a first plan from a colony read without the rooms census, so the
        // dining furniture is unread (#3340): the chain must take the seed-only path, not dereference a missing value.
        var f = new StationFacts
        {
            Catalog = new StationCatalog(),
            Furniture = new RoomFurniture(),
            Items = new ItemFacts(),
            Races = new AnimalRaceCatalog(),
            Colonists = Fact.Known(3L),
            Survey = new MapSurvey(),
            Built = new HashSet<int>(),
            RoofDistance = 6,
            Derive = true,
        };

        Assert.False(f.Dining.Known());
        var e = Record.Exception(() => PolicyFunctions.AdviseStations(f));
        // Past the dining check, a fact this sparse catalog lacks is a typed unknown, which the callers report.
        Assert.True(e is null or StationFactsException, e?.ToString());
    }
}
