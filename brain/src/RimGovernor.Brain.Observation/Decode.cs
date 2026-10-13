namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    internal static Identity ContextIdentity(C.ObservationContext? context)
    {
        try
        {
            ValidateContext(context);
        }
        catch (DomainException e)
        {
            throw new ObservationContractException(e.Message);
        }
        var result = new Identity
        {
            Colony = new ColonyId(context!.Identity?.ColonyId ?? ""),
            Map = new MapId(context.Identity?.MapId ?? 0),
            Load = new LoadId(context.Identity?.LoadToken ?? ""),
            Tick = new Tick((long)context.Tick),
            NativeGeneration = context.HasNativeGeneration ? Known(new NativeGeneration(context.NativeGeneration)) : default,
        };
        result.Validate();
        return result;
    }

    public static Identity DecodeIdentity(L.IdentityReply? reply)
    {
        if (reply is null)
        {
            throw new ObservationContractException("missing identity");
        }
        switch (reply.OutcomeCase)
        {
            case L.IdentityReply.OutcomeOneofCase.Loaded:
            {
                var loaded = reply.Loaded ?? throw new ObservationContractException("missing loaded identity");
                var result = ContextIdentity(loaded.Context);
                return loaded.HasPaused ? result with { Paused = Known(loaded.Paused) } : result;
            }
            case L.IdentityReply.OutcomeOneofCase.Unavailable:
                throw new BridgeUnavailableException();
            case L.IdentityReply.OutcomeOneofCase.Failure:
                throw new BridgeRefusedException();
            default:
                throw new ObservationContractException("identity outcome");
        }
    }

    /// <summary>The identity a lifecycle_read_tick reply reports; it is <see cref="DecodeIdentity"/> without the capability list.</summary>
    public static Identity DecodeTick(L.TickReply? reply)
    {
        if (reply is null)
        {
            throw new ObservationContractException("missing tick");
        }
        switch (reply.OutcomeCase)
        {
            case L.TickReply.OutcomeOneofCase.Loaded:
            {
                var loaded = reply.Loaded ?? throw new ObservationContractException("missing loaded tick");
                var result = ContextIdentity(loaded.Context);
                return loaded.HasPaused ? result with { Paused = Known(loaded.Paused) } : result;
            }
            case L.TickReply.OutcomeOneofCase.Unavailable:
                throw new BridgeUnavailableException();
            case L.TickReply.OutcomeOneofCase.Failure:
                throw new BridgeRefusedException();
            default:
                throw new ObservationContractException("tick outcome");
        }
    }
}
