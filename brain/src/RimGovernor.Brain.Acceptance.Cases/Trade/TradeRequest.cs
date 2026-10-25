using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>trade/request-caravan</c> and <c>trade/request-orbital</c>: a real UseCommsConsole trader request, one row per
/// channel. An interrupted contact charges nothing; an unpowered console, the alliance cost and the cooldown refuse; a
/// paid contact debits goodwill, starts the cooldown and queues the vanilla arrival within the channel's delay. The
/// orbital row then sees the passing ship arrive; a requested caravan's arrival is 120,000 ticks out, past the cap, so
/// the caravan row claims the queued arrival tick instead.
/// </summary>
internal static class TradeRequest
{
    private sealed record Row(string Channel, RimGovernor.Brain.GameFacts.Common.TradeRequestKind Kind, int MinDelay, int MaxDelay, bool Arrives, int Ticks);

    private static readonly Row[] Rows =
    [
        new("caravan", RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Caravan, 120_000, 120_000, Arrives: false, Ticks: 3_000),
        new("orbital", RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Orbital, 2_500, 5_000, Arrives: true, Ticks: 11_000),
    ];

    internal static IEnumerable<TypedCase> Cases() => Rows.Select(row => new TypedCase
    {
        Name = "trade/request-" + row.Channel,
        Scope = "Real UseCommsConsole " + row.Channel + " trader request: an interruption has no charge; an unpowered console, the "
            + "alliance cost and the cooldown refuse; a paid contact debits goodwill, starts the cooldown and queues the native arrival"
            + (row.Arrives ? ", and the passing ship arrives." : " at the vanilla delay."),
        Ticks = row.Ticks,
        Tier = CaseTier.OnDemand,
        Ops = [TradeKit.Tool],
        Expansions = row.Kind == RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Orbital ? ["ludeon.rimworld.odyssey"] : [],
        Run = (s, ct) => RunAsync(s, row, ct),
    });

