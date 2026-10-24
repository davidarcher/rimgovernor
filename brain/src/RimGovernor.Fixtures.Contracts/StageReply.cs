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

    /// <summary>Each staged research project's progress as a fraction of its base cost; 1 when finished.</summary>
    public IReadOnlyDictionary<string, float> Research { get; init; } = new Dictionary<string, float>();

    /// <summary>The found site, absolute; null when the spec had none.</summary>
    public StageRect? Site { get; init; }

    public IReadOnlyDictionary<string, StagedRoom> Rooms { get; init; } = new Dictionary<string, StagedRoom>();

    public IReadOnlyDictionary<string, StagedThings> Buildings { get; init; } = new Dictionary<string, StagedThings>();

    /// <summary>Bill name to the game's bill load id.</summary>
    public IReadOnlyDictionary<string, string> Bills { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, StagedThings> Things { get; init; } = new Dictionary<string, StagedThings>();

    public IReadOnlyDictionary<string, StagedThings> Plants { get; init; } = new Dictionary<string, StagedThings>();

    public IReadOnlyDictionary<string, StagedPawn> Pawns { get; init; } = new Dictionary<string, StagedPawn>();

    /// <summary>Zone name to the game's zone id.</summary>
    public IReadOnlyDictionary<string, string> Zones { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, StagedIncident> Incidents { get; init; } = new Dictionary<string, StagedIncident>();

    /// <summary>The outdoor temperature in °C and the map's local hour (0..23) once staging ends.</summary>
    public float OutdoorTemperature { get; init; }

    public int Hour { get; init; }

    /// <summary>One line per staged pawn and thing, for a combat case's metrics row.</summary>
    public string Digest { get; init; } = "";

    public IReadOnlyList<string> DigestRows { get; init; } = [];
}

/// <summary>The thing ids an entry made, and the cell of each, in the same order.</summary>
public sealed record StagedThings(IReadOnlyList<string> Ids, IReadOnlyList<StageCell> Cells)
{
    /// <summary>The player's powered turret guns among them, measured as the colony read measures a turret (test-only: the combat sweep's defense capacity).</summary>
    public IReadOnlyList<StagedGun> Guns { get; init; } = [];
}

/// <summary>A staged turret gun: its damage per second and whether its power trader is on.</summary>
public sealed record StagedGun(string Id, double Dps, bool Powered);

/// <summary>A staged pawn's primary weapon kind.</summary>
public enum StagedPrimary
{
    None,
    Melee,
    Ranged,
}

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

    /// <summary>The MentalStateDef and InspirationDef the pawn is in; null without one.</summary>
    public string? MentalState { get; init; }

    public string? Inspiration { get; init; }

    /// <summary>Summary health 0..1.</summary>
    public float Health { get; init; }

    /// <summary>How many apparel things it wears.</summary>
    public int Worn { get; init; }

    /// <summary>
    /// The pawn kind's combat power, and the pawn's melee and primary-ranged damage per second (ranged at its hit factor,
    /// #3845) as the colony read measures
    /// them (test-only: the combat sweep's raid points and defense capacity).
    /// </summary>
    public double CombatPower { get; init; }

    public double MeleeDps { get; init; }

    public double RangedDps { get; init; }

    public StagedPrimary Primary { get; init; }

    /// <summary>SkillDef to level for each skill the spec's Skills named, as the pawn holds it; null when it named none.</summary>
    public IReadOnlyDictionary<string, int>? Skills { get; init; }
}

/// <summary>A fired incident: the pawns it brought that trade.</summary>
public sealed record StagedIncident(IReadOnlyList<string> Traders);
