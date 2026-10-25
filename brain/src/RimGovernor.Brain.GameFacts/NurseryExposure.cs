using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>One native map frame for sound and random social interaction. Cells are indexed x + z * Width.
/// Region -1 names a cell without a passable region; visibility is CanBeSeenOverFast, not walkability.</summary>
public sealed record NurseryGeometryRead(int Width, int Height,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<int>))] EquatableArray<int> CellRegions,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<bool>))] EquatableArray<bool> SeeOver,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<NurseryRegionRead>))] EquatableArray<NurseryRegionRead> Regions,
    double ScreamRadius, double SocialRadius, int ClamorRegions)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<NurseryBedSlotsRead>))]
    public EquatableArray<NurseryBedSlotsRead> BedSlots { get; init; }
}

/// <summary>Native first sleeping-slot offsets, indexed by the four rotations, for planned beds.</summary>
public sealed record NurseryBedSlotsRead(string Def,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Offsets);

/// <summary>Native region link enumeration order is significant at the clamor traversal cap.
/// Door regions do not count against that cap. Room is the native Room.ID, -1 when roomless.</summary>
public sealed record NurseryRegionRead(int Id, bool Door, bool Open, int Room,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<int>))] EquatableArray<int> Neighbours);

/// <summary>A stationary service's exact native cell; absence names an unprojected service, never a target anchor.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Located), "located")]
[JsonDerivedType(typeof(Possible), "possible")]
[JsonDerivedType(typeof(PassableMap), "passableMap")]
[JsonDerivedType(typeof(Unprojected), "unprojected")]
public abstract record ServicePositionRead
{
    private ServicePositionRead() { }
    public sealed record Located(Cell Cell) : ServicePositionRead;
    /// <summary>A verified enclosure containing every possible native stationary service cell.
    /// It may include unattainable cells; no probability distribution over the cells is asserted.</summary>
    public sealed record Possible([property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Cells) : ServicePositionRead;
    /// <summary>Every cell with a passable region in the shared NurseryGeometryRead; no duplicate map census or distribution.</summary>
    public sealed record PassableMap : ServicePositionRead;
    public sealed record Unprojected(string Reason) : ServicePositionRead;
}
