namespace RimGovernor.Brain.GameAccess;

public interface IHaulProbeSource
{
    /// <summary>
    /// The relocation hauls' walks for <paramref name="expected"/>: the legs <c>ReconcileRules.UnprobedHauls</c> names, each
    /// answered by the game's pathfinder (observations_read_haul_probe). Throws when the native cannot read it, and
    /// <see cref="ObservationChangedException"/> when the reply answers another colony context.
    /// </summary>
    Task<RimGovernor.Brain.GameFacts.Observations.HaulProbeSnapshot> ReadHaulProbeAsync(ObservationIdentity expected, IReadOnlyList<RimGovernor.Brain.GameOrders.Observations.HaulProbeLeg> legs, CancellationToken cancellationToken);
}
