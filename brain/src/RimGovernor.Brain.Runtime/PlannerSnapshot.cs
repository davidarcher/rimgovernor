namespace RimGovernor.Brain.Runtime;

/// <summary>Snapshot recording hooks for replay, active only when the snapshot directory is set.</summary>
internal static class PlannerSnapshot
{
    /// <summary>Records the store's tables into the serve's snapshot stream when recording is on.</summary>
    public static void RecordTables(FactStore store)
    {
        var dir = Environment.GetEnvironmentVariable(SnapshotRecorder.DirEnv);
        if (!string.IsNullOrEmpty(dir))
        {
            var record = SnapshotLaterReplay.MirrorRecorder(dir);
            store.SetRecorder(published => SnapshotLaterReplay.Later(() => record(published), "section", new Tick(published.AsOf.Tick)));
        }
    }

    /// <summary>
    /// Appends a planner step's own colony read (<paramref name="planner"/> is "building", "bill", "hospital" or
    /// "deepdrill") to the serve's snapshot stream for replay when snapshot recording is on. A failed write is
    /// dropped rather than failing the step.
    /// </summary>
    public static void RecordStepRead(string planner, FilingKey goal, GenerationSnapshot current, ColonyReading reading)
    {
        var dir = Environment.GetEnvironmentVariable(SnapshotRecorder.DirEnv);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        SnapshotLaterReplay.Later(() => SnapshotStepReplay.RecordStep(dir, planner, goal, current, reading), "step", reading.Identity.Tick);
    }

    /// <summary>
    /// Starts recording one planner step's policy inputs for replay when snapshot recording is on; disposing the
    /// returned recorder is a no-op when recording is off, and <see cref="PlannerRecording.Finish"/> writes the
    /// inputs. A failed write is dropped rather than failing the step.
    /// </summary>
    public static PlannerRecording RecordPlannerStep(FilingKey goal, GenerationSnapshot current, Tick tick)
    {
        var recording = SnapshotPlannerReplay.StartPlanner(goal);
        return new PlannerRecording(recording, current, tick);
    }
}

/// <summary>One planner step's recording.</summary>
internal sealed class PlannerRecording(IDisposable recording, GenerationSnapshot current, Tick tick) : IDisposable
{
    public void Finish()
    {
        try
        {
            SnapshotPlannerReplay.Finish(recording, current, tick);
        }
        catch (Exception ex)
        {
            Tel.Decide(SnapshotSkip.SnapshotSkipDecision("recording", "planner", ex, tick));
        }
    }

    public void Dispose() => recording.Dispose();
}
