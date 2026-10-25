namespace RimGovernor.Brain.Policy;

/// <summary>The event-driven emergency read: awaiting its census, quiet, or answering live threats (including pending pods).</summary>
public abstract record HostileCensus
{
    private HostileCensus() { }

    public sealed record AwaitingEmergency : HostileCensus;

    public sealed record Quiet : HostileCensus;

    public sealed record Active(long Count) : HostileCensus;

    public static HostileCensus Of(long count) => count switch
    {
        0 => new Quiet(),
        > 0 => new Active(count),
        var invalid => throw new DomainException($"negative hostile census: {invalid}"),
    };

    /// <summary>The sampled threat count; no decision may price an emergency census that has not arrived.</summary>
    public long ThreatCount => this switch
    {
        Quiet => 0,
        Active { Count: > 0 } active => active.Count,
        Active => throw new DomainException("invalid active hostile census"),
        AwaitingEmergency => throw new RequiredFactException("hostiles"),
        HostileCensus unexpected => throw new DomainException($"invalid hostile census state: {unexpected.GetType().Name}"),
    };
}
