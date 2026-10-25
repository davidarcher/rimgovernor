using System.Text;

namespace RimGovernor.Brain.GameAccess;

/// <summary>The native observation breaks its contract.</summary>
public class ObservationContractException(string message) : DomainException("invalid native observation: " + message);

/// <summary>The native observation context changed between two reads.</summary>
public sealed class ObservationChangedException() : DomainException("native observation context changed");

/// <summary>The native observation is older than the caller allows.</summary>
public sealed class ObservationStaleException() : DomainException("native observation is stale");

/// <summary>A typed view of the native context, with the caller's pause state. The observed fields have one owner.</summary>
public sealed record ObservationIdentity
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context
    {
        get;
        init
        {
            if (value is null) throw new ObservationContractException("missing observation context identity");
            ValidateContext(value);
            field = value;
        }
    }

    public bool Paused { get; init; }
    public ColonyId Colony => new(Context.Identity.ColonyId);
    public MapId Map => new(Context.Identity.MapId);
    public LoadId Load => new(Context.Identity.LoadToken);
    public Tick Tick => new(Context.Tick);
    public RimGovernor.Brain.GameFacts.Common.ObservedGeneration NativeGeneration => Context.NativeGeneration;

    public void Validate() => ValidateContext(Context);

    public bool SameContext(ObservationIdentity other) => Colony == other.Colony && Map == other.Map && Load == other.Load;

    private static void ValidateContext(RimGovernor.Brain.GameFacts.Common.ObservationContext context)
    {
        if (context?.Identity is null) throw new ObservationContractException("missing observation context identity");
        foreach (var value in new[] { context.Identity.ColonyId, context.Identity.LoadToken })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || !IsValidText(value))
                throw new ObservationContractException("colony/load identity");
        }
        if (context.Identity.MapId < 0 || context.Tick < 0)
            throw new ObservationContractException("negative native identity field");
        switch (context.NativeGeneration)
        {
            case RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase { Value: > 0 }:
            case RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase:
                return;
            case RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase:
                throw new ObservationContractException("zero native generation");
            case null:
                throw new ObservationContractException("missing native generation state");

        }
        throw new ObservationContractException("invalid native generation state");
    }

    private static bool IsValidText(string value)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetBytes(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
