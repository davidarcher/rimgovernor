using RimGovernor.Protocol.Common;
using RimGovernor.Protocol.Observations;
using DomainCell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>Go's <c>CellRect</c>: one inclusive cell rectangle, the unit both defense reads and their callers' tiling work in.</summary>
public readonly record struct CellRect(DomainCell Min, DomainCell Max)
{
    /// <summary>Go's <c>Cells</c>: the inclusive cell count, zero for an inverted rectangle.</summary>
    public long Cells => Max.X < Min.X || Max.Z < Min.Z ? 0 : (long)(Max.X - Min.X + 1) * (Max.Z - Min.Z + 1);
}

/// <summary>
/// Go's <c>DefenseCell</c>: one observed cell of a defense site census. Unknown (fogged) cells carry only
/// <see cref="Fogged"/>; every other fact is the game's own: <see cref="CoverFill"/> (and sight blocking)
/// are what CoverUtility/GenSight consult, <see cref="EdgeReachable"/> is native ground reachability to a map edge
/// without opening doors (the walk-in raid approach), and NaturalRock/Door/PlayerOwned describe the edifice.
/// </summary>
public sealed record DefenseCell
{
    public DomainCell Cell { get; init; }

    public bool Fogged { get; init; }

    public string Terrain { get; init; } = "";

    public string EdificeDefName { get; init; } = "";

    /// <summary>The edifice's stuff def, empty for a stuffless one (a wooden door is upgraded to plasteel).</summary>
    public string EdificeStuff { get; init; } = "";

    public bool Walkable { get; init; }

    public bool Passable { get; init; }

    public double CoverFill { get; init; }

    public bool PlayerOwned { get; init; }

    public bool NaturalRock { get; init; }

    public bool Door { get; init; }

    public bool EdgeReachable { get; init; }

    /// <summary>The thing whose fill <see cref="CoverFill"/> reports, when the census can identify one the game's designators could remove.</summary>
    public DefenseCover? Cover { get; init; }

    /// <summary>A foundation removal designated on the cell.</summary>
    public bool Unbridging { get; init; }

    /// <summary>The cell is under any roof.</summary>
    public bool Roofed { get; init; }
}

/// <summary>
/// Go's <c>DefenseCover</c>: the clearance identity of one cover thing, what it is, which designation removes it
/// and whether one already stands.
/// </summary>
public sealed record DefenseCover(string ThingId, string DefName, CoverKind Kind, bool Designated)
{
    /// <summary>
    /// Go's <c>Designation</c>: the native designation def the cover's kind takes, the one a CoverClearance action
    /// carries; null (Go's empty string) for an unknown kind.
    /// </summary>
    public CoverClearanceDesignation? Designation =>
        Kind switch
        {
            CoverKind.Plant => CoverClearanceDesignation.CutPlant,
            CoverKind.Chunk => CoverClearanceDesignation.Haul,
            CoverKind.Mineable => CoverClearanceDesignation.Mine,
            CoverKind.Building => CoverClearanceDesignation.Deconstruct,
            _ => null,
        };
}

/// <summary>
/// Go's <c>RaidTrack</c>: one hostile lord the map has seen since load, its first pawn's spawn cell (<see cref="Ground"/>
/// when on the map edge) and a bounded trail of one of its pawns, sampled by the native RaidArrivalState component.
/// </summary>
public sealed record RaidTrack
{
    public string LordId { get; init; } = "";

    public string FactionDef { get; init; } = "";

    public Tick SpawnTick { get; init; }

    public Tick LastTick { get; init; }

    public DomainCell Spawn { get; init; }

    public bool Ground { get; init; }

    public IReadOnlyList<DomainCell> Trail { get; init; } = [];
}

/// <summary>Go's <c>DefenseSite</c>: one complete rectangle census with the context the caller validates against its acting generation.</summary>
public sealed record DefenseSite(ObservationContext? Context, uint Width, uint Height, CellRect Region, IReadOnlyList<DefenseCell> Cells);

