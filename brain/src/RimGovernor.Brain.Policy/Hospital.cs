namespace RimGovernor.Brain.Policy;

/// <summary>
/// What an unmet medical/care Need needs from the facility ladder: a humanlike bed the game treats as
/// medical, standing in a room whose native role can host the Hospital function.
/// </summary>
public enum HospitalMethod
{
    /// <summary>A native fact the choice depends on is unobserved.</summary>
    [WireName("")]
    Unknown = 0,

    /// <summary>Enough hosted medical beds already stand; tending, rescue and the medicine reserve own the deficit from here.</summary>
    [WireName("existing")]
    Existing,

    /// <summary>Flag Bed, an existing hosted bed, as medical.</summary>
    [WireName("convert")]
    Convert,

    /// <summary>Stage Definition in a Hospital-hosting room; the next review converts it.</summary>
    [WireName("build")]
    Build,

    /// <summary>No living colonist is a patient.</summary>
    [WireName("no_demand")]
    NoDemand,

    /// <summary>No bed can be converted without evicting a healthy colonist and no bed definition is buildable now.</summary>
    [WireName("unavailable")]
    Unavailable,
}

/// <summary>Asks for a hospital bed; the planner stages RoomFurniture.HospitalBeds in preference order, a sleeping spot (the free bed) last.</summary>
public sealed record HospitalRequest
{
    public Fact<EquatableArray<CarePawn>> Patients { get; init; }
    public SleepingObservation Sleeping { get; init; } = new();
    public Fact<RoomObservation> Rooms { get; init; }
    public IReadOnlyList<BenchDefinition> Definitions { get; init; } = [];

    /// <summary>Orders the beds the planner may stage (RoomFurniture).</summary>
    public RoomFurniture Furniture { get; init; } = new();

    /// <summary>
    /// The routine counts a conversion must not drop below: the native indoor sleeping capacity excludes medical
    /// beds, so converting a needed bed fails the sleeping gate.
    /// </summary>
    public long Colonists { get; init; }

    public long BedCapacity { get; init; }
    public long IndoorCapacity { get; init; }

    /// <summary>The count of Doctor-capable colonists; a known zero means nobody could treat a patient in the ward, so no hospital work runs.</summary>
    public Fact<int> Doctors { get; init; }

    /// <summary>Patients whose operation waits on a better bed or room (SurgeryBedShortPatients): each needs a hosted medical bed, and while any waits a sleeping spot neither counts nor is staged.</summary>
    public IReadOnlyList<PawnId> Surgical { get; init; } = [];

    /// <summary>The hosted medical beds a Hospital goal asks for whatever the patients (#3496); 0 for none.</summary>
    public int MinBeds { get; init; }
}

public sealed record HospitalChoice
{
    public HospitalMethod Method { get; init; }

    /// <summary>The count of hosted medical beds the patients call for.</summary>
    public int Needed { get; init; }

    public string Bed { get; init; } = "";
    public string Definition { get; init; } = "";
}

public static class HospitalRules
{
    /// <summary>
    /// The living patients who need a hosted medical bed: those whose health read says they need rest, and the
    /// surgical patients. A hospital bed serves medical rest; the medical/care Need's wider census (any bad
    /// condition, e.g. a scar) keeps the deficit and does not size the ward. Not ok when the census or a patient's
    /// rest or death is unknown.
    /// </summary>
    internal static (HashSet<PawnId> Ill, bool Ok) IllPatients(Fact<EquatableArray<CarePawn>> patients, IReadOnlyList<PawnId> surgical)
    {
        if (!patients.TryGetValue(out var rows))
        {
            return ([], false);
        }
        var ill = new HashSet<PawnId>();
        foreach (var p in rows)
        {
            if (!p.Dead && p.NeedsRest)
            {
                ill.Add(p.Id);
            }
        }
        foreach (var id in surgical)
        {
            ill.Add(id);
        }
        return (ill, true);
    }

    /// <summary>The beds standing in a room that can host the Hospital function.</summary>
    private static HashSet<string> HostedBeds(RoomObservation rooms)
    {
        var facility = RoomsRules.Facility(RoomRole.Hospital);
        return new HashSet<string>(rooms.Rooms.Where(room => facility.Hosts(room.Role)).SelectMany(room => room.BedIds()), StringComparer.Ordinal);
    }

    /// <summary>Whether a bed counts towards the ward: hosted, humanlike and no prisoner's, and not a sleeping spot while an operation waits on a better bed.</summary>
    private static bool Eligible(SleepingBed bed, HashSet<string> hosted, bool surgical) =>
        hosted.Contains(bed.Id) && !(surgical && bed.Definition.Value == SleepingRules.SleepingSpotDefinition.ToString()) && bed.Humanlike && !bed.Prisoners;

