using StoreDefenseLayoutRecord = RimGovernor.Brain.Store.DefenseLayoutRecord;

namespace RimGovernor.Brain.Snapshot;

/// <summary>
/// One RoundsDefensePlanner step as recorded: the native replies the step read (the emergency census, the combat
/// pawn rows and, for a hostile building, the lines of fire), the layout record it held the line against, and what it
/// decided. A threat response is not a rounds, so the routine snapshot does not carry these inputs.
/// </summary>
public sealed class SnapshotDefense
{
    public string Recorded { get; set; } = "";

    public GenerationSnapshot Snapshot { get; set; }

    public Tick Tick { get; set; }

    /// <summary>The stored defensive layout the step read; null when the colony has none.</summary>
    public StoreDefenseLayoutRecord? Layout { get; set; }

    /// <summary>The direction toward home of the layout plan's killbox opening, which the stored layout no longer carries.</summary>
    public Rotation Toward { get; set; }

    public EmergencyFacts Emergency { get; set; } = new();

    /// <summary>
    /// ProtoJSON of the emergency read's ObservationContext, the ListPawnsReply of the combat read and the
    /// lines-of-fire read's context. CombatPawns is null when the step stopped before the combat read.
    /// </summary>
    public string? EmergencyContext { get; set; }

    public string? CombatPawns { get; set; }

    public string? LinesContext { get; set; }

    public IReadOnlyList<LineOfFire> Lines { get; set; } = [];

    /// <summary>The step's result.</summary>
    public string Reason { get; set; } = "";

    public Domain.PlanId Plan { get; set; }

    /// <summary>The method the admitted plan was committed under.</summary>
    public MethodId Method { get; set; }
}

public static partial class SnapshotPackage
{
    /// <summary>Writes d into dir as defense-&lt;tick&gt;-&lt;reason&gt;; a later step at the same tick with the same result replaces it.</summary>
    public static void RecordDefense(string dir, SnapshotDefense d) => Write(dir, "defense", $"defense-{d.Tick.Value}-{d.Reason}", d);
}
