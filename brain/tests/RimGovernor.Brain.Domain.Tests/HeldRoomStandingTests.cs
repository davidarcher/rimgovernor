using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HeldRoomStandingTests
{
    private static StationFacts Facts(IReadOnlySet<int>? built, params int[] pinned) => new()
    {
        Colonists = Fact.Known(5L),
        Survey = new MapSurvey(),
        Plan = new LayoutPlan
        {
            Rooms =
            [
                new PlannedRoom { Id = 6, Role = LayoutSurveyRules.PlannedBedroom },
                new PlannedRoom { Id = 7, Role = LayoutSurveyRules.PlannedKitchen },
                new PlannedRoom { Id = 8, Role = LayoutSurveyRules.PlannedKitchen, Replaces = 7 },
            ],
        },
        Built = built,
        Pinned = new HashSet<int>(pinned),
    };

    [Fact]
    public void RoomsUnderConstructionStandForTheEmbeddingSoNoSecondOneIsSited()
    {
        // A replan re-sited the room being built beside it while the held one stayed: 3 bedrooms for 5 colonists.
        var ids = Facts(new HashSet<int> { 7 }, 6, 7).StandingIds()!;
        Assert.Contains(6, ids);
        Assert.Contains(7, ids);
    }

    [Fact]
    public void UnheldRoomsAndRebuildSuccessorsStayFreeToMove()
    {
        var ids = Facts(new HashSet<int> { 7 }, 8).StandingIds()!;
        Assert.DoesNotContain(6, ids);
        Assert.DoesNotContain(8, ids);
    }

    [Fact]
    public void UnknownCensusStaysUnknown() => Assert.Null(Facts(null, 6).StandingIds());
}
