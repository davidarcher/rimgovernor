namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Narrows the bridge client to what <see cref="TradeBoundary"/> reads. Every trade action is an idempotent
/// intent naming its trader and negotiator, sent through Actions/Apply; native validates it against the one live
/// session and refuses with a reason when it is stale.
/// </summary>
public interface ITradeNative
{
    (Bridge.TradeSessionRead Read, BridgeResult Result) ReadTradeSession(CancellationToken ct, CommonPb.Identity identity);
}

public interface ICommsTradeRequestNative
{
    (ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result) ReadTradeAcquisition(CancellationToken ct, CommonPb.Identity identity, OperationsPb.FormCaravanIntent? intent);

    (Bridge.TradersRead Read, BridgeResult Result) ListTraders(CancellationToken ct, CommonPb.Identity identity);
}

public sealed class TradeCapabilities
{
    public ITradeNative? Native { get; init; }

    public Boundary.IActionsWriter? Writer { get; init; }

    public ICommsTradeRequestNative? Requests { get; init; }
}

/// <summary>
/// The building boundary extended with the trade inspections and writes. Go embedded the boundary pointer, so
/// every promoted method is the wrapped <see cref="Place"/>'s; the executor-facing members promoted from it
/// are awaiting the boundary package port.
/// </summary>
internal sealed class TradeBoundary(Boundary.Boundary boundary, TradeCapabilities trade) : Executor.ITradeBoundary, Executor.ICommsTradeRequestBoundary
{
    public Boundary.Boundary Place { get; } = boundary;

    private TradeCapabilities Trade { get; } = trade;

    /// <summary>
    /// Anchors the dispatch to a fresh native read. It does not preview: native judges the intent when it
    /// applies, and a refusal there is the intent's terminal receipt, where a refused preview would only hold it.
    /// </summary>
    public Executor.TradeInspection InspectTrade(CancellationToken ct, Executor.Target target)
    {
        var output = new Executor.TradeInspection { StartedAt = Place.Clock.Now() };
        try
        {
            Bridge.Intents.IntentAction("inspect", target.Action);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new Executor.EvidenceException(e);
        }
        var (session, _) = Trade.Native!.ReadTradeSession(ct, BoundaryApi.Identity(target.Snapshot));
        var observed = BoundaryApi.Context(session.Context, target.Snapshot);
        output = output with
        {
            Facts = new Pol.TradeAdmissionFacts { Snapshot = observed, PreviewTick = new Tick(session.Context?.Tick ?? 0) },
            ObservedAt = Place.Clock.Now(),
        };
        ct.ThrowIfCancellationRequested();
        return output;
    }

    /// <summary>Sends the intent through Actions/Apply.</summary>
    public Executor.Receipt WriteTrade(CancellationToken ct, Executor.Placement p) =>
        Place.DispatchIntent(ct, p, Trade.Writer!);

    public Executor.CommsTradeInspection InspectCommsTradeRequest(CancellationToken ct, Executor.Target target)
    {
        var output = new Executor.CommsTradeInspection { StartedAt = Place.Clock.Now() };
        if (target.Action.CommsTradeRequest is not { } request)
        {
            throw new Executor.EvidenceException();
        }
        var (facts, _) = Trade.Requests!.ReadTradeAcquisition(ct, BoundaryApi.Identity(target.Snapshot), null);
        if (facts is null || facts.Context is null)
        {
            throw new Executor.EvidenceException();
        }
        output = output with
        {
            Snapshot = BoundaryApi.Context(facts.Context, target.Snapshot),
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
        var (sellers, _) = Trade.Requests.ListTraders(ct, BoundaryApi.Identity(target.Snapshot));
        try
        {
            BoundaryApi.Context(sellers.Context, target.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new Executor.EvidenceException(e);
        }
        if ((sellers.Context?.Tick ?? 0) < output.Tick.Value)
        {
            throw new Executor.EvidenceException();
        }
        output = output with { Tick = new Tick(sellers.Context!.Tick) };
        foreach (var seller in sellers.Traders)
        {
            if (seller.Faction == request.Faction && seller.Kind == request.TraderKind)
            {
                output = output with { MatchingSeller = true };
            }
        }
        return output with { ObservedAt = Place.Clock.Now() };
    }

    public Executor.Receipt WriteCommsTradeRequest(CancellationToken ct, Executor.Placement placement) =>
        Place.DispatchIntent(ct, placement, Trade.Writer!);
}
