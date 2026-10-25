using RimGovernor.Brain.GameFacts.Host;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Host;

/// <summary><c>save_game</c>'s arguments: the speed matrix saves its stage through it.</summary>
public sealed record SaveGameRequest
{
    /// <summary>The save name without extension, under the profile's Saves.</summary>
    public required string SaveName { get; init; }
}