    private static async Task RunAsync(TypedSession s, Row row, CancellationToken cancellationToken)
    {
        Task<Fx.TradeRequestPrepared> Control(string action, Fx.TradeRequestPrepared? setup) =>
            s.FixtureAsync(action, TradeKit.Request, (action, row.Channel, setup), cancellationToken);

        var p = await Control("request_setup", null).ConfigureAwait(false);
        s.Record("prepared", ServedOps.Json(p));
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        Task<RimGovernor.Brain.GameFacts.Operations.ActionResult> Apply(string key, long tick) => TradeKit.ApplyAsync(s, key, new RimGovernor.Brain.GameOrders.Operations.CommsTradeRequestIntent
        {
            Kind = row.Kind,
            FactionId = p.Faction,
            TraderKind = p.TraderKind,
            ConsoleId = p.Console,
            NegotiatorId = p.Negotiator,
            ExpectedLastRequestTick = tick,
        }, cancellationToken);

        async Task<(RimGovernor.Brain.GameFacts.Observations.TradeAcquisition Facts, RimGovernor.Brain.GameFacts.Observations.TradeRequestOption? Request, RimGovernor.Brain.GameFacts.Observations.TradeRequestArrival? Arrival)> Read(string label, CancellationToken ct)
        {
            var facts = await TradeKit.AcquisitionAsync(s, label, null, ct).ConfigureAwait(false);
            return (
                facts,
                facts.Requests.LastOrDefault(r => r.FactionId == p.Faction && r.TraderKind == p.TraderKind),
                facts.Arrivals.LastOrDefault(a => a.FactionId == p.Faction && a.TraderKind == p.TraderKind));
        }

        async Task Unchanged(string label, Fx.TradeRequestPrepared before)
        {
            var (_, request, _) = await Read(label + "-facts", cancellationToken).ConfigureAwait(false);
            if (request?.Goodwill != before.Goodwill || request.LastRequestTick != before.LastRequestTick)
            {
                throw new AcceptanceException($"{label} changed payment: goodwill {before.Goodwill} tick {before.LastRequestTick} -> {request}");
            }
        }

        async Task Refusal(string label, long tick)
        {
            var result = await Apply(label, tick).ConfigureAwait(false);
            if (TradeKit.RefusedCode(result) != RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest)
            {
                throw new AcceptanceException($"{label}: expected an INVALID_REQUEST refusal, got {result}");
            }

            s.RecordWire(label, result.Outcome?.Refused);
        }

        async Task Queued(string label)
        {
            var result = await Apply(label, p.LastRequestTick).ConfigureAwait(false);
            var effect = TradeKit.Applied(label, result).CommsTradeRequest;
            if (effect?.WorkQueued != true || effect.TraderKind != p.TraderKind || effect.FactionId != p.Faction)
            {
                throw new AcceptanceException($"{label}: expected queued work for trader kind {p.TraderKind} of {p.Faction}, got {result}");
            }

            await Unchanged(label, p).ConfigureAwait(false);
            var (facts, _, _) = await Read(label + "-work", cancellationToken).ConfigureAwait(false);
            if (facts.CommsWork.Count == 0 || facts.Arrivals.Count != 0)
            {
                throw new AcceptanceException($"{label}: queued work misclassified: work {facts.CommsWork.Count}, arrivals {facts.Arrivals.Count}");
            }
        }

        await Queued("interrupted-request").ConfigureAwait(false);
        _ = await Control("request_interrupt", p).ConfigureAwait(false);
        await Unchanged("interrupted", p).ConfigureAwait(false);
        _ = await Control("request_console_off", p).ConfigureAwait(false);
        await Refusal("console-unavailable", p.LastRequestTick).ConfigureAwait(false);
        _ = await Control("request_console_on", p).ConfigureAwait(false);
        var low = await Control("request_low_goodwill", p).ConfigureAwait(false);
        if (!low.Ally)
        {
            throw new AcceptanceException($"low goodwill control must retain Ally: {low}");
        }

        await Refusal("alliance-cost", p.LastRequestTick).ConfigureAwait(false);
        await Unchanged("alliance-cost", low).ConfigureAwait(false);
        _ = await Control("request_restore_goodwill", p).ConfigureAwait(false);
        await Queued("paid-request").ConfigureAwait(false);

        RimGovernor.Brain.GameFacts.Observations.TradeRequestOption? applied = null;
        RimGovernor.Brain.GameFacts.Observations.TradeRequestArrival? arrival = null;
        _ = await s.RunUntilAsync("request-contact", 2_500, async ct =>
        {
            (_, applied, arrival) = await Read("request-contact-facts", ct).ConfigureAwait(false);
            return ($"{applied} {arrival}", arrival is not null && applied?.LastRequestTick > p.LastRequestTick);
        }, cancellationToken).ConfigureAwait(false);
        s.RecordWire("applied", applied);
        s.RecordWire("queuedArrival", arrival);
        if (applied?.Goodwill != p.Goodwill - p.Cost || !(applied.CooldownRemainingTicks > 0))
        {
            throw new AcceptanceException($"native payment/cooldown mismatch: {applied}, before goodwill {p.Goodwill} cost {p.Cost}");
        }

        var delay = arrival?.ArrivalTick - applied.LastRequestTick;
        if (!(delay >= row.MinDelay && delay <= row.MaxDelay))
        {
            throw new AcceptanceException($"native arrival {delay} ticks out, want {row.MinDelay}..{row.MaxDelay}: {arrival}");
        }

        await Refusal("cooldown", applied.LastRequestTick).ConfigureAwait(false);
        if (!row.Arrives)
        {
            return;
        }

        // The tick budget follows the native queue; no incident is injected.
        _ = await s.RunUntilAsync("native-arrival", row.MaxDelay + 3_000, async ct =>
        {
            var (facts, _, _) = await Read("native-arrival-facts", ct).ConfigureAwait(false);
            var ship = facts.PassingShips.FirstOrDefault(sh => sh.TraderKind == p.TraderKind);
            if (ship is not null)
            {
                s.RecordWire("arrived", ship);
            }

            return ($"{facts.PassingShips.Count} ships", ship is not null);
        }, cancellationToken).ConfigureAwait(false);
    }
}
