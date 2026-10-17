#nullable enable

namespace RimGovernor.Fixtures.Contracts;

/// <summary>
/// The reply of <c>test/runtime_fault_unpatch</c>: it removes one required authority hook from the live game, the
/// runtime fault no stage primitive can inject (process-scoped Harmony state, not map state).
/// </summary>
public sealed record RuntimeFaultReply
{
    /// <summary>Whether the fixture ran.</summary>
    public bool Success { get; init; }

    /// <summary>Whether the hook read uninstalled right after the unpatch.</summary>
    public bool Removed { get; init; }

    /// <summary>The verified hook targets right after the removal.</summary>
    public int Health { get; init; }

    /// <summary>The required hook targets.</summary>
    public int Required { get; init; }
}
