using Receipt = RimGovernor.Brain.Executor.Receipt;

namespace RimGovernor.Brain.Runtime;

public sealed class TradeCapabilities
{
    public GameClient? Native { get; init; }

    public IActionsWriter? Writer { get; init; }

    public GameClient? Requests { get; init; }
}

/// <summary>
/// The trade inspections and writes of the executor. It needs only the building boundary's
/// clock and its intent dispatch, so this takes the clock directly. The natives it reads are asynchronous
/// bridge reads.
/// </summary>
internal sealed class TradeBoundary(IClock clock, TradeCapabilities trade) : ITradeBoundary, ICommsTradeBoundary
{
    private IClock Clock { get; } = clock;

    private TradeCapabilities Trade { get; } = trade;

    public Task<Receipt> WriteTradeAsync(Placement placement, CancellationToken ct) =>
        Boundary.DispatchIntentAsync(placement, Trade.Writer!, ct);

    public Task<Receipt> WriteCommsTradeRequestAsync(Placement placement, CancellationToken ct) =>
        Boundary.DispatchIntentAsync(placement, Trade.Writer!, ct);

    /// <summary>
    /// Anchors the dispatch to a fresh native read. It does not preview: native judges the intent when it
    /// applies, and a refusal there is the intent's terminal receipt, where a refused preview would only hold it.
    /// </summary>
    public async Task<TradeInspection> InspectTradeAsync(Target target, CancellationToken ct)
    {
        var output = new TradeInspection { StartedAt = Clock.Now() };
        try
        {
            IntentActionRules.IntentAction("inspect", target.Action);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new EvidenceException(e);
        }
        var session = await Trade.Native!.ReadTradeSessionAsync(Boundary.Identity(target.Snapshot), ct).ConfigureAwait(false);
        var observed = Boundary.Context(session.Context, target.Snapshot);
        output = output with
        {
            Facts = new TradeAdmissionFacts { Snapshot = observed, PreviewTick = new Tick(session.Context.Tick) },
            ObservedAt = Clock.Now(),
        };
        ct.ThrowIfCancellationRequested();
        return output;
    }

    public async Task<CommsTradeInspection> InspectCommsTradeRequestAsync(Target target, CancellationToken ct)
    {
        var output = new CommsTradeInspection { StartedAt = Clock.Now() };
        if (target.Action.CommsTradeRequest is not { } request)
        {
            throw new EvidenceException();
        }
        var facts = await Trade.Requests!.ReadTradeAcquisitionAsync(Boundary.Identity(target.Snapshot), null, ct).ConfigureAwait(false);
        output = output with
        {
            Snapshot = Boundary.Context(facts.Context, target.Snapshot),
            Tick = new Tick(facts.Context.Tick),
        };
        var kind = request.Kind == TradeRequestKind.Orbital ? RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Orbital : RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Caravan;
        foreach (var row in facts.Requests)
        {
            if (row.FactionId == request.Faction && row.Kind == kind && row.TraderKind == request.TraderKind)
            {
                output = output with
                {
                    RequestKnown = true,
                    LastRequestTick = row.LastRequestTick,
                    Eligible = row.Eligible && row.NegotiatorIds.Contains(request.Negotiator)
                        && facts.Consoles.Any(c => c.Id == request.Console && c.NegotiatorIds.Contains(request.Negotiator)),
                };
            }
        }
        foreach (var work in facts.CommsWork)
        {
            if (work.FactionId == request.Faction && work.ConsoleId == request.Console && work.NegotiatorId == request.Negotiator)
            {
                output = output with { MatchingWork = true };
            }
        }
        foreach (var arrival in facts.Arrivals)
        {
            if (arrival.FactionId == request.Faction && arrival.Kind == kind && arrival.TraderKind == request.TraderKind)
            {
                output = output with { MatchingArrival = true };
            }
        }
        var sellers = await Trade.Requests.ListTradersAsync(Boundary.Identity(target.Snapshot), ct).ConfigureAwait(false);
        try
        {
            Boundary.Context(sellers.Context, target.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new EvidenceException(e);
        }
        if (sellers.Context.Tick < output.Tick.Value)
        {
            throw new EvidenceException();
        }
        output = output with { Tick = new Tick(sellers.Context.Tick) };
        foreach (var seller in sellers.Traders)
        {
            if (seller.FactionId == request.Faction && seller.Kind == request.TraderKind)
            {
                output = output with { MatchingSeller = true };
            }
        }
        return output with { ObservedAt = Clock.Now() };
    }
}
