#nullable enable

namespace RimGovernor.Fixtures.Contracts;

/// <summary>
/// The reply of <c>test/map_scope_generate</c> (a second player settlement map generated beside the viewed one) and
/// <c>test/map_scope_view</c> (the player's switch of the viewed map). No stage primitive settles a world tile or
/// switches the viewed map.
/// </summary>
public sealed record MapScopeReply
{
    /// <summary>Whether the fixture ran.</summary>
    public bool Success { get; init; }

    /// <summary>The map viewed when the fixture returns.</summary>
    public int ViewedMapId { get; init; }

    /// <summary>The map viewed before generation (generate only).</summary>
    public int HomeMapId { get; init; }

    /// <summary>The generated map (generate only).</summary>
    public int SecondMapId { get; init; }
}
