using System.Text;

namespace RimGovernor.Brain.Observation;

// Package observation maps native wire observations into explicit domain facts. It does not infer player
// authority or manufacture runtime generations.

/// <summary>Go's <c>ErrContract</c>: the native observation breaks its contract.</summary>
public class ObservationContractException(string message) : DomainException("invalid native observation: " + message);

/// <summary>Go's <c>ErrChanged</c>: the native observation context changed between two reads.</summary>
public sealed class ObservationChangedException() : DomainException("native observation context changed");

/// <summary>Go's <c>ErrStale</c>: the native observation is older than the caller allows.</summary>
public sealed class ObservationStaleException() : DomainException("native observation is stale");

/// <summary>The domain identity of one observation: typed ids, tick, native generation and pause. The wire <c>Common.Identity</c> is the same context as raw strings and is decoded into this.</summary>
public sealed record Identity
{
    public ColonyId Colony { get; init; }

    public MapId Map { get; init; }

    public LoadId Load { get; init; }

    public Tick Tick { get; init; }

    public Fact<NativeGeneration> NativeGeneration { get; init; }

    public Fact<bool> Paused { get; init; }

    public void Validate()
    {
        foreach (var value in new[] { Colony.Value, Load.Value })
        {
            if (!IsValidText(value) || string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            {
                throw new ObservationContractException("colony/load identity");
            }
        }
        if (Map.Value < 0 || Tick.Value < 0)
        {
            throw new ObservationContractException("negative native identity field");
        }
        if (NativeGeneration.TryGetValue(out var generation) && generation.Value == 0)
        {
            throw new ObservationContractException("zero native generation");
        }
    }

    public bool SameContext(Identity other) => Colony == other.Colony && Map == other.Map && Load == other.Load;

    // utf8.ValidString: a string holding a lone surrogate cannot round-trip through UTF-8.
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

public enum Availability
{
    None = 0,

    [WireName("game_loaded")]
    GameLoaded,
}

public sealed record Status
{
    public Availability Availability { get; init; }

    public Fact<bool> Paused { get; init; }
}

/// <summary>
/// One tick observation: the identity the reply's ObservationContext reported (Observe issues a single native
/// call). Native generation is an optional observed fact; Manual and player direction are not inferred.
/// </summary>
public sealed record Snapshot
{
    public Identity Identity { get; init; } = new();

    public Status Status { get; init; } = new();

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset ObservedAt { get; init; }

    public void CheckFresh(DateTimeOffset now, TimeSpan maxAge, Identity current)
    {
        Identity.Validate();
        current.Validate();
        if (!Identity.SameContext(current) || current.Tick < Identity.Tick)
        {
            throw new ObservationChangedException();
        }
        if (maxAge < TimeSpan.Zero || StartedAt == default || ObservedAt == default || ObservedAt < StartedAt || now < StartedAt || now < ObservedAt || now - StartedAt > maxAge)
        {
            throw new ObservationStaleException();
        }
        if (Status.Availability != Availability.GameLoaded)
        {
            throw new ObservationChangedException();
        }
    }
}

/// <summary>Implemented by the bridge client. Typed observation code never receives arbitrary call names or arguments through this interface.</summary>
public interface ISource
{
    Task<L.TickReply> TickAsync(CancellationToken cancellationToken);
}

public sealed record Reading
{
    public Snapshot Snapshot { get; init; } = new();

}

public static class ObservationReads
{
    /// <summary>
    /// One lifecycle_read_tick call: the reply's ObservationContext carries the colony, load, map, tick and
    /// generation and the reply its pause state, which is everything a status observation reports. An
    /// unavailable reply (no game or no map) is the typed bridge unavailable exception.
    /// </summary>
    public static async Task<Reading> Observe(ISource? source, IClock? clock, CancellationToken cancellationToken)
    {
        if (source is null || clock is null)
        {
            throw new ObservationContractException("missing observation dependency");
        }
        var startedAt = clock.Now();
        var reply = await source.TickAsync(cancellationToken).ConfigureAwait(false);
        var identity = DecodeReads.DecodeTick(reply);
        var observedAt = clock.Now();
        if (startedAt == default || observedAt < startedAt)
        {
            throw new ObservationStaleException();
        }
        return new Reading
        {
            Snapshot = new Snapshot
            {
                StartedAt = startedAt,
                Identity = identity,
                Status = new Status { Availability = Availability.GameLoaded, Paused = identity.Paused },
                ObservedAt = observedAt,
            },
        };
    }
}