    /// <summary>The hosted medical beds standing.</summary>
    public static int HostedMedicalBeds(RoomObservation rooms, SleepingObservation sleeping, bool surgical)
    {
        var hosted = HostedBeds(rooms);
        return sleeping.Beds.Count(b => b.Medical && Eligible(b, hosted, surgical));
    }

    /// <summary>
    /// Whether the hosted medical beds fall below a Hospital goal's bed floor, the unmet half of the care Need when no
    /// patient is sick (#3496); a known false without a floor, unknown while the room census is unread.
    /// </summary>
    public static Fact<bool> BedsShort(int floor, Fact<RoomObservation> rooms, SleepingObservation sleeping, bool surgical)
    {
        if (floor <= 0)
        {
            return Fact.Known(false);
        }
        if (!rooms.TryGetValue(out var census))
        {
            return Fact.Unknown<bool>();
        }
        return Fact.Known(HostedMedicalBeds(census, sleeping, surgical) < floor);
    }

    /// <summary>
    /// Decides whether the colony's patients already have hosted medical beds, which existing bed to convert, or
    /// which definition to stage. Conversion prefers an unowned bed, then a bed a patient owns (the game drops the
    /// owner, who then rests in it as a patient); a healthy colonist is never evicted for a hospital bed while one
    /// can be built instead. It issues no orders and reserves nothing.
    /// </summary>
    public static HospitalChoice SelectHospitalBed(HospitalRequest r)
    {
        var (ill, known) = IllPatients(r.Patients, r.Surgical);
        if (!known)
        {
            return new HospitalChoice { Method = HospitalMethod.Unknown };
        }
        var surgical = r.Surgical.Count > 0;
        var choice = new HospitalChoice { Needed = Math.Max(ill.Count, r.MinBeds) };
        if (choice.Needed == 0)
        {
            return choice with { Method = HospitalMethod.NoDemand };
        }
        if (r.Doctors.TryGetValue(out var doctors) && doctors <= 0)
        {
            return choice with { Method = HospitalMethod.Unavailable };
        }
        var sleeping = r.Sleeping;
        var rk = r.Rooms.TryGetValue(out var rooms);
        if (!rk)
        {
            return choice with { Method = HospitalMethod.Unknown };
        }
        var hosted = HostedBeds(rooms!);
        var medical = 0;
        var spare = new List<SleepingBed>();
        var owned = new List<SleepingBed>();
        foreach (var bed in sleeping!.Beds)
        {
            if (!Eligible(bed, hosted, surgical))
            {
                continue;
            }
            if (bed.Medical)
            {
                medical++;
                continue;
            }
            if (bed.Owners.Count == 0)
            {
                spare.Add(bed);
                continue;
            }
            var patientOwned = true;
            foreach (var owner in bed.Owners)
            {
                patientOwned = patientOwned && ill.Contains(owner);
            }
            if (patientOwned)
            {
                owned.Add(bed);
            }
        }
        if (medical >= choice.Needed)
        {
            return choice with { Method = HospitalMethod.Existing };
        }
        if (!HospitalConversionSpare(r, sleeping))
        {
            spare = [];
            owned = [];
        }
        foreach (var candidates in new[] { spare, owned })
        {
            if (candidates.Count == 0)
            {
                continue;
            }
            var first = candidates.OrderBy(c => c.Id, StringComparer.Ordinal).First();
            return choice with { Method = HospitalMethod.Convert, Bed = first.Id };
        }
        var byName = new Dictionary<string, BenchDefinition>(StringComparer.Ordinal);
        foreach (var d in r.Definitions)
        {
            byName[d.Name] = d;
        }
        var unknown = false;
        foreach (var name in r.Furniture.HospitalBeds())
        {
            if (surgical && name == SleepingRules.SleepingSpotDefinition)
            {
                continue;
            }
            var exists = byName.TryGetValue(name, out var d);
            var ak = false;
            var available = false;
            if (exists)
            {
                ak = d!.Available.TryGetValue(out available);
            }
            if (!exists || !ak)
            {
                unknown = true;
                continue;
            }
            if (available)
            {
                return choice with { Method = HospitalMethod.Build, Definition = name };
            }
        }
        if (unknown)
        {
            return choice with { Method = HospitalMethod.Unknown };
        }
        return choice with { Method = HospitalMethod.Unavailable };
    }

    /// <summary>
    /// Reports whether one regular bed can turn medical without dropping bed or indoor sleeping capacity below
    /// the colonist count. Native IndoorSleepingCapacity excludes medical beds, so converting a needed
    /// bed deadlocks the sleeping gate; a new bed is staged instead (and converted once it stands, when
    /// the surplus it adds allows).
    /// </summary>
    private static bool HospitalConversionSpare(HospitalRequest r, SleepingObservation sleeping)
    {
        var required = Math.Max(sleeping.Colonists, r.Colonists);
        return Math.Min(r.BedCapacity, r.IndoorCapacity) - 1 >= required;
    }
}
