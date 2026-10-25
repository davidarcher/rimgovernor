namespace RimGovernor.Brain.Runtime;

// TradeParticipantOf. The Bridge has no counterpart yet, so the
// building runtime carries it next to its only callers.
internal static class TradeTarget
{
    /// <summary>The closed participant a wire trade target names; an unset target reads as the empty map participant.</summary>
    internal static TradeParticipant TradeParticipantOf(RimGovernor.Brain.GameFacts.Common.TradeTarget? target)
    {
        switch (target?.Kind)
        {
            case RimGovernor.Brain.GameFacts.Common.TradeTargetKind.MapTraderCase { Value: var map }:
                return new TradeParticipant(TradeParticipantKind.Map, map.TraderId ?? "");
            case RimGovernor.Brain.GameFacts.Common.TradeTargetKind.OrbitalShipCase { Value: var ship }:
                return new TradeParticipant(TradeParticipantKind.Orbital, ship.ShipId ?? "");
            case RimGovernor.Brain.GameFacts.Common.TradeTargetKind.SettlementCase { Value: var settlement }:
                return new TradeParticipant(TradeParticipantKind.Settlement, settlement.SettlementId ?? "", settlement.CaravanId ?? "");
            case null:
                throw new DomainException("trade participant unavailable");
        }
        throw new DomainException("unsupported trade participant");
    }

    /// <summary>The trader key of the session's engagement; empty while the session is idle.</summary>
    internal static string TraderOf(RimGovernor.Brain.GameFacts.Observations.TradeSession session) =>
        session.Engagement is RimGovernor.Brain.GameFacts.Observations.TradeEngagement.Engaged engaged ? TradeParticipantOf(engaged.Target).Key() : "";
}
