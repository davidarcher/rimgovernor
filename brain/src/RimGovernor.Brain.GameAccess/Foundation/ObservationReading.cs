namespace RimGovernor.Brain.GameAccess;

/// <summary>The lifecycle scope supplied by a native source.</summary>
public interface IObservationSource
{
    Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick> TickAsync(CancellationToken cancellationToken);
}

/// <summary>One native context and its observation interval, without a second status or identity copy.</summary>
public sealed record ObservationReading
{
    public required ObservationIdentity Identity { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset ObservedAt { get; init; }

    public void CheckFresh(DateTimeOffset now, TimeSpan maxAge)
    {
        if (maxAge < TimeSpan.Zero || StartedAt == default || ObservedAt == default || ObservedAt < StartedAt
            || now < StartedAt || now < ObservedAt || now - StartedAt > maxAge)
            throw new ObservationStaleException();
    }
}
