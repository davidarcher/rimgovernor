using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3697: a force-pausing choice dialog read inside its interactivity grace is a wait only wall-clock time ends. The
/// held clock advances no tick and nothing invalidates, so the people entry that waited on it stays marked: the next
/// step (the held clock's retry) runs it again, and a cadence run never keeps the wait. Before, the mark was cleared
/// and the research-finished dialog that opened mid-run was never read again.
/// </summary>
public sealed class DialogGraceRetryTests
{
    private static PlannerEntry Entry(string name) => new()
    {
        Name = name,
        Class = PlannerClass.Critical,
        Configured = _ => true,
        Run = (_, _, _, _, _, _) => Task.FromResult(default(Verdict)),
    };

    private static readonly PlannerEntry[] Catalog = [Entry("people"), Entry("shelter")];

    [Fact]
    public void AnEntryWaitingOnTheDialogGraceRunsAgainOnTheNextStepAtTheSameTick()
    {
        var queue = new PlannerQueue { Catalog = () => Catalog };
        queue.Ran(queue.Selection(0, true, null), Catalog.Select(e => e.Name), _ => null, 0, null);

        // The dialog's hold wakes every entry at 600; people finds the dialog not yet interactive.
        queue.Mark("people");
        queue.Mark("shelter");
        var wake = queue.Selection(600, false, null);
        queue.Ran(wake, ["people", "shelter"], name => name == "people" ? Verdict.BuildingReasonNotInteractive : Verdict.BuildingReasonNoDeficit, 600, null);

        // The clock stayed held at 600: the retry step runs people alone.
        var retry = queue.Selection(600, false, null);
        Assert.True(retry.Planners);
        Assert.Equal(["people"], Catalog.Where(e => retry.Pick!(e)).Select(e => e.Name));
        Assert.Equal("dirty", retry.Why!["people"]);

        // Answered: the mark clears and nothing runs on a further retry.
        queue.Ran(retry, ["people"], _ => Verdict.BuildingReasonAdmitted, 600, null);
        Assert.False(queue.Selection(600, false, null).Planners);
    }

    [Fact]
    public void TheDialogGraceIsNeverKeptAndIsTheEntryVerdictOverAnotherNeedsWait()
    {
        Assert.True(Verdict.BuildingReasonNotInteractive.WaitsOnRealTime);
        Assert.False(Verdict.BuildingReasonExistingWork.WaitsOnRealTime);
        Assert.False(FamilyRunner.Keepable(Verdict.BuildingReasonNotInteractive));

        var mood = new NeedReport(PeopleFamily.Mood(new PawnId("pawn-a")), Verdict.AwaitingPlan("unsupported_forecast_mood_cast", "detail"));
        var dialog = new NeedReport(PeopleFamily.Dialog, Verdict.BuildingReasonNotInteractive);
        Assert.Equal(Verdict.BuildingReasonNotInteractive, FamilyRunner.EntryVerdict([mood, dialog]));
    }
}
