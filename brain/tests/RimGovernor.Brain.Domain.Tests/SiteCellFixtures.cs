using RimGovernor.Brain.Policy;
using NativeCellGrid = RimGovernor.Brain.GameFacts.Mirror.CellGrid;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Complete native grid fixtures; all changes are staged before publishing a SiteCell view.</summary>
internal static class SiteCellFixtures
{
    public static SiteCell At(Cell cell, bool walkable = true, string? zoneId = null, string? roof = null,
        bool indoors = false, int storageStacks = 0, int storageCapacity = 1, bool doorway = false,
        double fertility = 1, bool polluted = false, double glow = 0, int? room = null,
        string terrain = "Soil", string baseTerrain = "Soil", bool foundationRemoval = false, Thing[]? things = null) =>
        new(new NativeCellGrid
        {
            X = cell.X, Z = cell.Z, Width = 1, Height = 1,
            Held = [true], Walkable = [walkable], Indoors = [indoors], Doorway = [doorway], Polluted = [polluted],
            InHome = [false], TopLayerRemovable = [false], FoundationRemoval = [foundationRemoval],
            StorageStacks = [storageStacks], StorageCapacity = [storageCapacity],
            Fertility = [(float)fertility], Glow = [(float)glow], SnowDepth = [0],
            Terrain = [terrain], BaseTerrain = [baseTerrain], Roof = [roof], ZoneId = [zoneId], Room = [room], Things = [things],
        }, 0);
}
