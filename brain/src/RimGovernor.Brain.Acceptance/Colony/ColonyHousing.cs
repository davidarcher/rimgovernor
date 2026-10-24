using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Common = RimGovernor.Contracts.Wire.Common;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The colony review's housing census (#3751): how many colonists own a bed in an enclosed room the game scores a Bedroom
/// or Barracks, read from the live game each timeline sample under <see cref="Key"/> as {colonists, housed}.
/// colonyreview's layout section reads it for the housed-by day.
/// </summary>
public static class ColonyHousing
{
    /// <summary>The timeline sample key.</summary>
    public const string Key = "housing";

    /// <summary>The live colonist count.</summary>
    public const string ColonistsKey = "colonists";

    /// <summary>The colonists owning a bed in an enclosed Bedroom or Barracks.</summary>
    public const string HousedKey = "housed";

    /// <summary>The watch probe reading the census on <paramref name="h"/> under <paramref name="identity"/>.</summary>
    public static WatchProbe Probe(Harness h, Common.Identity identity) => new(Key, ct => SampleAsync(h, identity, ct));

    /// <summary>Reads the colonists and the rooms and returns {colonists, housed}.</summary>
    public static async Task<JsonObject> SampleAsync(Harness h, Common.Identity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(h);
        var scope = TypedReads.Scope(identity);
        var pawnsReply = await h.CallAsync("housing-pawns", NativeOps.ListPawns, new Observations.ListPawnsRequest { Scope = scope, Filter = new() { Colonist = true } }, cancellationToken).ConfigureAwait(false);
        var pawns = TypedReads.Observed("housing-pawns", NativeOps.ListPawns, pawnsReply, pawnsReply.Outcome?.Observed).Pawns;
        var roomsReply = await h.CallAsync("housing-rooms", NativeOps.ListRooms, new Observations.ListRoomsRequest { Scope = scope }, cancellationToken).ConfigureAwait(false);
        var rooms = TypedReads.Observed("housing-rooms", NativeOps.ListRooms, roomsReply, roomsReply.Outcome?.Observed).Rooms;
        var (colonists, housed) = Count(pawns, rooms);
        return new JsonObject { { ColonistsKey, colonists }, { HousedKey, housed } };
    }

    /// <summary>The live colonists, and those whose owned bed stands in an enclosed Bedroom or Barracks.</summary>
    public static (int Colonists, int Housed) Count(IEnumerable<Observations.PawnState> pawns, IEnumerable<Observations.RoomState> rooms)
    {
        ArgumentNullException.ThrowIfNull(pawns);
        ArgumentNullException.ThrowIfNull(rooms);
        var beds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in rooms.Where(r => Enclosed(r) && (r.Role == Wire.Name(RoomRole.Bedroom) || r.Role == Wire.Name(RoomRole.Barracks))))
        {
            beds.UnionWith(room.Beds.Select(b => b.Id));
        }
        var live = pawns.Where(p => !p.Dead).ToList();
        return (live.Count, live.Count(p => p.OwnedBed is { Id: { Length: > 0 } id } && beds.Contains(id)));
    }

    // The census's enclosure (as Observation's TemperatureRooms reads Room.Enclosed): a proper room, no doorway, not
    // outdoors or psychologically outdoors, off the map edge and fully roofed.
    private static bool Enclosed(Observations.RoomState r) =>
        r.ProperRoom == true && r.Doorway != true && r.Outdoors != true && r.PsychologicallyOutdoors != true && r.TouchesMapEdge != true && (r.OpenRoofCount ?? 0) == 0;
}
