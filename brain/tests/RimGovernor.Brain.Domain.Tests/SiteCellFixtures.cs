using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared planning cell the tests stand cells with: a whole <see cref="SiteCell"/> (every required member) of
/// open, walkable, unzoned, unroofed outdoor soil in no room, holding nothing. Tests set what they exercise with
/// <c>with</c>.
/// </summary>
internal static class SiteCellFixtures
{
    public static readonly SiteCell Ground = new()
    {
        Cell = new Cell(0, 0),
        Walkable = true,
        Zone = false,
        Roofed = false,
        Indoors = false,
        StorageStacks = 0,
        StorageCapacity = 1,
        Doorway = false,
        Fertility = 1.0,
        Polluted = false,
        Glow = 0,
        Roof = null,
        ZoneId = null,
        Room = CellRoom.Outside,
        Terrain = "Soil",
        InHome = false,
        BaseTerrain = "Soil",
        FoundationRemoval = false,
        Things = [],
    };

    /// <summary>Open ground at <paramref name="cell"/>.</summary>
    public static SiteCell At(Cell cell) => Ground with { Cell = cell };
}
