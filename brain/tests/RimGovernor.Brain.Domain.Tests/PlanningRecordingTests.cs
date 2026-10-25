using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Runtime.Recordings;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlanningRecordings
{
    public const string Name = "Planning recording sink";
}

[Collection(PlanningRecordings.Name)]
public sealed class PlanningRecordingTests
{
    [Fact]
    public void DisabledReviewDoesNotInventReplayInputs()
    {
        Assert.Null(SnapshotReviewReplay.FromReview(default, new Tick(12), new(), ColonyFactsCoreTests.Reading()));
    }

    [Fact]
    public void PartialDefenseAndStepRecordingsReachTheSameSinkUnchanged()
    {
        var previous = SnapshotRecorder.Sink;
        var sink = new RecordingSink();
        SnapshotRecorder.Sink = sink;
        try
        {
            var defense = new SnapshotDefense { Tick = new(17), Reason = "before_combat" };
            SnapshotDefenseReplay.RecordDefense("recordings", defense);
            var reading = ColonyFactsCoreTests.Reading();
            SnapshotStepReplay.RecordStep("recordings", "building", new FilingKey("test.recording"), default, reading);

            var savedDefense = Assert.IsType<SnapshotDefense>(sink.Rows[0].Value);
            Assert.Same(defense, savedDefense);
            Assert.Null(savedDefense.CombatPawns);
            Assert.Null(savedDefense.Layout);
            Assert.Equal(("recordings", "defense", "defense-17-before_combat"), sink.Rows[0].Identity);
            var step = Assert.IsType<SnapshotStep>(sink.Rows[1].Value);
            Assert.Same(reading, step.Reading);
            Assert.Equal(reading.Identity.Tick, step.Tick);
        }
        finally
        {
            SnapshotRecorder.Sink = previous;
        }
    }

    [Fact]
    public void NestedPlannerScopesKeepPartialInputsInTheirOwnRecording()
    {
        var previousSink = SnapshotRecorder.Sink;
        var previousDirectory = Environment.GetEnvironmentVariable(SnapshotRecorder.DirEnv);
        var sink = new RecordingSink();
        SnapshotRecorder.Sink = sink;
        Environment.SetEnvironmentVariable(SnapshotRecorder.DirEnv, "recordings");
        try
        {
            var outerRequest = new WorkshopRequest();
            var innerRequest = new ResourceMethodRequest();
            using var outer = SnapshotPlannerReplay.StartPlanner(new FilingKey("test.outer"));
            SnapshotPlannerReplay.NoteWorkshop(outerRequest);
            using (var inner = SnapshotPlannerReplay.StartPlanner(new FilingKey("test.inner")))
            {
                SnapshotPlannerReplay.NoteResourceMethod(innerRequest);
                SnapshotPlannerReplay.Finish(inner, default, new Tick(23));
            }
            SnapshotPlannerReplay.NoteWorkshop(outerRequest);
            SnapshotPlannerReplay.Finish(outer, default, new Tick(23));

            var innerSnapshot = Assert.IsType<SnapshotPlanner>(sink.Rows[0].Value);
            Assert.Same(innerRequest, Assert.Single(innerSnapshot.ResourceMethods));
            Assert.Empty(innerSnapshot.Workshops);
            var outerSnapshot = Assert.IsType<SnapshotPlanner>(sink.Rows[1].Value);
            Assert.Equal(2, outerSnapshot.Workshops.Count);
            Assert.All(outerSnapshot.Workshops, request => Assert.Same(outerRequest, request));
            Assert.Empty(outerSnapshot.ResourceMethods);
        }
        finally
        {
            SnapshotRecorder.Sink = previousSink;
            Environment.SetEnvironmentVariable(SnapshotRecorder.DirEnv, previousDirectory);
        }
    }

    [Fact]
    public void FailedBackgroundWriteDoesNotLoseFollowingRecordsOrPreventRestart()
    {
        var written = new List<int>();
        SnapshotLaterReplay.Later(() => written.Add(1), "step", new Tick(31));
        SnapshotLaterReplay.Later(() => throw new IOException("recording write failed"), "step", new Tick(31));
        SnapshotLaterReplay.Later(() => written.Add(2), "step", new Tick(31));
        SnapshotLaterReplay.Flush();
        Assert.Equal([1, 2], written);

        SnapshotLaterReplay.Later(() => written.Add(3), "step", new Tick(32));
        SnapshotLaterReplay.Flush();
        Assert.Equal([1, 2, 3], written);
    }

    [Fact]
    public void CombatRecordingRequiresItsObservedFrame()
    {
        Assert.Throws<DomainException>(() =>
            SnapshotCombatStopReplay.RecordCombatStop("recordings", null, new SnapshotCombatStop { Tick = new(41) }));
    }

    private sealed class RecordingSink : ISnapshotSink
    {
        internal List<((string Directory, string Kind, string Name) Identity, object Value)> Rows { get; } = [];
        public void Record(string directory, string kind, string name, object value) =>
            Rows.Add(((directory, kind, name), value));
    }
}
