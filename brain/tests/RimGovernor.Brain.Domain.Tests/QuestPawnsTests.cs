using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The quest capacity states: calm, the roster split and the pawn census are named states, never an unread fact (#3924).</summary>
public sealed class QuestPawnsTests
{
    [Fact]
    public void PodsAboutToOpenMakeTheColonyBusy()
    {
        var calm = QuestCapacity.QuestCalmColony(new ReviewFacts(), new EmergencyDecision(true, []), true);

        Assert.IsType<QuestCalm.Busy>(calm);
    }

    [Fact]
    public void AnEmergencyThatIsNotClearLeavesTheColonyUnsettled()
    {
        var calm = QuestCapacity.QuestCalmColony(new ReviewFacts(), new EmergencyDecision(false, []), false);

        Assert.IsType<QuestCalm.Unsettled>(calm);
    }

    [Fact]
    public void AnUnplannedWorkDecisionLeavesTheRosterUnplanned()
    {
        var roster = new WorkDecision { State = WorkDecisionState.CheckboxMode, Matches = false };

        var split = QuestCapacity.QuestRosterOf(new WorkPawnsRead([]), [], roster);

        Assert.IsType<QuestRoster.Unplanned>(split);
    }

    [Fact]
    public void AFreshReviewHasNoPawnCensusAndNoPlannedRoster()
    {
        var f = new ReviewFacts();

        Assert.IsType<QuestPawns.Unseen>(f.QuestPawns);
        Assert.IsType<QuestRoster.Unplanned>(f.QuestRoster);
        Assert.IsType<QuestCalm.Unsettled>(f.QuestCalm);
    }
}
