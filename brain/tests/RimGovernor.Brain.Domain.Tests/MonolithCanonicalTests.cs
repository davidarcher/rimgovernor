using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class MonolithCanonicalTests
{
    private static RimGovernor.Brain.GameFacts.Observations.MonolithState Monolith() => new()
    {
        MonolithId = "monolith", PendingVoidStructureIds = [], VoidNodeId = null,
        VoidNodePawnIds = [], CanActivate = true, NextLevelDef = null,
        CodexShortfall = 0, BlockingConditions = [], GleamingInteractionAvailable = false,
    };

    private static RimGovernor.Brain.GameFacts.Observations.AnomalyColonyFacts Colony(RimGovernor.Brain.GameFacts.Observations.MonolithState monolith) => new()
    {
        Incidents = new() { MonolithSpawned = true, AmbientHorrorMode = false, Level = 0 },
        Monolith = monolith,
    };

    private static AwakenGate Gate() => new() { Threat = ColonyFactsCoreTests.QuietThreat };

    [Fact]
    public void BridgeRetainsActualMonolithAndIncidentRows()
    {
        var facts = Colony(Monolith());
        var view = Assert.IsType<AnomalyState.Active>(AnomalyColonyReads.AnomalyOf(new RimGovernor.Brain.GameFacts.Observations.AnomalySection.Active(facts)));
        Assert.Same(facts, view.Monolith);
        Assert.Same(facts.Monolith, view.Monolith.Monolith);
        Assert.Same(facts.Incidents, view.Monolith.Incidents);
        Assert.Equal(MonolithOrder.Investigate, MonolithRules.MonolithAdvanceOwed(view.Monolith, Gate()).Order);
    }

    [Fact]
    public void NativeCodexAndVoidNodeRelationsControlTheNextStep()
    {
        var monolith = Monolith() with { CanActivate = false, CodexShortfall = 3 };
        var blocked = MonolithRules.MonolithAdvanceOwed(Colony(monolith), Gate());
        Assert.Equal(MonolithOrder.None, blocked.Order);
        Assert.Contains("short 3 entries", blocked.Hold, StringComparison.Ordinal);

        var node = monolith with { VoidNodeId = "node", VoidNodePawnIds = [] };
        Assert.Contains("no colonist", MonolithRules.MonolithAdvanceOwed(Colony(node), Gate()).Hold, StringComparison.Ordinal);
        var reachable = node with { VoidNodePawnIds = ["pawn"] };
        Assert.Equal(MonolithOrder.Interact, MonolithRules.MonolithAdvanceOwed(Colony(reachable), Gate()).Order);
        Assert.Equal(MonolithOrder.None, MonolithRules.MonolithAdvanceOwed(
            Colony(reachable) with { Incidents = new() { MonolithSpawned = true, AmbientHorrorMode = true, Level = 0 } }, Gate()).Order);
    }
}
