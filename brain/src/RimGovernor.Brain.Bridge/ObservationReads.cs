using RimGovernor.Brain.GameAccess;

namespace RimGovernor.Brain.Bridge;

public static class ObservationReads
{
    /// <summary>Reads one lifecycle scope. Native unavailability is reported by the source; no context is manufactured.</summary>
    public static async Task<ObservationReading> Observe(IObservationSource source, IClock clock, CancellationToken cancellationToken)
    {
        var startedAt = clock.Now();
        var reply = await source.TickAsync(cancellationToken).ConfigureAwait(false);
        if (reply is null) throw new ObservationContractException("missing tick");
        var identity = new ObservationIdentity { Context = reply.Context, Paused = reply.Paused };
        var observedAt = clock.Now();
        if (startedAt == default || observedAt < startedAt) throw new ObservationStaleException();
        return new ObservationReading { Identity = identity, StartedAt = startedAt, ObservedAt = observedAt };
    }
}
