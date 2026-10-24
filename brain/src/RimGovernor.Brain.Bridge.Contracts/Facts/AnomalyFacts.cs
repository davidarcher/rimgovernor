using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// The lift of a pawn's Anomaly block into the squad facts' Fact-typed field (SquadDefense.Anomaly).
public static class AnomalyFactsRules
{
    /// <summary>A pawn row's Anomaly block; unknown when Anomaly is inactive.</summary>
    public static Fact<PawnAnomaly> PawnAnomaly(PawnAnomalyState state) =>
        state is PawnAnomalyState.Active { Facts: var anomaly } ? Known(anomaly) : Unknown<PawnAnomaly>();
}
