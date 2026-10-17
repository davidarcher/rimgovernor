namespace RimGovernor.Brain.Policy;


public static class ClimateEnvelopeRules
{
    /// <summary>
    /// Keeps the vents and roofs of a room's ready operations: the plan-wide climate envelope (#2962, the
    /// <c>layout/climate-envelope</c> Need) owes the room nothing else. An owner builds a room's vents and roofs only
    /// while its own mismatch check holds (ReconcileRoom), so a room whose owner has stopped reconciling would never
    /// get them; the envelope asks the plan itself: a vent over a standing ring wall (OpVentIn) or a roofed-set cell
    /// still open to the sky once the ring stands (OpRoofIn). A vent is absent while its def is unavailable, on rock
    /// or already standing, and a roof is never owed an outdoor or unfenced room.
    /// </summary>
    public static List<Operation> EnvelopeOps(IEnumerable<Operation> ops) =>
        [.. ops.Where(op => op.Kind == OpKind.VentIn || op.Kind == OpKind.RoofIn)];
}
