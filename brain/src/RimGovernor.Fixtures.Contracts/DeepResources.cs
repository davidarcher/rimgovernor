#nullable enable

namespace RimGovernor.Fixtures.Contracts;

/// <summary>
/// The reply of <c>test/deep_resources_seed</c>: deep resource lumps written into the map's DeepResourceGrid, two mineral
/// scanners and two deep drills around the map centre. No stage primitive writes the deep resource grid.
/// </summary>
public sealed record DeepResourcesReply
{
    /// <summary>Whether the fixture ran.</summary>
    public bool Success { get; init; }

    /// <summary>The map centre the fixture laid out around, x.</summary>
    public int X { get; init; }

    /// <summary>The map centre the fixture laid out around, z.</summary>
    public int Z { get; init; }
}
