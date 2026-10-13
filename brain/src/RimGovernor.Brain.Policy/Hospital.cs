namespace RimGovernor.Brain.Policy;

/// <summary>
/// What a MaintainMedicalReserves deficit needs from the facility ladder: a humanlike bed the game treats as
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
    public Fact<SleepingObservation> Sleeping { get; init; }
    public Fact<RoomObservation> Rooms { get; init; }
    public IReadOnlyList<BenchDefinition> Definitions { get; init; } = [];

    /// <summary>Orders the beds the planner may stage (RoomFurniture).</summary>
    public RoomFurniture Furniture { get; init; } = new();

    /// <summary>
    /// The routine counts a conversion must not drop below: the native indoor sleeping capacity excludes medical
    /// beds, so converting a needed shelter spot fails the shelter gate.
    /// </summary>
    public Fact<long> Colonists { get; init; }

    public Fact<long> HousingTarget { get; init; }
    public Fact<long> BedCapacity { get; init; }
    public Fact<long> IndoorCapacity { get; init; }

    /// <summary>The count of Doctor-capable colonists; a known zero means nobody could treat a patient in the ward, so no hospital work runs.</summary>
    public Fact<int> Doctors { get; init; }

    /// <summary>Patients whose operation waits on a better bed or room (SurgeryBedShortPatients): each needs a hosted medical bed, and while any waits a sleeping spot neither counts nor is staged.</summary>
    public IReadOnlyList<PawnId> Surgical { get; init; } = [];
}

public sealed record HospitalChoice
{
    public HospitalMethod Method { get; init; }

    /// <summary>The count of hosted medical beds the patients call for.</summary>
    public int Needed { get; init; }

    public string Bed { get; init; } = "";
    public string Definition { get; init; } = "";
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The living patients who need a hosted medical bed: those whose health read says they need rest, and the
    /// surgical patients. A hospital bed serves medical rest; MaintainMedicalReserves's wider census (any bad
    /// condition, e.g. a scar) keeps the deficit and does not size the ward. Not ok when the census or a patient's
    /// rest or death is unknown.
    /// </summary>
    private static (HashSet<PawnId> Ill, bool Ok) IllPatients(Fact<EquatableArray<CarePawn>> patients, IReadOnlyList<PawnId> surgical)
    {
        if (!patients.TryGetValue(out var rows))
        {
            return ([], false);
        }
        var ill = new HashSet<PawnId>();
        foreach (var p in rows)
        {
            var dk = p.Dead.TryGetValue(out var dead);
            var rk = p.NeedsRest.TryGetValue(out var rest);
            if (!dk)
            {
                return ([], false);
            }
            if (dead)
            {
                continue;
            }
            if (!rk)
            {
                return ([], false);
            }
            if (rest)
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
        var choice = new HospitalChoice { Needed = ill.Count };
        if (choice.Needed == 0)
        {
            return choice with { Method = HospitalMethod.NoDemand };
        }
        if (r.Doctors.TryGetValue(out var doctors) && doctors <= 0)
        {
            return choice with { Method = HospitalMethod.Unavailable };
        }
        var facility = Facility(RoomRole.Hospital);
        var sk = r.Sleeping.TryGetValue(out var sleeping);
        var rk = r.Rooms.TryGetValue(out var rooms);
        if (!sk || !rk)
        {
            return choice with { Method = HospitalMethod.Unknown };
        }
        var hosted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in rooms!.Rooms)
        {
            if (!room.Role.TryGetValue(out var role))
            {
                return choice with { Method = HospitalMethod.Unknown };
            }
            if (facility.Hosts(role))
            {
                foreach (var bed in room.Beds)
                {
                    hosted.Add(bed);
                }
            }
        }
        var medical = 0;
        var spare = new List<SleepingBed>();
        var owned = new List<SleepingBed>();
        foreach (var bed in sleeping!.Beds)
        {
            if (!hosted.Contains(bed.Id) || (surgical && bed.Definition.Value == "SleepingSpot"))
            {
                continue;
            }
            var hk = bed.Humanlike.TryGetValue(out var humanlike);
            var mk = bed.Medical.TryGetValue(out var isMedical);
            var pk = bed.Prisoners.TryGetValue(out var prisoners);
            if (!hk || !mk || !pk)
            {
                return choice with { Method = HospitalMethod.Unknown };
            }
            if (!humanlike || prisoners)
            {
                continue;
            }
            if (isMedical)
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
            if (surgical && name == SleepingSpotDefinition)
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
    /// max(colonists, housing target). Native IndoorSleepingCapacity excludes medical beds, so converting a needed
    /// shelter spot deadlocks the shelter gate; a new bed is staged instead (and converted once it stands, when
    /// the surplus it adds allows).
    /// </summary>
    private static bool HospitalConversionSpare(HospitalRequest r, SleepingObservation sleeping)
    {
        long required = sleeping.Colonists;
        foreach (var f in new[] { r.Colonists, r.HousingTarget })
        {
            if (f.TryGetValue(out var v))
            {
                required = Math.Max(required, v);
            }
        }
        long capacity = 0;
        var measured = false;
        foreach (var f in new[] { r.BedCapacity, r.IndoorCapacity })
        {
            if (f.TryGetValue(out var v))
            {
                if (!measured || v < capacity)
                {
                    capacity = v;
                }
                measured = true;
            }
        }
        if (!measured)
        {
            foreach (var bed in sleeping.Beds)
            {
                var humanlike = bed.Humanlike.ValueOrDefault;
                var mk = bed.Medical.TryGetValue(out var medical);
                var prisoners = bed.Prisoners.ValueOrDefault;
                if (humanlike && mk && !medical && !prisoners)
                {
                    capacity++;
                }
            }
        }
        return capacity - 1 >= required;
    }
}
