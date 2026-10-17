using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using Domain = RimGovernor.Brain.Domain;
using PbDefs = RimGovernor.Contracts.Wire.Defs;

namespace RimGovernor.Brain.Host;

// Go trade_acquisition.go, trade_session.go, trade_sheet.go and traders.go.
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>Client.ReadTradeAcquisition</c>: the comms-console, passing-ship and trade-request census. Optional native
    /// facts stay absent: an absent cost, relation, eligibility or sampled timing is never a zero or permission to
    /// dispatch. <paramref name="pack"/> is the existing formation intent, used solely as a calculation input.
    /// </summary>
    internal async Task<PbObservations.TradeAcquisition> ReadTradeAcquisitionAsync(PbCommon.Identity identity, PbOperations.FormCaravanIntent? pack, CancellationToken cancellationToken)
    {
        var request = new PbObservations.TradeAcquisitionRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Pack = pack };
        var reply = await Facade.ReadTradeAcquisitionAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return outcome.Observed!;
    }

    /// <summary>
    /// Go <c>Client.ReadTradeSession</c>: which negotiator is walking to or trading with which trader. The read issues no
    /// order: a walk the game ended simply reads as absent.
    /// </summary>
    internal async Task<TradeSessionRead> ReadTradeSessionAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.TradeSessionRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var reply = await Facade.ReadTradeSessionAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        var session = outcome.Observed!;
        var trader = BuildingRuntimePackage.TradeParticipantOf(session.Target).Key();
        return new TradeSessionRead { Target = session.Target, Context = session.Context, Trader = trader, Negotiator = session.NegotiatorId ?? "", Open = session.Open ?? false };
    }

    /// <summary>
    /// Go <c>Client.ReadTradeSheet</c>: the whole trade sheet of the live session (always with untradeable rows and no name
    /// filter).
    /// </summary>
    internal async Task<TradeSheetRead> ReadTradeSheetAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.TradeSheetRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
        };
        var reply = await Facade.ReadTradeSheetAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        var sheet = TradeSheetPage(outcome.Observed!);
        cancellationToken.ThrowIfCancellationRequested();
        return sheet;
    }

    /// <summary>Go <c>tradeSheetPage</c>: decodes the sheet's header and rows.</summary>
    private static TradeSheetRead TradeSheetPage(PbObservations.TradeSheet v)
    {
        return new TradeSheetRead
        {
            Context = v.Snapshot?.Context,
            SessionId = v.SessionId ?? "",
            Target = v.Target,
            Trader = BuildingRuntimePackage.TradeParticipantOf(v.Target).Key(),
            Negotiator = v.Negotiator?.Id ?? "",
            GiftMode = v.GiftMode ?? false,
            CanTradeNow = v.CanTradeNow ?? false,
            Balance = v.Balance ?? 0,
            BalanceKnown = v.Balance is not null,
            ColonyCanAfford = v.ColonyCanAfford ?? false,
            TraderHasSilver = v.TraderHasEnoughSilver ?? false,
            DealSignature = v.DealSignature ?? "",
            FavorCurrency = v.CurrencyKind == PbDefs.TradeCurrency.Favor,
            Rows = [.. v.Lines.Select(TradeSheetRowOf)],
        };
    }

    /// <summary>Go <c>tradeSheetRow</c>: one trade sheet line.</summary>
    private static TradeSheetRow TradeSheetRowOf(PbObservations.TradeLine v)
    {
        return new TradeSheetRow
        {
            Pawn = v.Pawn,
            ThingId = v.ThingId ?? "",
            Quality = v.Quality ?? 0,
            QualityKnown = v.Quality is not null,
            HitPoints = v.HitPointsFraction ?? 0,
            HitPointsKnown = v.HitPointsFraction is not null,
            ZoneId = v.ZoneId ?? "",
            LineId = v.LineId ?? "",
            DefName = v.Definition?.DefName ?? "",
            Stuff = v.Stuff ?? "",
            ColonyCount = v.ColonyCount ?? 0,
            TraderCount = v.TraderCount ?? 0,
            BuyPrice = v.BuyPrice ?? 0,
            SellPrice = v.SellPrice ?? 0,
            MarketValue = v.MarketValue ?? 0,
            TransferNow = v.TransferCount ?? 0,
            MinimumCount = v.MinimumCount ?? 0,
            MaximumCount = v.MaximumCount ?? 0,
            BuyPriceKnown = v.BuyPrice is not null,
            SellPriceKnown = v.SellPrice is not null,
            TraderWillTrade = v.TraderWillTrade ?? false,
            TraderWillTradeKnown = v.TraderWillTrade is not null,
            Currency = v.Currency ?? false,
            CurrencyKnown = v.Currency is not null,
            // The favor currency row has no ThingDef, so its definition is empty.
            Favor = v.Favor == true && v.Currency == true,
        };
    }

    /// <summary>
    /// Go <c>Client.ListTraders</c>: the map's trader caravans and eligible negotiators, one complete unfiltered census.
    /// </summary>
    internal async Task<TradersRead> ListTradersAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.TradersRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var reply = await Facade.ListTradersAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        var decoded = TradeDecodeTraders(outcome.Observed!);
        cancellationToken.ThrowIfCancellationRequested();
        return decoded;
    }

    /// <summary>Go <c>decodeTraders</c>: decodes a complete trader census.</summary>
    internal static TradersRead TradeDecodeTraders(PbObservations.TradersSnapshot snapshot)
    {
        var traders = new List<TraderRead>();
        var negotiators = new List<NegotiatorRead>();
        foreach (var row in snapshot.Traders)
        {
            var id = row.Entity?.Id ?? "";
            var cell = row.Entity?.Position;
            var participant = new Domain.TradeParticipant(row.Orbital == true ? Domain.TradeParticipantKind.Orbital : Domain.TradeParticipantKind.Map, id);
            traders.Add(new TraderRead
            {
                Participant = participant,
                Id = participant.Key(),
                Token = row.TraderSnapshot?.Token ?? "",
                Kind = row.Kind ?? "",
                Faction = row.Faction?.Id ?? "",
                CanTrade = row.CanTrade.GetValueOrDefault(),
                Travelling = row.Travelling.GetValueOrDefault(),
                Reason = row.Reason ?? "",
                GoodsStacks = row.GoodsStacks ?? 0,
                X = cell?.X ?? 0,
                Z = cell?.Z ?? 0,
            });
        }

        foreach (var row in snapshot.Negotiators)
        {
            negotiators.Add(new NegotiatorRead(row.Id ?? "", row.Id ?? ""));
        }

        return new TradersRead(snapshot.Context, traders, negotiators);
    }
}
