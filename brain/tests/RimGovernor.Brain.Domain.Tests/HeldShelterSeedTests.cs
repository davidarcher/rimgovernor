using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HeldShelterSeedTests
{
    private static StationFacts Facts(IReadOnlySet<int> built, IReadOnlySet<int> pinned) => new()
    {
        Colonists = Fact.Known(5L),
        Survey = new MapSurvey(),
        Plan = new LayoutPlan { Rooms = [new PlannedRoom { Id = 6, Role = PlannedShelter }] },
        Built = built,
        Pinned = pinned,
    };

    [Fact]
    public void ShelterBeingBuiltFillsTheShelterNodeInsteadOfSitingASecondOne()
    {
        // Every replan re-sited the unbuilt shelter beside the one the colony was building: 3 shelters for 5 colonists.
        Assert.False(Facts(new HashSet<int>(), new HashSet<int> { 6 }).SeedRoomsOf().Shelter);
    }

    [Fact]
    public void ShelterNobodyIsBuildingIsStillOwedItsNode()
    {
        Assert.True(Facts(new HashSet<int>(), new HashSet<int>()).SeedRoomsOf().Shelter);
        Assert.True(Facts(new HashSet<int> { 6 }, new HashSet<int> { 6 }).SeedRoomsOf().Shelter);
    }
}
