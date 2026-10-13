namespace RimGovernor.Brain.NativeFacade;

/// <summary>
/// Where the native mod publishes its <see cref="INativeFacade"/> for the brain in the same process. The mod
/// sets it once at startup; the brain host reads it. One facade serves the process, so a second publish is a
/// bug and throws.
/// </summary>
public static class NativeFacadeRegistry
{
    private static INativeFacade? _current;

    /// <summary>The published facade, or null before the native mod has started.</summary>
    public static INativeFacade? Current => Volatile.Read(ref _current);

    /// <summary>Publishes the facade (the native mod, once).</summary>
    public static void Publish(INativeFacade facade)
    {
        ArgumentNullException.ThrowIfNull(facade);
        if (Interlocked.CompareExchange(ref _current, facade, null) is not null)
        {
            throw new InvalidOperationException("A native facade is already published.");
        }
    }

    /// <summary>The published facade, or an <see cref="InvalidOperationException"/> naming that the native mod has not started.</summary>
    public static INativeFacade Require() =>
        Current ?? throw new InvalidOperationException("The native mod has not published a facade.");
}
