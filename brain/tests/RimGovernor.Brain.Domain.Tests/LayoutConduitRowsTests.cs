using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LayoutConduitRowsTests
{
    [Fact]
    public void EachConduitDecisionBecomesALayoutConduitRowWithItsVerdictReasonAndLink()
    {
        var tiled = new TileResult();
        tiled.ConduitDecisions.Add(new ConduitDecision(ConduitRef.ForRoom(2), ConduitRef.ForSite("turbine-1"), LayoutTileRules.TilePlace, LayoutConduitRules.ReasonConduitKept));
        tiled.ConduitDecisions.Add(new ConduitDecision(ConduitRef.ForRoom(3), ConduitRef.ForRoom(2), LayoutTileRules.TilePlace, LayoutConduitRules.ReasonConduitRouted));
        tiled.ConduitDecisions.Add(new ConduitDecision(ConduitRef.ForRoom(4), default, LayoutTileRules.TileUnsatisfied, LayoutConduitRules.ReasonConduitBlocked));

        var rows = LayoutPlanning.ConduitRows(new StationAdvice { Tiled = tiled }).ToList();

        Assert.All(rows, r => Assert.Equal(("layout_conduit", "layout"), (r.Kind, r.Component)));
        Assert.Equal(
            [
                ("room:2", "place", "conduit_kept", "site:turbine-1"),
                ("room:3", "place", "conduit_routed", "room:2"),
                ("room:4", "unsatisfied", "conduit_no_route", ""),
            ],
            rows.Select(r => (r.Target, r.Verdict, r.Reason, (string)r.Attrs!["to"]!)));
    }

    [Fact]
    public void AnAdviceWithoutATiledPlanHasNoConduitRows() =>
        Assert.Empty(LayoutPlanning.ConduitRows(new StationAdvice()));
}
