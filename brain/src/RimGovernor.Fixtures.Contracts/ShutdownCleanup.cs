namespace RimGovernor.Fixtures.Contracts;

/// <summary>Observed state after the real engine quit callbacks, before the engine frees leftover allocations.</summary>
public sealed record ShutdownCleanupReply
{
    public bool BrainStopped { get; init; }
    public bool MapDisposed { get; init; }
    public bool GameCleared { get; init; }
    public long BytesBefore { get; init; }
    public long BytesAfter { get; init; }
}
