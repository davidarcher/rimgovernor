using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;
using Cell = RimGovernor.Brain.Domain.Cell;
using StoreDefenseLayoutRecord = RimGovernor.Brain.Store.DefenseLayoutRecord;

namespace RimGovernor.Brain.Executor;

/// <summary>
/// One DefensePlanner step as recorded: the native replies the step read (the emergency census, the combat
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

    /// <summary>The layout combat fights from, read from the layout plan (its killbox opening and its DefFiring and DefLane nodes), which the stored layout does not carry; null without an opening.</summary>
    public CombatLayout? Combat { get; set; }

    public EmergencyFacts Emergency { get; set; } = new();

    /// <summary>
    /// ProtoJSON of the emergency read's ObservationContext, the PawnSnapshot of the combat read and the
    /// lines-of-fire read's context. CombatPawns is null when the step stopped before the combat read.
    /// </summary>
    public string? EmergencyContext { get; set; }

    public string? CombatPawns { get; set; }

    public string? LinesContext { get; set; }

    public IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.LineOfFire> Lines { get; set; } = [];

    /// <summary>The step's result.</summary>
    public string Reason { get; set; } = "";

    public Domain.PlanId Plan { get; set; }

    /// <summary>The method the admitted plan was committed under.</summary>
    public MethodId Method { get; set; }
}

public static class SnapshotDefenseReplay
{
    /// <summary>Writes d into dir as defense-&lt;tick&gt;-&lt;reason&gt;; a later step at the same tick with the same result replaces it.</summary>
    public static void RecordDefense(string dir, SnapshotDefense d) => SnapshotRecorder.Write(dir, "defense", $"defense-{d.Tick.Value}-{d.Reason}", d);
}
