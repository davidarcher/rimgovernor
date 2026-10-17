using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class IdleControlTests
{
    [Fact]
    public void CompletedIdleReleasesThroughTheSameObservedMethodLifecycle()
    {
        var step = new PawnActivityStep(PawnActivityKind.Idle, "", "", "", 0, 200);
        var task = new SelectedActivityTask("defer/pawn", "pawn", [], step, new(1, 1), 100);
        var batch = new PawnActivities(1, [new("pawn", "method/pawn", "method", 0, 1, false, [step]) { Task = task.Id }]) { Tasks = [task] };
        batch.Validate();
        ActivityTaskRead Read(string state) => new()
        {
            Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 200 },
            Pawns = [new("pawn", new("method/pawn", "method", 1, 1, state, "reconsider_due", null, 200))]
        };
        var next = Assert.IsType<PawnActivities>(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Read("completed")));
        Assert.Empty(next.Tasks);
        Assert.True(Assert.Single(next.Orders).Release);
        Assert.Equal(2, next.Orders[0].Revision);
        Assert.Empty(next.Orders[0].Steps);
    }

    [Fact]
    public void FailedOrExpiredIdleNeverRetainsPretendSuccessfulOwnership()
    {
        var step = new PawnActivityStep(PawnActivityKind.Idle, "", "", "", 0, 200);
        var batch = new PawnActivities(1, [new("pawn", "method/pawn", "method", 0, 1, false, [step]) { Task = "defer/pawn" }])
            { Tasks = [new("defer/pawn", "pawn", [], step, new(1, 1), 100)] };
        var read = new ActivityTaskRead { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 201 },
            Pawns = [new("pawn", new("method/pawn", "method", 1, 0, "holding", "", null, 201))] };
        var release = Assert.IsType<PawnActivities>(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), read));
        Assert.True(Assert.Single(release.Orders).Release);
    }
}
