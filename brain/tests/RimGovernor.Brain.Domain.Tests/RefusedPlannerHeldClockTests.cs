using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3706: a planner refused shared admission on a held clock is not re-run by its marks or its cadence (the held
/// clock advances no tick, and the refusal is no wall-clock wait), but the held clock's retry steps continue (a
/// refused resume is a HeldException, retried after the back-off) and the first one past
/// <see cref="StepReasons.DefaultFullStepEvery"/> is promoted to a full step that runs every planner. That
/// safety net is the re-run the deleted <c>PlannerQueue.Stalled</c> once supplied.
/// </summary>
public sealed class RefusedPlannerHeldClockTests
{
    private static PlannerEntry Entry(string name) => new()
    {
        Name = name,
        Class = PlannerClass.Critical,
        Configured = _ => true,
        Run = (_, _, _, _, _, _) => Task.FromResult(default(Verdict)),
    };

    private static readonly PlannerEntry[] Catalog = [Entry("colony"), Entry("shelter")];

    private static readonly DateTimeOffset LastFull = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARefusedPlannerOnAHeldClockRunsAgainOnTheRetryPromotedToAFullStep()
    {
        var queue = new PlannerQueue { Catalog = () => Catalog };
        queue.Ran(queue.Selection(0, true, null), Catalog.Select(e => e.Name), _ => null, 0, null);

        // A hold wakes every entry at 600; colony is refused by the shared admission check.
        var refused = Verdict.Refuse(Cause.SharedAdmission, "safeguard", "");
        Assert.False(refused.WaitsOnRealTime);
        queue.Mark("colony");
        queue.Mark("shelter");
        var wake = queue.Selection(600, false, null);
        queue.Ran(wake, ["colony", "shelter"], name => name == "colony" ? refused : Verdict.BuildingReasonNoDeficit, 600, null);

        // The clock stays held at 600: a retry step inside the safety-net interval selects nothing.
        var early = PlannerScheduler.Promoted(StepCause.Retry, LastFull, LastFull + TimeSpan.FromSeconds(5), TimeSpan.Zero);
        Assert.Equal(StepCause.Retry, early);
        Assert.False(queue.Selection(600, false, null).Planners);

        // The retry past the interval is a full step, and a full selection runs the refused planner again.
        var late = PlannerScheduler.Promoted(StepCause.Retry, LastFull, LastFull + StepReasons.DefaultFullStepEvery, TimeSpan.Zero);
        Assert.Equal(StepCause.Full, late);
        var full = queue.Selection(600, true, null);
        Assert.True(full.Planners);
        Assert.Null(full.Pick);
        Assert.Equal("full", full.Why!["colony"]);
    }

    [Fact]
    public void OnlyReviewAndRetryStepsArePromoted()
    {
        var late = LastFull + StepReasons.DefaultFullStepEvery;
        Assert.Equal(StepCause.Full, PlannerScheduler.Promoted(StepCause.Review, LastFull, late, TimeSpan.Zero));
        Assert.Equal(StepCause.Wake, PlannerScheduler.Promoted(StepCause.Wake, LastFull, late, TimeSpan.Zero));
        Assert.Equal(StepCause.Full, PlannerScheduler.Promoted(StepCause.Retry, default, LastFull, TimeSpan.Zero));
        Assert.Equal(StepCause.Retry, PlannerScheduler.Promoted(StepCause.Retry, LastFull, late, TimeSpan.FromMinutes(5)));
    }
}
