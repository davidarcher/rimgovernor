namespace RimGovernor.Brain.Store;

/// <summary>
/// A store's last validated review with the payload it was decoded from. The review is a function of its payload
/// alone, and a transaction reads it several times (an admission's safeguards, overlap and resource checks each
/// load it): decoding, re-encoding and validating it was the brain's largest allocator. An unchanged payload
/// therefore costs a string compare; any other payload, including one written from outside, is decoded and checked.
/// </summary>
internal sealed class RoundsSlot
{
    public sealed record Entry(string Payload, Rounds Review);

    private Entry? _loaded;

    public Entry? Loaded
    {
        get => Volatile.Read(ref _loaded);
        set => Volatile.Write(ref _loaded, value);
    }
}
