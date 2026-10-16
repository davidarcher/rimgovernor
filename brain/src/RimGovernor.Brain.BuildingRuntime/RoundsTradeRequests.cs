namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsTradePlanner
{
    /// <summary>
    /// Holds a forecast trade request the supply plan proposed. The supply owners are Needs (food/supply since #3254,
    /// economy/resource since #3303) and no comms request is ever committed under them: the forecast cannot yet
    /// weigh comms negotiation against its participants, travel and service alternatives, so a non-settlement
    /// proposal is held while the resource Need has no open work. A native arrival later appears in the ordinary
    /// trader census and is browsed by the shared trade phases.
    /// </summary>
    internal Task<(RoundsTradeResult Result, bool Handled)> RequestAsync(ControlState state, CancellationToken call)
    {
        var proposal = Reviewer.TradeAcquisitionPlan("resources").Proposal;
        if (proposal is null || proposal.Option.Kind == Pol.TradeAcquisitionKind.Settlement || Reviewer.ResourceTargets(state.Snapshot).Count == 0)
        {
            return Task.FromResult((new RoundsTradeResult(), false));
        }
        var journal = Reviewer.Player.Journal;
        foreach (var method in journal.LoadNeed(EconomyFamily.Resource, state.Snapshot, call).Methods)
        {
            if (StorePackage.PlanOpen(journal.LoadPlan(method.Plan, call)))
            {
                return Task.FromResult((new RoundsTradeResult(), false));
            }
        }
        return Task.FromResult((new RoundsTradeResult { Verdict = AwaitingPlan("unsupported_forecast_trade_request",
            "Comms negotiation requires shared participant, travel and service alternatives.") }, true));
    }
}
