namespace RimGovernor.Brain.Runtime;

public sealed partial class GameClient
{
    /// <summary>
    /// The comms-console, passing-ship and trade-request census. <paramref name="pack"/> is the existing formation
    /// intent, used solely as a calculation input.
    /// </summary>
    internal async Task<RimGovernor.Brain.GameFacts.Observations.TradeAcquisition> ReadTradeAcquisitionAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent? pack, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.TradeAcquisitionRequest { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity }, Pack = pack };
        var read = await Facade.ReadTradeAcquisitionAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return read;
    }

    /// <summary>
    /// Which negotiator is walking to or trading with which trader. The read issues no
    /// order: a walk the game ended simply reads as idle.
    /// </summary>
    internal async Task<RimGovernor.Brain.GameFacts.Observations.TradeSession> ReadTradeSessionAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.TradeSessionRequest { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity } };
        return await Facade.ReadTradeSessionAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The whole trade sheet of the live session, every line with no filter.</summary>
    internal async Task<RimGovernor.Brain.GameFacts.Observations.TradeSheet> ReadTradeSheetAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.TradeSheetRequest { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity } };
        var sheet = await Facade.ReadTradeSheetAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return sheet;
    }

    /// <summary>The map's traders and eligible negotiators, one complete unfiltered census.</summary>
    internal async Task<RimGovernor.Brain.GameFacts.Observations.TradersSnapshot> ListTradersAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.TradersRequest { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity } };
        var census = await Facade.ListTradersAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return census;
    }
}
