namespace RimGovernor.Brain.Observation;

public static class DecodeReads
{
    internal static Identity ContextIdentity(C.ObservationContext? context)
    {
        var result = new Identity
        {
            Colony = new ColonyId(context!.Identity?.ColonyId ?? ""),
            Map = new MapId(context.Identity?.MapId ?? 0),
            Load = new LoadId(context.Identity?.LoadToken ?? ""),
            Tick = new Tick((long)context.Tick),
            NativeGeneration = context.NativeGeneration is not null ? Known(new NativeGeneration(context.NativeGeneration.GetValueOrDefault())) : default,
        };
        result.Validate();
        return result;
    }

    public static Identity DecodeIdentity(L.LoadedIdentity? loaded)
    {
        if (loaded is null)
        {
            throw new ObservationContractException("missing identity");
        }

        var result = ContextIdentity(loaded.Context);
        return loaded.Paused is not null ? result with { Paused = Known(loaded.Paused.GetValueOrDefault()) } : result;
    }

    /// <summary>
    /// The identity a lifecycle_read_tick reply reports; it is what <see cref="DecodeIdentity"/> reads. No colony map
    /// (the main menu, between loads) is thrown by the facade as <see cref="NotLoadedException"/>, the expected state.
    /// </summary>
    public static Identity DecodeTick(L.LoadedTick? loaded)
    {
        if (loaded is null)
        {
            throw new ObservationContractException("missing tick");
        }

        var result = ContextIdentity(loaded.Context);
        return loaded.Paused is not null ? result with { Paused = Known(loaded.Paused.GetValueOrDefault()) } : result;
    }
}
