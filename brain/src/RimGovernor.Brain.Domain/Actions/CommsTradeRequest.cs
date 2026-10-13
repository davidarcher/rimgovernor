namespace RimGovernor.Brain.Domain;

public enum TradeRequestKind
{
    Caravan,
    Orbital,
}

/// <summary>
/// Fences a single paid vanilla request. Native cooldown and queue facts own recovery; a queued comms job is
/// not the request effect.
/// </summary>
public sealed record CommsTradeRequest : IActionPayload
{
    /// <summary>Go <c>CommsTradeRequest.Validate</c> as the constructor; throws <see cref="DomainException"/> when invalid.</summary>
    public CommsTradeRequest(TradeRequestKind kind, string faction, string traderKind, string console, string negotiator, long expectedLastRequestTick)
    {
        DomainException.Require(
            Wire.IsDefined(kind)
                && Identity.IsValidText(faction)
                && Identity.IsValidText(traderKind)
                && Identity.IsValidText(console)
                && Identity.IsValidText(negotiator)
                && expectedLastRequestTick >= int.MinValue
                && expectedLastRequestTick <= int.MaxValue,
            "invalid comms trade request"
        );
        Kind = kind;
        Faction = faction;
        TraderKind = traderKind;
        Console = console;
        Negotiator = negotiator;
        ExpectedLastRequestTick = expectedLastRequestTick;
    }

    public TradeRequestKind Kind { get; }

    public string Faction { get; }

    public string TraderKind { get; }

    public string Console { get; }

    public string Negotiator { get; }

    public long ExpectedLastRequestTick { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewCommsTradeRequestAction(ActionId id, CommsTradeRequest request) => Create(id, ActionKind.CommsTradeRequest, request);

    public CommsTradeRequest? CommsTradeRequest => PayloadOf<CommsTradeRequest>(ActionKind.CommsTradeRequest);
}
