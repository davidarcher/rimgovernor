using Receipts = RimGovernor.Contracts.Wire.Receipts;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>trade/execute</c>: a whole native trade. The colony holds home-area silver and steel; the negotiator beside a
/// staged outlander bulk caravan's trader opens a session, stages one buy line and one sell line and accepts the signed
/// deal. The accept executed and closed the session with colony silver moved, and a reopened session shows the
/// trader's and the colony's stock moved by the staged counts. A buy the colony cannot afford is refused and a cancel
/// ends the session.
/// </summary>
internal static class TradeExecute
{
    internal static TypedCase Case() => new()
    {
        Name = "trade/execute",
        Scope = "Native trade execution: open beside a staged caravan trader, set one buy and one sell line, accept the signed deal, "
            + "then stock and silver moved and the session closed; an unaffordable deal's accept is refused.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Site = new Fx.StageSite { Width = 7, Height = 7, Near = Fx.StageAnchor.Colonist, OffsetX = 2, Radius = 12, Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged] },

            // A caravan buys only home-area or stored items.
            Cells = [new() { Rect = new(0, 0, 7, 7), Home = true }],
            Things =
            [
                new() { Name = "silver", Def = "Silver", Count = 400, Cell = new(1, 1), Near = true },
                new() { Name = "steel", Def = "Steel", Count = 150, Cell = new(4, 4), Near = true },
            ],

            // Outlander bulk traders buy steel.
            Incidents = [new() { Name = "trader", Def = "TraderCaravanArrival", TraderKind = "Caravan_Outlander_BulkGoods" }],
        },
        Ops = [TradeKit.Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var incident = s.Staged!.Incidents["trader"];
        var traderId = incident.Traders.Count > 0 ? incident.Traders[0] : throw new AcceptanceException($"stage: no trader pawn was spawned: {incident}");
        var crew = await s.FixtureAsync("teleport-adjacent", TradeKit.Teleport, traderId, cancellationToken).ConfigureAwait(false);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        Task<Operations.ActionResult> Apply(string label, Operations.TradeIntentStep step) => TradeKit.ApplyAsync(s, "trade-execute-" + label, new Operations.TradeIntent
        {
            Target = new Common.TradeTarget { Kind = new Common.MapTradeTarget { TraderId = traderId } },
            NegotiatorId = crew.Negotiator,
            Step = step,
        }, cancellationToken);

        async Task<string> Open(string label)
        {
            var trade = TradeKit.AppliedTrade(label, await Apply(label, new Operations.OpenTrade { GiftMode = false }).ConfigureAwait(false));
            return trade is Receipts.TradeEffect.OpenCase { SessionId: { Length: > 0 } id } ? id : throw new AcceptanceException($"{label}: expected a session id, got {trade}");
        }

        async Task SetLines(string label, params Operations.TradeLine[] lines)
        {
            var effect = TradeKit.AppliedTrade(label, await Apply(label, new Operations.SetTradeLines { Lines = [.. lines] }).ConfigureAwait(false));
            if (effect is not Receipts.TradeEffect.StagedCase staged || !staged.Lines.Select(x => x.LineId).Order(StringComparer.Ordinal).SequenceEqual(lines.Select(x => x.LineId).Order(StringComparer.Ordinal)))
            {
                throw new AcceptanceException($"{label}: expected the receipt to name the staged lines, got {effect}");
            }
        }

        static bool Plain(Observations.TradeLine row) => row.TraderWillTrade && !row.Currency && row.Pawn is null && row.LineId.Length != 0;

        static double N(double value) => value;

        // Step 1: open, stage one buy and one sell, accept.
        var sessionId = await Open("open").ConfigureAwait(false);
        var before = await TradeKit.SheetAsync(s, "sheet", sessionId, cancellationToken).ConfigureAwait(false);
        Observations.TradeLine? buy = null;
        Observations.TradeLine? sell = null;
        foreach (var row in before.Lines.Where(Plain))
        {
            if (N(row.TraderCount) > 0 && N(row.MaximumCount) >= 1 && N(row.BuyPrice) > 0 && (buy is null || N(row.BuyPrice) < N(buy.BuyPrice)))
            {
                buy = row;
            }

            if (N(row.ColonyCount) > 0 && N(row.MinimumCount) <= -1 && N(row.SellPrice) > 0
                && (sell is null || -N(row.MinimumCount) * N(row.SellPrice) > -N(sell.MinimumCount) * N(sell.SellPrice)))
            {
                sell = row;
            }
        }

        if (buy is null || sell is null || buy.LineId == sell.LineId)
        {
            throw new AcceptanceException($"sheet: expected a distinct buy row and sell row, got buy={buy} sell={sell}");
        }

        // Sell enough to cover the one bought unit, within the colony's stock.
        var sellCount = (int)Math.Min(Math.Ceiling(N(buy.BuyPrice) / N(sell.SellPrice)) + 1, -N(sell.MinimumCount));
        s.RecordWire("buy", buy);
        s.RecordWire("sell", sell);
        await SetLines("lines", new Operations.TradeLine { LineId = buy.LineId, AbsoluteCount = 1 }, new Operations.TradeLine { LineId = sell.LineId, AbsoluteCount = -sellCount }).ConfigureAwait(false);
        var staged = await TradeKit.SheetAsync(s, "staged", sessionId, cancellationToken).ConfigureAwait(false);
        if (staged.Balance is not Observations.TradeBalance.Counted { ColonyCanAfford: true })
        {
            throw new AcceptanceException($"staged: the colony cannot afford the staged deal: {staged}");
        }

        var deal = TradeKit.AppliedTrade("accept", await Apply("accept", new Operations.AcceptTrade { ExpectedDealSignature = staged.DealSignature }).ConfigureAwait(false));
        s.RecordWire("accept", deal);
        if (deal is not Receipts.TradeEffect.ExecutedCase executed || !executed.ActuallyTraded || executed.BeforeSilver == executed.AfterSilver || executed.DealSignature != staged.DealSignature)
        {
            throw new AcceptanceException($"accept: expected an executed, traded, closed deal of the staged signature that moved colony silver, got {deal}");
        }

        var session = await TradeKit.SessionAsync(s, "session-closed", cancellationToken).ConfigureAwait(false);
        if (session.Engagement is not Observations.TradeEngagement.Idle)
        {
            throw new AcceptanceException($"session-closed: expected no live trade after accept, got {session}");
        }

        // Stock moved: a fresh session shows the trader's bought row and the colony's sold row down by the staged counts.
        sessionId = await Open("reopen").ConfigureAwait(false);
        var after = await TradeKit.SheetAsync(s, "after", sessionId, cancellationToken).ConfigureAwait(false);
        Observations.TradeLine? ByDef(Observations.TradeLine row) => after.Lines.FirstOrDefault(o => o.DefName == row.DefName && o.Stuff == row.Stuff);
        if (ByDef(buy) is not { } bought || N(bought.TraderCount) != N(buy.TraderCount) - 1)
        {
            throw new AcceptanceException($"after: expected the trader's {buy.LineId} down by one from {buy.TraderCount}, got {ByDef(buy)}");
        }

        if (ByDef(sell) is not { } sold || N(sold.ColonyCount) != N(sell.ColonyCount) - sellCount)
        {
            throw new AcceptanceException($"after: expected the colony's {sell.LineId} down by {sellCount} from {sell.ColonyCount}, got {ByDef(sell)}");
        }

        // Step 2: a buy the colony cannot afford is refused and the session stays open until a cancel ends it.
        var dear = after.Lines.Where(r => Plain(r) && N(r.MaximumCount) >= 1).MaxBy(r => N(r.MaximumCount) * N(r.BuyPrice))
            ?? throw new AcceptanceException($"after: no row left to buy: {after}");
        await SetLines("dear-lines", new Operations.TradeLine { LineId = dear.LineId, AbsoluteCount = (int)N(dear.MaximumCount) }).ConfigureAwait(false);
        var dearSheet = await TradeKit.SheetAsync(s, "dear", sessionId, cancellationToken).ConfigureAwait(false);
        if (dearSheet.Balance is Observations.TradeBalance.Counted { ColonyCanAfford: true })
        {
            throw new AcceptanceException($"dear: the colony can afford the whole of {dear.LineId}; no unaffordable deal to stage");
        }

        var refused = await Apply("dear-accept", new Operations.AcceptTrade { ExpectedDealSignature = dearSheet.DealSignature }).ConfigureAwait(false);

        // Validation catches the shortfall first (INVALID_REQUEST); a deal that slips past it and the game declines is NATIVE_FAILURE.
        if (TradeKit.RefusedCode(refused) is not (Common.FailureCode.InvalidRequest or Common.FailureCode.NativeFailure))
        {
            throw new AcceptanceException($"dear-accept: expected a refusal, got {refused}");
        }

        s.RecordWire("dearRefusal", refused.Outcome?.Refused);
        _ = TradeKit.AppliedTrade("cancel", await Apply("cancel", new Operations.EndTrade { Kind = Operations.EndTradeKind.Cancel }).ConfigureAwait(false));
    }
}
