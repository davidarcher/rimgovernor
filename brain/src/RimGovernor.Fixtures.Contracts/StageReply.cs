#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

/// <summary>
/// What <c>test/stage</c> made, keyed by the names in the <see cref="StageSpec"/>. Every cell is absolute. A refused
/// staging is a tool error, so a reply always reports success.
/// </summary>
public sealed record StageReply
{
    public bool Success { get; init; }

    public int Seed { get; init; }

    /// <summary>The found site, absolute; null when the spec had none.</summary>
    public StageRect? Site { get; init; }

    public IReadOnlyDictionary<string, StagedRoom> Rooms { get; init; } = new Dictionary<string, StagedRoom>();

    public IReadOnlyDictionary<string, StagedThings> Buildings { get; init; } = new Dictionary<string, StagedThings>();

    public IReadOnlyDictionary<string, StagedThings> Things { get; init; } = new Dictionary<string, StagedThings>();

    public IReadOnlyDictionary<string, StagedPawn> Pawns { get; init; } = new Dictionary<string, StagedPawn>();

    /// <summary>Zone name to the game's zone id.</summary>
    public IReadOnlyDictionary<string, string> Zones { get; init; } = new Dictionary<string, string>();

    /// <summary>One line per staged pawn and thing, for a combat case's metrics row.</summary>
    public string Digest { get; init; } = "";

    public IReadOnlyList<string> DigestRows { get; init; } = [];
}

/// <summary>The thing ids an entry made, and the cell of each, in the same order.</summary>
public sealed record StagedThings(IReadOnlyList<string> Ids, IReadOnlyList<StageCell> Cells);

/// <summary>A built room's wall and door ids and its interior cells.</summary>
public sealed record StagedRoom(IReadOnlyList<string> Walls, IReadOnlyList<string> Doors, IReadOnlyList<StageCell> Interior);

/// <summary>A staged pawn as it stands once staging ends.</summary>
public sealed record StagedPawn
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public StageCell Cell { get; init; }

    /// <summary>False while an arrival still carries it (a drop pod).</summary>
    public bool Spawned { get; init; }

    public bool Hostile { get; init; }

    public bool Prisoner { get; init; }

    public string? Weapon { get; init; }

    public string? LordJob { get; init; }

    /// <summary>Summary health 0..1.</summary>
    public float Health { get; init; }

    public IReadOnlyList<StagedApparel> Worn { get; init; } = [];
}

public sealed record StagedApparel(string Id, string Def);
