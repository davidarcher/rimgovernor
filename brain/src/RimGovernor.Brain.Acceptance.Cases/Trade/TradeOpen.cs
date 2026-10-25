using Receipts = RimGovernor.Contracts.Wire.Receipts;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>trade/open</c>: the OpenTrade intent against a staged trader caravan with the negotiator beside its trader (the
/// immediate open path). An unknown trader is refused, open applies, the same key replays, a fresh key for the same
/// pair reuses the live session, and a different negotiator is refused while it is held.
/// </summary>
internal static class TradeOpen
{
    internal static TypedCase Case() => new()
    {
        Name = "trade/open",
        Scope = "Native OpenTrade intent: a real TradeSession admission against a staged trader caravan, unknown-trader and "
            + "other-pair refusals, open-or-reuse under a fresh key, a resent key replayed, and the session in the trade-session read.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec { Incidents = [new() { Name = "trader", Def = "TraderCaravanArrival", TraderKind = "Caravan_Outlander_BulkGoods" }] },
        Ops = [TradeKit.Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var incident = s.Staged!.Incidents["trader"];
        var traderId = incident.Traders.Count > 0 ? incident.Traders[0] : throw new AcceptanceException($"stage: no trader pawn was spawned: {incident}");
        var crew = await s.FixtureAsync("teleport-adjacent", TradeKit.Teleport, traderId, cancellationToken).ConfigureAwait(false);
        s.Record("negotiators", ServedOps.Json(crew));
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        Task<Operations.ActionResult> Open(string key, string trader, string negotiator) => TradeKit.ApplyAsync(s, key, new Operations.TradeIntent
        {
            Target = new Common.TradeTarget { Kind = new Common.MapTradeTarget { TraderId = trader } },
            NegotiatorId = negotiator,
            Step = new Operations.OpenTrade { GiftMode = false },
        }, cancellationToken);

        var unknown = await Open("trade-open-unknown", "Thing_NoSuchTrader", crew.Negotiator).ConfigureAwait(false);
        if (TradeKit.RefusedCode(unknown) != Common.FailureCode.NotFound)
        {
            throw new AcceptanceException($"unknown-trader: expected a NOT_FOUND refusal, got {unknown}");
        }

        var first = await Open("trade-open", traderId, crew.Negotiator).ConfigureAwait(false);
        var opened = TradeKit.AppliedTrade("open", first) as Receipts.TradeEffect.OpenCase ?? throw new AcceptanceException("open: no open-session evidence");
        s.RecordWire("open", opened);
        if (string.IsNullOrEmpty(opened.SessionId) || string.IsNullOrEmpty(opened.FactionId))
        {
            throw new AcceptanceException($"open: expected session and faction ids on a bare open, neither executed nor closed, got {opened}");
        }

        var session = await TradeKit.SessionAsync(s, "session", cancellationToken).ConfigureAwait(false);
        if (session.Engagement is not Observations.TradeEngagement.Engaged { Open: true } engaged
            || engaged.Target.Kind?.MapTrader?.TraderId != traderId || engaged.Negotiator.Value != crew.Negotiator)
        {
            throw new AcceptanceException($"session: expected {crew.Negotiator} trading with {traderId} in an open session, got {session}");
        }

        var replay = await Open("trade-open", traderId, crew.Negotiator).ConfigureAwait(false);
        if (replay != first)
        {
            throw new AcceptanceException($"resend: the same key returned a different result: {replay}");
        }

        var again = await Open("trade-open-again", traderId, crew.Negotiator).ConfigureAwait(false);
        if (TradeKit.AppliedTrade("reuse", again) is not Receipts.TradeEffect.OpenCase reused || reused.SessionId != opened.SessionId)
        {
            throw new AcceptanceException($"reuse: expected the live session {opened.SessionId}, got {again}");
        }

        if (crew.Other.Length > 0)
        {
            var other = await Open("trade-open-other", traderId, crew.Other).ConfigureAwait(false);
            if (TradeKit.RefusedCode(other) != Common.FailureCode.StaleIdentity)
            {
                throw new AcceptanceException($"other-pair: expected a STALE_IDENTITY refusal, got {other}");
            }
        }
    }
}
