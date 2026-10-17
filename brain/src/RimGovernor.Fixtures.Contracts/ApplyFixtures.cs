#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

/// <summary>
/// The reply of <c>test/apply_refusal_prepare</c>: one target per routine write kind apply/refusal moves the world
/// under. A roofed, walled 2x3 interior (a stockpile zone over its first 2x2, the other two cells free roofed ground), an
/// allowed WoodLog stack to haul, a draftable colonist with hauling enabled, a wild hare to tame, a plant pot with a crop
/// it does not grow yet and an open build cell. The case's moves and refused writes name these exact targets.
/// </summary>
public sealed record ApplyRefusalPrepared
{
    public bool Success { get; init; }

    public string ZoneId { get; init; } = "";

    public IReadOnlyList<FixtureCell> ZoneCells { get; init; } = [];

    public IReadOnlyList<FixtureCell> FreeCells { get; init; } = [];

    public FixtureCell? BuildCell { get; init; }

    public string HaulItemId { get; init; } = "";

    public string PawnId { get; init; } = "";

    public string TameId { get; init; } = "";

    public string GrowerId { get; init; } = "";

    public string GrowerCrop { get; init; } = "";
}
