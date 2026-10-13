namespace RimGovernor.Brain.BuildingRuntime;

// Go: internal/bridge/trade_target.go (TradeParticipantOf). The Bridge port has no counterpart yet, so the
// building runtime carries it next to its only callers.
internal static partial class BuildingRuntimePackage
{
    /// <summary>The closed participant a wire trade target names; an unset target reads as the empty map participant.</summary>
    internal static TradeParticipant TradeParticipantOf(C.TradeTarget? target)
    {
        switch (target?.KindCase)
        {
            case C.TradeTarget.KindOneofCase.MapTrader:
                return new TradeParticipant(TradeParticipantKind.Map, target.MapTrader.TraderId);
            case C.TradeTarget.KindOneofCase.OrbitalShip:
                return new TradeParticipant(TradeParticipantKind.Orbital, target.OrbitalShip.ShipId);
            case C.TradeTarget.KindOneofCase.Settlement:
                return new TradeParticipant(TradeParticipantKind.Settlement, target.Settlement.SettlementId, target.Settlement.CaravanId);
            default:
                return new TradeParticipant(TradeParticipantKind.Map, "");
        }
    }
}
