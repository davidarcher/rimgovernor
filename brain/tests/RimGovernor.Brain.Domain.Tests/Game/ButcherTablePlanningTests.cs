using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class ButcherTablePlanningTests
{
    [Fact]
    public void FirstReviewDescribesBothButcherHostsTheLayoutComparisonPrices()
    {
        // Spot and table are compared by consequence (RG-HABITAT-005): the first review must describe both.
        Assert.Contains(KitchenSeparationRules.ButcherTableDefinition, DefinitionsReads.StarterDefinitions);
        Assert.Contains(Rules.ButcherSpotDefinition, DefinitionsReads.StarterDefinitions);
    }
}
