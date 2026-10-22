using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// An urgent build holds the clock from its admission until a rounds review whose step began after its placement settled
/// has compared its placed sites (#3537), and that review compares those sites alone.
/// </summary>
public sealed class UrgentBuildsTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static ClockWatchDecision Decide(bool urgent) => ClockWatchRules.EvaluateClockWatch(
        new ClockWatchFacts
        {
            Current = World,
            Tick = new(100),
            SinceStarted = TimeSpan.FromSeconds(1),
            SinceObserved = TimeSpan.FromMilliseconds(500),
            Emergency = EmergencySnapshot.New(World, new(100), new EmergencyFacts
            {
                ColonistsComplete = Known(true),
                DependentPatients = Known(EquatableArray.From<EmergencyDependentPatient>([])),
            }),
            EventsOpen = true,
            Status = new ClockWatchStatus { Snapshot = World, Tick = new(100), State = ClockWatchState.Held, NativeTickBoundary = Known(true) },
            CombatPlan = Known(false),
            UrgentBuilds = urgent,
        },
        new ClockWatchLimits { MaxAge = TimeSpan.FromSeconds(10) });

    [Fact]
    public void AWaitingUrgentBuildHoldsTheClockAndNoneHoldsNothing()
    {
        Assert.Equal([ClockWatchReason.UrgentBuild], Decide(true).Refused.Items);
        Assert.True(Decide(false).Run);
    }

    [Fact]
    public void AnUrgentBuildWaitsUntilAReviewCoveringItsSettledPlacementCommits()
    {
        var builds = new UrgentBuilds();
        var wall = new Building("Wall", new Cell(1, 1), Rotation.North, "WoodLog");
        builds.Placing(new PlanId("shell"), [wall]);
        Assert.True(builds.Waiting);

        // A step that began before the placement settled covers nothing: its review compares no urgent site.
        Assert.Empty(builds.Settled(_ => false));
        Assert.Empty(builds.Covered.Items);
        builds.Reviewed([]);
        Assert.True(builds.Waiting);

        var covered = builds.Settled(_ => true);
        Assert.Equal([new PlanId("shell")], covered);
        Assert.Equal([wall], builds.Covered.Items);
        builds.Reviewed(covered);
        Assert.False(builds.Waiting);
        Assert.Empty(builds.Covered.Items);
    }

    [Fact]
    public void AWaitingUrgentBuildRunsTheRounderAtTheNextReviewStep()
    {
        const long every = ClockSchedulerConfig.DefaultRoundsEveryTicks;
        Assert.Equal("urgent_build", ClockScheduler.RoundsSelection(StepCause.Review, false, 10_600, true, 10_000, every, urgent: true));
        Assert.Null(ClockScheduler.RoundsSelection(StepCause.Review, false, 10_600, true, 10_000, every));
    }
}