/// <summary>
/// Go's <c>LineOfFire</c>: the native shooting-model evidence for one (firing, approach) pair, GenSight line of
/// sight and CoverUtility's overall block chance for a target at <see cref="To"/> shot from <see cref="From"/>
/// (<see cref="TargetCover"/>) and the reverse (<see cref="ShooterCover"/>). A fogged endpoint leaves
/// <see cref="Known"/> false with only the distance set.
/// </summary>
public sealed record LineOfFire
{
    public DomainCell From { get; init; }

    public DomainCell To { get; init; }

    public bool Known { get; init; }

    public bool LineOfSight { get; init; }

    public double TargetCover { get; init; }

    public double ShooterCover { get; init; }

    public double Distance { get; init; }
}

/// <summary>
/// Go's <c>LinesOfFire</c>: the pairs a read produced, firing-major. <see cref="Skipped"/> means the
/// LinesOfFireBudgetMS ran out: the pairs after <see cref="Lines"/> were not read, so a pair absent from
/// <see cref="Lines"/> is unknown, not blocked.
/// </summary>
public sealed record LinesOfFire(ObservationContext? Context, IReadOnlyList<LineOfFire> Lines, bool Skipped);

// Go defense_site.go. ReadDefenseSite, ReadLinesOfFire and the defense site / lines-of-fire reply validators are
// wire plumbing and not ported; the raid-track decoder the colony threat projection needs is.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>maxDefenseSiteExtent</c>: bounds a defense-layout cell coordinate.</summary>
    internal const int MaxDefenseSiteExtent = 4096;

    private static bool CellOnDefenseMap(DomainCell cell) => cell.X >= 0 && cell.Z >= 0 && cell.X < MaxDefenseSiteExtent && cell.Z < MaxDefenseSiteExtent;

    /// <summary>
    /// Go's <c>validateRaidTracks</c>: decodes the hostile lords of a threat census, one track per lord, ticks in
    /// order, cells on the map. Throws a contract error for an invalid track.
    /// </summary>
    internal static IReadOnlyList<RaidTrack> ValidateRaidTracks(IEnumerable<RimGovernor.Protocol.Observations.RaidTrack> rows)
    {
        var lords = new HashSet<string>(StringComparer.Ordinal);
        var tracks = new List<RaidTrack>();
        foreach (var row in rows)
        {
            if (!TryProtoCell(row.Spawn, out var spawn)
                || !row.HasLordId
                || !IsValidId(row.LordId)
                || lords.Contains(row.LordId)
                || !row.HasFactionDef
                || !IsValidId(row.FactionDef)
                || !row.HasSpawnTick
                || !row.HasLastTick
                || !row.HasGround
                || row.SpawnTick < 0
                || row.LastTick < row.SpawnTick
                || !CellOnDefenseMap(spawn))
            {
                throw Contract("invalid raid track");
            }
            lords.Add(row.LordId);
            var trail = new List<DomainCell>(row.Trail.Count);
            foreach (var at in row.Trail)
            {
                if (!TryProtoCell(at, out var cell) || !CellOnDefenseMap(cell))
                {
                    throw Contract("invalid raid track trail");
                }
                trail.Add(cell);
            }
            tracks.Add(
                new RaidTrack
                {
                    LordId = row.LordId,
                    FactionDef = row.FactionDef,
                    SpawnTick = new Tick(row.SpawnTick),
                    LastTick = new Tick(row.LastTick),
                    Spawn = spawn,
                    Ground = row.Ground,
                    Trail = trail,
                }
            );
        }
        return tracks;
    }
}

public static partial class BridgeFunctions
{
    /// <summary>Go's <c>validateDefenseRegion</c>: whether <paramref name="region"/> is a non-inverted rectangle on the defense map.</summary>
    public static bool IsValidDefenseRegion(CellRect region) =>
        region.Min.X >= 0
        && region.Min.Z >= 0
        && region.Max.X >= region.Min.X
        && region.Max.Z >= region.Min.Z
        && region.Max.X < MaxDefenseSiteExtent
        && region.Max.Z < MaxDefenseSiteExtent;
}
