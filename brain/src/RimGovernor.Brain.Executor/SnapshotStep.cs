using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Cell = RimGovernor.Brain.Domain.Cell;
using O = RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Executor;

/// <summary>
/// One planner step's own colony read: the projection a building or bill planner decided from, which carries what
/// the review's read lacks (rooms, the step's definitions, fresh benches). A test replays the planner's selector
/// over Reading.
/// </summary>
public sealed class SnapshotStep
{
    public string Recorded { get; set; } = "";

    public GenerationSnapshot Snapshot { get; set; }

    public Tick Tick { get; set; }

    public ConcernId Concern { get; set; }

    /// <summary>"building", "bill", "hospital" or "deepdrill".</summary>
    public string Planner { get; set; } = "";

    /// <summary>The step's canonical reading, including the observed geometry used by site searches.</summary>
    public required ColonyReading Reading { get; set; }
}

/// <summary>Names one step read in a stream: step-&lt;planner&gt;-&lt;goal&gt;-&lt;tick&gt;-&lt;seq&gt;.</summary>
public readonly record struct StepRead(string Planner, ConcernId Concern, Tick Tick, int Seq)
{
    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"step-{Planner}-{Concern.Value}-{Tick.Value}-{Seq}");
}

public static class SnapshotStepReplay
{
    private static readonly object StepLock = new();
    private static readonly Dictionary<string, int> StepSeq = new(StringComparer.Ordinal);

    /// <summary>Appends goal's step read to this process's stream in dir, numbered from 1 per planner, goal and tick.</summary>
    public static void RecordStep(string dir, string planner, ConcernId goal, GenerationSnapshot current, ColonyReading reading)
    {
        var tick = reading.Identity.Tick;
        var step = new SnapshotStep
        {
            Recorded = SnapshotRecorder.Provenance(current, tick) + " standard " + goal.Value,
            Snapshot = current,
            Tick = tick,
            Concern = goal,
            Planner = planner,
            Reading = reading,
        };
        int seq;
        lock (StepLock)
        {
            var key = new StepRead(planner, goal, tick, 0).ToString();
            seq = StepSeq.GetValueOrDefault(key) + 1;
            StepSeq[key] = seq;
        }
        SnapshotRecorder.Write(dir, "step", new StepRead(planner, goal, tick, seq).ToString(), step);
    }
}
