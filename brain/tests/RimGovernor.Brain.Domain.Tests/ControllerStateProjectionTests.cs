using RimGovernor.Brain.Observation;
using RimGovernor.Brain.PlayerApi;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The state and routines projections name what the controller has not observed instead of carrying an unread
/// fact: a world seen, a world not seen, a control state that names the world or does not, a review run or not.
/// </summary>
public sealed class ControllerStateProjectionTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly Identity World = new() { Colony = new ColonyId("colony-1"), Load = new LoadId("load-1"), Map = new MapId(3), Tick = new Tick(42) };

    private static ControllerSnapshot Snapshot(ControllerWorld world, ControllerControl control, bool connected = true) =>
        new() { Connected = connected, Mode = "manual", World = world, Control = control };

    [Fact]
    public void AnObservedWorldProjectsItsIdentityTickPauseAndTime()
    {
        var state = ReadProjections.State(Snapshot(new ControllerWorld.Observed(World, true, ObservedAt), new ControllerControl.Unmatched()));

        Assert.Equal("colony-1", state.Identity!.ColonyId);
        Assert.Equal(42L, state.Game.Tick);
        Assert.True(state.Game.Paused);
        Assert.Equal("2026-10-24T12:00:00Z", state.Game.ObservedAt);
        Assert.False(state.Game.Stale);
        Assert.Null(state.Generation);
        Assert.Null(state.ActivePlanId);
    }

    [Fact]
    public void AnUnobservedWorldProjectsNoIdentityAndReadsStale()
    {
        var state = ReadProjections.State(Snapshot(new ControllerWorld.Unobserved(), new ControllerControl.Unmatched(), connected: false));

        Assert.Null(state.Identity);
        Assert.Null(state.Game.Tick);
        Assert.Null(state.Game.Paused);
        Assert.Null(state.Game.ObservedAt);
        Assert.True(state.Game.Stale);
    }

    [Fact]
    public void AMatchingControlStateProjectsItsGenerationAndActivePlan()
    {
        var generation = new GenerationSnapshot { Colony = new ColonyId("colony-1"), Load = new LoadId("load-1"), Map = new MapId(3), Plan = new PlanId("plan-7") };

        var state = ReadProjections.State(Snapshot(new ControllerWorld.Observed(World, false, ObservedAt), new ControllerControl.Matching(generation)));

        Assert.Equal("plan-7", state.ActivePlanId);
        Assert.Equal("plan-7", state.Generation!.Plan);
    }

    [Fact]
    public void ARoutinesStatusNamesWhetherAReviewHasRun()
    {
        var reviewed = ReadProjections.Rounds(new RoundsStatus { LastReview = new ReviewProgress.Reviewed(new Tick(900)) });
        var none = ReadProjections.Rounds(new RoundsStatus { LastReview = new ReviewProgress.NotReviewed() });

        Assert.Equal(900L, reviewed.LastReviewTick);
        Assert.Null(none.LastReviewTick);
    }
}
