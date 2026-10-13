namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Port of planner_snapshot.go: snapshot recording hooks for replay, active only when the snapshot directory is set.</summary>
internal static class PlannerSnapshot
{
    /// <summary>Records the store's tables into the serve's snapshot stream when recording is on.</summary>
    public static void RecordTables(FactStore store)
    {
        var dir = Environment.GetEnvironmentVariable(Snap.DirEnv);
        if (!string.IsNullOrEmpty(dir))
        {
            var record = Snap.MirrorRecorder(dir);
            store.SetRecorder(published => Snap.Later(() => record(published)));
        }
    }

    /// <summary>
    /// Appends a planner step's own colony read (<paramref name="planner"/> is "building", "bill", "hospital" or
    /// "deepdrill") to the serve's snapshot stream for replay when snapshot recording is on. A failed write is
    /// dropped rather than failing the step.
    /// </summary>
    public static void RecordStepRead(string planner, ConcernId goal, GenerationSnapshot current, ColonyProjection reading)
    {
        var dir = Environment.GetEnvironmentVariable(Snap.DirEnv);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        Snap.Later(() =>
        {
            try
            {
                Snap.RecordStep(dir, planner, goal, current, reading);
            }
            catch (Exception)
            {
                // recording is best effort
            }
        });
    }

    /// <summary>
    /// Starts recording one planner step's policy inputs for replay when snapshot recording is on; disposing the
    /// returned recorder is a no-op when recording is off, and <see cref="PlannerRecording.Finish"/> writes the
    /// inputs. A failed write is dropped rather than failing the step.
    /// </summary>
    public static PlannerRecording RecordPlannerStep(ConcernId goal, GenerationSnapshot current, Tick tick)
    {
        var recording = Snap.StartPlanner(goal);
        return new PlannerRecording(recording, current, tick);
    }
}

/// <summary>One planner step's recording (Go returned the derived context and a finish func).</summary>
internal sealed class PlannerRecording(IDisposable recording, GenerationSnapshot current, Tick tick) : IDisposable
{
    public void Finish()
    {
        try
        {
            Snap.Finish(recording, current, tick);
        }
        catch (Exception)
        {
            // recording is best effort
        }
    }

    public void Dispose() => recording.Dispose();
}
