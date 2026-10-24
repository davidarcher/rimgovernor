using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Cell = RimGovernor.Brain.Domain.Cell;
using O = RimGovernor.Contracts.Wire.Observations;
using static RimGovernor.Brain.Domain.Fact;
namespace RimGovernor.Brain.Executor;

/// <summary>
/// One planner step's own colony read: the projection a building or bill planner decided from, which carries what
/// the review's read lacks (rooms, the step's definitions, fresh benches). A test replays the planner's selector
/// over Projection.
/// </summary>
public sealed class SnapshotStep
{
    public string Recorded { get; set; } = "";

    public GenerationSnapshot Snapshot { get; set; }

    public Tick Tick { get; set; }

    public ConcernId Concern { get; set; }

    /// <summary>"building", "bill", "hospital" or "deepdrill".</summary>
    public string Planner { get; set; } = "";

    /// <summary>The step's reading with its Facts; its Zones and Window are not recorded, as in the rounds snapshot.</summary>
    public required ColonyProjection Projection { get; set; }
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
    public static void RecordStep(string dir, string planner, ConcernId goal, GenerationSnapshot current, ColonyProjection reading)
    {
        reading = reading with { Zones = null, Window = null };
        var tick = reading.Identity.Tick;
        var step = new SnapshotStep
        {
            Recorded = SnapshotRecorder.Provenance(current, tick) + " standard " + goal.Value,
            Snapshot = current,
            Tick = tick,
            Concern = goal,
            Planner = planner,
            Projection = reading,
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
