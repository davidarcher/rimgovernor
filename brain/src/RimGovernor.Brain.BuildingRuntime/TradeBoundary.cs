using Receipt = RimGovernor.Brain.Executor.Receipt;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Narrows the bridge client to what <see cref="TradeBoundary"/> reads. Every trade action is an idempotent
/// intent naming its trader and negotiator, sent through Actions/Apply; native validates it against the one live
/// session and refuses with a reason when it is stale.
/// </summary>
public interface ITradeNative
{
    Task<(TradeSessionRead Read, BridgeResult Result)> ReadTradeSessionAsync(C.Identity identity, CancellationToken ct);
}

public interface ICommsTradeRequestNative
{
    Task<(ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result)> ReadTradeAcquisitionAsync(C.Identity identity, OperationsPb.FormCaravanIntent? intent, CancellationToken ct);

    Task<(TradersRead Read, BridgeResult Result)> ListTradersAsync(C.Identity identity, CancellationToken ct);
}

public sealed class TradeCapabilities
{
    public ITradeNative? Native { get; init; }

    public IActionsWriter? Writer { get; init; }

    public ICommsTradeRequestNative? Requests { get; init; }
}

/// <summary>
/// The trade inspections and writes of the executor. Go embedded the building boundary pointer and used only its
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
            BridgeApi.IntentAction("inspect", target.Action);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new EvidenceException(e);
        }
        var (session, _) = await Trade.Native!.ReadTradeSessionAsync(Boundary.Identity(target.Snapshot), ct).ConfigureAwait(false);
        var observed = Boundary.Context(session.Context, target.Snapshot);
        output = output with
        {
            Facts = new TradeAdmissionFacts { Snapshot = observed, PreviewTick = new Tick(session.Context?.Tick ?? 0) },
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
        var (facts, _) = await Trade.Requests!.ReadTradeAcquisitionAsync(Boundary.Identity(target.Snapshot), null, ct).ConfigureAwait(false);
        if (facts is null || facts.Context is null)
        {
            throw new EvidenceException();
        }
        output = output with
        {
            Snapshot = Boundary.Context(facts.Context, target.Snapshot),
            Tick = new Tick(facts.Context.Tick.GetValueOrDefault()),
        };
        var kind = request.Kind == TradeRequestKind.Orbital ? CommonPb.TradeRequestKind.Orbital : CommonPb.TradeRequestKind.Caravan;
        foreach (var row in facts.Requests)
        {
            if ((row.FactionId ?? "") == request.Faction && row.Kind == kind && (row.TraderKind ?? "") == request.TraderKind)
            {
                output = output with
                {
                    RequestKnown = true,
                    LastRequestTick = row.LastRequestTick.GetValueOrDefault(),
                    Eligible = row.Eligible.GetValueOrDefault() && row.NegotiatorIds.Contains(request.Negotiator)
                        && facts.Consoles.Any(c => (c.Id ?? "") == request.Console && c.NegotiatorIds.Contains(request.Negotiator)),
                };
            }
        }
        foreach (var work in facts.CommsWork)
        {
            if ((work.FactionId ?? "") == request.Faction && (work.ConsoleId ?? "") == request.Console && (work.NegotiatorId ?? "") == request.Negotiator)
            {
                output = output with { MatchingWork = true };
            }
        }
        foreach (var arrival in facts.Arrivals)
        {
            if ((arrival.FactionId ?? "") == request.Faction && arrival.Kind == kind && (arrival.TraderKind ?? "") == request.TraderKind)
            {
                output = output with { MatchingArrival = true };
            }
        }
        var (sellers, _) = await Trade.Requests.ListTradersAsync(Boundary.Identity(target.Snapshot), ct).ConfigureAwait(false);
        try
        {
            Boundary.Context(sellers.Context, target.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new EvidenceException(e);
        }
        if ((sellers.Context?.Tick ?? 0) < output.Tick.Value)
        {
            throw new EvidenceException();
        }
        output = output with { Tick = new Tick(sellers.Context!.Tick.GetValueOrDefault()) };
        foreach (var seller in sellers.Traders)
        {
            if (seller.Faction == request.Faction && seller.Kind == request.TraderKind)
            {
                output = output with { MatchingSeller = true };
            }
        }
        return output with { ObservedAt = Clock.Now() };
    }
}

/// <summary>Presents the rounds reviewer's observation clock as the executor's clock; both read the same instant source.</summary>
internal sealed class ExecutorClockOf(Observation.IClock inner) : IClock
{
    public DateTimeOffset Now() => inner.Now();
}
