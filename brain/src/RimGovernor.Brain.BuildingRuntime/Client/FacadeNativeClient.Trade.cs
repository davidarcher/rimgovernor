namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// The comms-console, passing-ship and trade-request census. <paramref name="pack"/> is the existing formation
    /// intent, used solely as a calculation input.
    /// </summary>
    internal async Task<PbObservations.TradeAcquisition> ReadTradeAcquisitionAsync(PbCommon.Identity identity, PbOperations.FormCaravanIntent? pack, CancellationToken cancellationToken)
    {
        var request = new PbObservations.TradeAcquisitionRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Pack = pack };
        var read = await Facade.ReadTradeAcquisitionAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return read;
    }

    /// <summary>
    /// Which negotiator is walking to or trading with which trader. The read issues no
    /// order: a walk the game ended simply reads as idle.
    /// </summary>
    internal async Task<PbObservations.TradeSession> ReadTradeSessionAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.TradeSessionRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        return await Facade.ReadTradeSessionAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The whole trade sheet of the live session, every line with no filter.</summary>
    internal async Task<PbObservations.TradeSheet> ReadTradeSheetAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.TradeSheetRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var sheet = await Facade.ReadTradeSheetAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return sheet;
    }

    /// <summary>The map's traders and eligible negotiators, one complete unfiltered census.</summary>
    internal async Task<PbObservations.TradersSnapshot> ListTradersAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.TradersRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var census = await Facade.ListTradersAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return census;
    }
}
