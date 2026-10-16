namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsTradePlanner
{
    /// <summary>
    /// Holds a forecast trade request the supply plan proposed, as the economy/resource Need's work (#3316). No comms
    /// request is ever committed under the supply Needs: the forecast cannot yet weigh comms negotiation against its
    /// participants, travel and service alternatives, so a non-settlement proposal is held while the resource Need
    /// has no open work. A native arrival later appears in the ordinary trader census and is browsed by the
    /// economy/caravan Need. Null when there is nothing to hold.
    /// </summary>
    internal Verdict? RequestHold(NeedWork work, CancellationToken call)
    {
        var proposal = Reviewer.TradeAcquisitionPlan("resources").Proposal;
        if (proposal is null || proposal.Option.Kind == Pol.TradeAcquisitionKind.Settlement || Reviewer.ResourceTargets(work.State.Snapshot).Count == 0)
        {
            return null;
        }
        if (OpenWork(work.Owner, call))
        {
            return null;
        }
        return AwaitingPlan("unsupported_forecast_trade_request",
            "Comms negotiation requires shared participant, travel and service alternatives.");
    }
}
