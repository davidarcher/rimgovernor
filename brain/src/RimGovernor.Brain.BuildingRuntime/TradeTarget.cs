namespace RimGovernor.Brain.BuildingRuntime;

// Go: internal/bridge/trade_target.go (TradeParticipantOf). The Bridge port has no counterpart yet, so the
// building runtime carries it next to its only callers.
internal static partial class BuildingRuntimePackage
{
    /// <summary>The closed participant a wire trade target names; an unset target reads as the empty map participant.</summary>
    internal static TradeParticipant TradeParticipantOf(C.TradeTarget? target)
    {
        switch (target?.Kind)
        {
            case C.TradeTargetKind.MapTraderCase { Value: var map }:
                return new TradeParticipant(TradeParticipantKind.Map, map.TraderId ?? "");
            case C.TradeTargetKind.OrbitalShipCase { Value: var ship }:
                return new TradeParticipant(TradeParticipantKind.Orbital, ship.ShipId ?? "");
            case C.TradeTargetKind.SettlementCase { Value: var settlement }:
                return new TradeParticipant(TradeParticipantKind.Settlement, settlement.SettlementId ?? "", settlement.CaravanId ?? "");
            default:
                return new TradeParticipant(TradeParticipantKind.Map, "");
        }
    }
}
