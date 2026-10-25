using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CookingBenchRoomTests
{
    [Fact]
    public void AButcherTableInTheKitchenMeetsTheStationNeed()
    {
        var inside = ProductionBenchFixtures.Butchering("butcher", "TableButcher", "butcher-token", true, "kitchen");
        var facts = new ReviewFacts { EssentialInputs = new() { Benches = [inside] } };
        Assert.True(Rules.ButcherFacilityMet(facts));
        Assert.True(Rules.ButcherFacilityMet(facts with { EssentialInputs = new() { Benches = [ProductionBenchFixtures.InRoom(inside, null)] } }));
    }
}
