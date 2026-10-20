using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3599: the speed matrix is a throughput run; its outcome check only proves a governed row's colonists completed
/// brain-selected work, so a row cannot look fast because everyone idled.
/// </summary>
public sealed class SpeedmatrixOutcomeTests
{
    private static TimelineRecord Execution(string pawn, string assignment, double revision, string state) => new()
    {
        Kind = PawnActivityTelemetry.ExecutionKind,
        Payload = new Dictionary<string, object?>
        {
            [PawnActivityTelemetry.PawnKey] = pawn,
            [PawnActivityTelemetry.AssignmentKey] = assignment,
            [PawnActivityTelemetry.RevisionKey] = revision,
            [PawnActivityTelemetry.StateKey] = state,
        },
    };

    private static SpeedOutcome Row(string name, int completed) =>
        new() { Case = name, CompletedTasks = completed, HealthyColonists = 3 };

    [Fact]
    public void EachCompletedOrderCountsOnceWhateverItsMethod()
    {
        var timeline = new[]
        {
            Execution("pawn1", "coupled-a", 1, "installed"),
            Execution("pawn1", "coupled-a", 1, "started"),
            Execution("pawn1", "coupled-a", 1, PawnActivityTelemetry.CompletedState),

            // A repeated completion row of the same order is one task.
            Execution("pawn1", "coupled-a", 1, PawnActivityTelemetry.CompletedState),

            // The same assignment's next revision is another task, and so is another kind of Method.
            Execution("pawn1", "coupled-a", 2, PawnActivityTelemetry.CompletedState),
            Execution("pawn2", "service-b", 1, PawnActivityTelemetry.CompletedState),
        };

        Assert.Equal(3, SpeedMatrix.CompletedTasks(timeline));
    }

    [Fact]
    public void FailedReleasedAndStartedRowsAreNoCompletedTask()
    {
        var timeline = new[]
        {
            Execution("pawn1", "coupled-a", 1, "failed"),
            Execution("pawn2", "coupled-b", 1, "started"),
            Execution("pawn3", "coupled-c", 1, "released"),
            new TimelineRecord { Kind = PawnActivityTelemetry.IntervalKind, Payload = new Dictionary<string, object?> { [PawnActivityTelemetry.StateKey] = PawnActivityTelemetry.CompletedState } },
        };

        Assert.Equal(0, SpeedMatrix.CompletedTasks(timeline));
    }

    [Fact]
    public void ARowWhoseColonistsCompletedBrainWorkPasses()
    {
        Assert.Empty(SpeedMatrix.CheckOutcomes([Row("Normal", 1), Row("Hyperfast", 40)], tolerance: 1));
    }

    [Fact]
    public void ARowWhoseColonistsCompletedNoBrainWorkFails()
    {
        var problems = SpeedMatrix.CheckOutcomes([Row("Normal", 12), Row("Hyperfast", 0)], tolerance: 1);

        Assert.Equal(["Hyperfast: its colonists completed no brain-selected task"], problems);
    }
}
