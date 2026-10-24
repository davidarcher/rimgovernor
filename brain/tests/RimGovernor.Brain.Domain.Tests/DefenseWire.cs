using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Whole defense-read wire rows for the decoder tests: the site census, the lines of fire and the access audit.</summary>
internal static class DefenseWire
{
    internal static readonly C.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };

    /// <summary>A frame's lines of fire when none were traced: no pair read, and none skipped.</summary>
    internal static readonly O.LinesOfFireSnapshot NoLines = new() { Context = Context, Lines = [], Skipped = false };

    internal static O.DefenseCell.Seen Open(Cell cell) => new()
    {
        Cell = cell,
        Terrain = "Soil",
        Walkable = true,
        Passable = true,
        CoverFill = 0,
        EdgeReachable = true,
        FoundationRemovalDesignated = false,
        Roofed = false,
        Edifice = null,
        Cover = null,
    };

    internal static O.AccessTarget Target(Cell cell, bool native, bool projected) => new() { Cell = cell, NativeReachable = native, ProjectedReachable = projected };

    internal static O.SpatialAccessSnapshot Access(params O.PawnAccess[] pawns) => new() { Context = Context, Pawns = [.. pawns] };
}
