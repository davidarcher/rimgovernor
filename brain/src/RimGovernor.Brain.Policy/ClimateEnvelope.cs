namespace RimGovernor.Brain.Policy;

// Go: climate_envelope.go

public static partial class PolicyFunctions
{
    /// <summary>
    /// The Shelter department's plan-wide climate envelope (#2962): every planned room's missing vents and
    /// roofs, whichever owner (or none) reconciles the room. An owner builds a room's vents and roofs only
    /// while its own mismatch check holds (ReconcileRoom), so a room whose owner has stopped reconciling would
    /// never get them; this Standard asks the plan itself. It is a Standard whose target is nothing owed: a
    /// vent over a standing ring wall (OpVentIn) or a roofed-set cell still open to the sky once the ring
    /// stands (OpRoofIn). The build rides the reconcile library's two operations, so a vent is absent while
    /// its def is unavailable, on rock or already standing, and a roof is never owed an outdoor or unfenced
    /// room.
    /// </summary>
    public static readonly ConcernId MaintainClimateEnvelope = new("MaintainClimateEnvelope");

    /// <summary>Ranks MaintainClimateEnvelope with the other priority-3 chores.</summary>
    internal const int ClimateEnvelopePriority = 3;

    /// <summary>Raises MaintainClimateEnvelope while a planned vent or roof is owed; unknown raises nothing.</summary>
    internal static void InspectClimateEnvelope(RoundsRun c) => c.Owed(MaintainClimateEnvelope, ClimateEnvelopePriority, c.F.EnvelopeOwed);

    /// <summary>Keeps the vents and roofs of a room's ready operations: the plan-wide envelope owes the room nothing else.</summary>
    public static List<Operation> EnvelopeOps(IEnumerable<Operation> ops) =>
        [.. ops.Where(op => op.Kind == OpKind.VentIn || op.Kind == OpKind.RoofIn)];
}
