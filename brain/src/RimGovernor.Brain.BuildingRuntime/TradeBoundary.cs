using Receipt = RimGovernor.Brain.Executor.Receipt;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Narrows the bridge client to what <see cref="TradeBoundary"/> reads. Every trade action is an idempotent
/// intent naming its trader and negotiator, sent through Actions/Apply; native validates it against the one live
/// session and refuses with a reason when it is stale.
/// </summary>
public interface ITradeNative
{
    (TradeSessionRead Read, BridgeResult Result) ReadTradeSession(CancellationToken ct, C.Identity identity);
}

public interface ICommsTradeRequestNative
{
    (ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result) ReadTradeAcquisition(CancellationToken ct, C.Identity identity, OperationsPb.FormCaravanIntent? intent);

    (TradersRead Read, BridgeResult Result) ListTraders(CancellationToken ct, C.Identity identity);
}

public sealed class TradeCapabilities
{
    public ITradeNative? Native { get; init; }

    public IActionsWriter? Writer { get; init; }

    public ICommsTradeRequestNative? Requests { get; init; }
}

/// <summary>
/// The trade inspections and writes of the executor. Go embedded the building boundary pointer and used only its
/// clock and its intent dispatch, so this takes the clock directly. The natives it reads are the planner's
/// synchronous census reads, so the inspections complete inline and the tasks they return are already finished.
/// </summary>
internal sealed class TradeBoundary(IClock clock, TradeCapabilities trade) : ITradeBoundary, ICommsTradeBoundary
{
    private IClock Clock { get; } = clock;

    private TradeCapabilities Trade { get; } = trade;

    public Task<TradeInspection> InspectTradeAsync(Target target, CancellationToken ct) =>
        Task.FromResult(InspectTrade(ct, target));

    public Task<Receipt> WriteTradeAsync(Placement placement, CancellationToken ct) =>
        Boundary.DispatchIntentAsync(placement, Trade.Writer!, ct);

    public Task<CommsTradeInspection> InspectCommsTradeRequestAsync(Target target, CancellationToken ct) =>
        Task.FromResult(InspectCommsTradeRequest(ct, target));

    public Task<Receipt> WriteCommsTradeRequestAsync(Placement placement, CancellationToken ct) =>
        Boundary.DispatchIntentAsync(placement, Trade.Writer!, ct);

    /// <summary>
    /// Anchors the dispatch to a fresh native read. It does not preview: native judges the intent when it
    /// applies, and a refusal there is the intent's terminal receipt, where a refused preview would only hold it.
    /// </summary>
    public TradeInspection InspectTrade(CancellationToken ct, Target target)
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
        var (session, _) = Trade.Native!.ReadTradeSession(ct, Boundary.Identity(target.Snapshot));
        var observed = Boundary.Context(session.Context, target.Snapshot);
        output = output with
        {
            Facts = new TradeAdmissionFacts { Snapshot = observed, PreviewTick = new Tick(session.Context?.Tick ?? 0) },
            ObservedAt = Clock.Now(),
        };
        ct.ThrowIfCancellationRequested();
        return output;
    }

    public CommsTradeInspection InspectCommsTradeRequest(CancellationToken ct, Target target)
    {
        var output = new CommsTradeInspection { StartedAt = Clock.Now() };
        if (target.Action.CommsTradeRequest is not { } request)
        {
            throw new EvidenceException();
        }
        var (facts, _) = Trade.Requests!.ReadTradeAcquisition(ct, Boundary.Identity(target.Snapshot), null);
        if (facts is null || facts.Context is null)
        {
            throw new EvidenceException();
        }
        output = output with
        {
            Snapshot = Boundary.Context(facts.Context, target.Snapshot),
            Tick = new Tick(facts.Context.Tick),
        };
        var kind = request.Kind == TradeRequestKind.Orbital ? CommonPb.TradeRequestKind.Orbital : CommonPb.TradeRequestKind.Caravan;
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
        var (sellers, _) = Trade.Requests.ListTraders(ct, Boundary.Identity(target.Snapshot));
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
        output = output with { Tick = new Tick(sellers.Context!.Tick) };
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
