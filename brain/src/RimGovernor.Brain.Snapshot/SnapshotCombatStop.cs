namespace RimGovernor.Brain.Snapshot;

/// <summary>
/// One fight stop as recorded. A combat recording is a fight in the serve's stream: the snapshot frames the fight
/// decided from, and one line per stop that wrote to the journal. The frame carries the stop's inputs; a stop holds
/// none, so the replay rebuilds the view from the frame before it, which must be at the stop's tick.
/// </summary>
public sealed class SnapshotCombatStop
{
    public Domain.PlanId Plan { get; set; }

    public Tick Tick { get; set; }

    public StopEvent Stop { get; set; } = new();

    /// <summary>The fight's owned drafts at the stop, from the plan's progress; journal state no frame carries.</summary>
    public IReadOnlyList<Domain.PawnId> Orderable { get; set; } = [];

    /// <summary>The stored defense layout the view held, when one was.</summary>
    public CombatLayout? Layout { get; set; }

    /// <summary>The stop's one geometry round trip, when it had one.</summary>
    public GeometryRequest? Ask { get; set; }

    public GeometryReply? Reply { get; set; }

    public CombatMemory MemoryIn { get; set; } = new();

    public CombatMemory MemoryOut { get; set; } = new();

    /// <summary>Set when the stop sent orders: the plan's evidence record at Tick holds them and their per-pawn results.</summary>
    public bool Evidence { get; set; }
}

public static partial class SnapshotPackage
{
    /// <summary>Caps a committed combat recording's stops.</summary>
    public const int CombatRecordingMaxStops = 40;

    /// <summary>Caps a committed combat recording's gzipped size.</summary>
    public const int CombatRecordingMaxBytes = 300 << 10;

    /// <summary>
    /// Appends one fight stop to this process's stream in dir, after the frame it decided from when that frame is
    /// not the last one recorded. The sink owns the stream and the frame/event de-duplication.
    /// </summary>
    public static void RecordCombatStop(string dir, O.BundleSnapshot? frame, SnapshotCombatStop s)
    {
        if (frame is null)
        {
            throw new DomainException($"combat stop at {s.Tick.Value} without a frame");
        }
        Write(dir, "combat", $"combat-{s.Tick.Value}", new RecordedStop(s, frame));
    }
}

/// <summary>A recorded fight stop with the frame it decided from.</summary>
public sealed record RecordedStop(SnapshotCombatStop Stop, O.BundleSnapshot Frame);
