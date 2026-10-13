using RimGovernor.Contracts.Wire.Lifecycle;
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
            Tick = new Tick((long)context.Tick.GetValueOrDefault()),
            NativeGeneration = context.NativeGeneration is not null ? Known(new NativeGeneration(context.NativeGeneration.GetValueOrDefault())) : default,
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
        switch (reply.Outcome)
        {
            case IdentityReplyOutcome.LoadedCase:
            {
                var loaded = reply.Outcome?.Loaded ?? throw new ObservationContractException("missing loaded identity");
                var result = ContextIdentity(loaded.Context);
                return loaded.Paused is not null ? result with { Paused = Known(loaded.Paused.GetValueOrDefault()) } : result;
            }
            case IdentityReplyOutcome.UnavailableCase:
                throw new BridgeUnavailableException();
            case IdentityReplyOutcome.FailureCase:
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
        switch (reply.Outcome)
        {
            case TickReplyOutcome.LoadedCase:
            {
                var loaded = reply.Outcome?.Loaded ?? throw new ObservationContractException("missing loaded tick");
                var result = ContextIdentity(loaded.Context);
                return loaded.Paused is not null ? result with { Paused = Known(loaded.Paused.GetValueOrDefault()) } : result;
            }
            case TickReplyOutcome.UnavailableCase:
                throw new BridgeUnavailableException();
            case TickReplyOutcome.FailureCase:
                throw new BridgeRefusedException();
            default:
                throw new ObservationContractException("tick outcome");
        }
    }
}
