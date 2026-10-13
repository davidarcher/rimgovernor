using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>
    /// The free, instant-build spot the game's gathering worker picks: RCellFinder.TryFindGatheringSpot takes
    /// a built colony building whose def is in GatheringDef.gatherSpotDefs (Core Party lists PartySpot) at a
    /// cell that passes GatheringsUtility.ValidateGatheringSpot (standable, safe, roofed, reservable).
    /// EnsureComfort keeps one in the best shared room.
    /// </summary>
    public const string PartySpotDefinition = "PartySpot";
}

/// <summary>What the PartySpot review reads.</summary>
public sealed record PartySpotInput
{
    public Fact<RoomObservation> Rooms { get; init; }

    public Fact<IReadOnlyList<UpkeepRoom>> Qualities { get; init; }

    public ImpressivenessLevels Levels { get; init; } = new();

    public Fact<CurrentConstruction> Census { get; init; }

    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    /// <summary>Whether a PartySpot can be raised at all: the definition is available and a colonist can build it.</summary>
    public bool Placeable { get; init; }

    /// <summary>
    /// Reports whether this Episode already spent the one-shot for the building to retire (retire true) or
    /// for the room to place in (room "" is anywhere). A spent one-shot drops the deficit instead of holding
    /// it.
    /// </summary>
    public Func<bool, string, bool>? Spent { get; init; }
}

/// <summary>The one step the colony owes its PartySpot; the default value owes none.</summary>
public sealed record PartySpotNeed
{
    /// <summary>The standing spot to deconstruct: a duplicate, or the spot of a room a strictly higher impressiveness stage outranks.</summary>
    public CurrentBuilding? Retire { get; init; }

    public bool HasRetire => Retire is not null;

    /// <summary>Owes a spot, in Room's Cells (Room "" places in any roofed indoor cell, Cells empty).</summary>
    public bool Place { get; init; }

    public string Room { get; init; } = "";

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    /// <summary>Reports whether any step is owed.</summary>
    public bool Owed => HasRetire || Place;
}

public static partial class PolicyFunctions
{
    private sealed record PartyCandidate(string Id, int Stage, int Size, IReadOnlyList<Cell> Cells);

    /// <summary>
    /// Decides the PartySpot step. The best shared room is the dining or rec room of the highest observed
    /// impressiveness stage, ties broken by cell count. The spot moves only to a room of a strictly higher
    /// stage than the incumbent's room, read from the observed spot; same-stage rooms never move it.
    /// Duplicates are retired. Unknown rooms, qualities or census leave the need unknown.
    /// </summary>
    public static Fact<PartySpotNeed> ReviewPartySpot(PartySpotInput input)
    {
        if (!input.Rooms.TryGetValue(out var rooms) || !input.Qualities.TryGetValue(out var qualities)
            || !input.Census.TryGetValue(out var census) || !census.Colony)
        {
            return Unknown<PartySpotNeed>();
        }
        foreach (var site in census.Sites)
        {
            if (site.Building!.Definition == PolicyConstants.PartySpotDefinition)
            {
                // A blueprint or frame is the step in flight.
                return Known(new PartySpotNeed());
            }
        }
        var quality = new Dictionary<string, RoomQuality>(StringComparer.Ordinal);
        foreach (var q in qualities)
        {
            if (q.Quality.TryGetValue(out var v))
            {
                quality[q.Id] = v;
            }
        }
        var free = new HashSet<Cell>();
        foreach (var c in input.Cells)
        {
            c.Roofed.TryGetValue(out var roofed);
            c.Indoors.TryGetValue(out var indoors);
            if (roofed && indoors && !c.Occupied())
            {
                free.Add(c.Cell);
            }
        }
        var stages = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new List<PartyCandidate>();
        foreach (var room in rooms.Rooms)
        {
            var known = room.Role.TryGetValue(out var role);
            var scored = quality.TryGetValue(room.Id, out var q);
            if (!known || (role != RoomRole.DiningRoom && role != RoomRole.RecRoom) || !scored)
            {
                continue;
            }
            stages[room.Id] = input.Levels.Stage(q!.Impressiveness);
            var cells = room.Cells.Where(free.Contains).ToList();
            candidates.Add(new PartyCandidate(room.Id, stages[room.Id], room.Cells.Count, cells));
        }
        // The best room with a free cell: highest stage, then the larger room.
        PartyCandidate? best = null;
        foreach (var c in candidates)
        {
            if (c.Cells.Count == 0)
            {
                continue;
            }
            if (best is null || c.Stage > best.Stage
                || (c.Stage == best.Stage && (c.Size > best.Size || (c.Size == best.Size && string.CompareOrdinal(c.Id, best.Id) < 0))))
            {
                best = c;
            }
        }
        var spots = census.Buildings.Where(b => b.Building!.Definition == PolicyConstants.PartySpotDefinition && b.Cells.Count > 0).ToList();
        // StageOf is the stage of the room holding a standing spot: -1 outside every shared room; not scored
        // (false) in a shared room with no quality read, which never moves the spot.
        (int Stage, bool Scored) StageOf(CurrentBuilding b)
        {
            foreach (var room in rooms.Rooms)
            {
                if (room.Cells.Contains(b.Cells[0]))
                {
                    var known = room.Role.TryGetValue(out var role);
                    if (!known || (role != RoomRole.DiningRoom && role != RoomRole.RecRoom))
                    {
                        return (-1, true);
                    }
                    var scored = stages.TryGetValue(room.Id, out var stage);
                    return (stage, scored);
                }
            }
            return (-1, true);
        }
        bool Spent(bool retire, string id) => input.Spent is not null && input.Spent(retire, id);
        if (spots.Count > 1)
        {
            // Keep the spot in the highest stage room (then the lowest ID); the rest are duplicates, each
            // retired once.
            var keep = 0;
            for (var i = 0; i < spots.Count; i++)
            {
                var si = StageOf(spots[i]).Stage;
                var sk = StageOf(spots[keep]).Stage;
                if (si > sk || (si == sk && string.CompareOrdinal(spots[i].Id, spots[keep].Id) < 0))
                {
                    keep = i;
                }
            }
            for (var i = 0; i < spots.Count; i++)
            {
                if (i != keep && !Spent(true, spots[i].Id))
                {
                    return Known(new PartySpotNeed { Retire = spots[i] });
                }
            }
            return Known(new PartySpotNeed());
        }
        if (spots.Count == 1)
        {
            var (incumbent, scored) = StageOf(spots[0]);
            if (scored && best is not null && best.Stage > incumbent && !Spent(true, spots[0].Id))
            {
                return Known(new PartySpotNeed { Retire = spots[0] });
            }
            return Known(new PartySpotNeed());
        }
        if (!input.Placeable)
        {
            return Known(new PartySpotNeed());
        }
        var need = new PartySpotNeed { Place = true };
        if (best is not null)
        {
            need = need with { Room = best.Id, Cells = best.Cells };
        }
        else if (free.Count == 0)
        {
            return Known(new PartySpotNeed());
        }
        if (Spent(false, need.Room))
        {
            return Known(new PartySpotNeed());
        }
        return Known(need);
    }
}
