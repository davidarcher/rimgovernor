namespace RimGovernor.Brain.Policy;

/// <summary>
/// What a MaintainHousing deficit needs next: ownership of a vacant suitable bed for one colonist, or one more
/// humanlike bed staged in a room that can host a Bedroom. Observed sleep in the assigned bed, never an
/// assignment or construction receipt, completes the goal (ReviewSleeping).
/// </summary>
public enum SleepingMethod
{
    /// <summary>A native fact the choice depends on is unobserved.</summary>
    Unknown,

    /// <summary>Every colonist owns a suitable bed; the goal waits on observed use.</summary>
    NoDemand,

    /// <summary>Transfer ownership of Bed to Pawn (AssignIntent).</summary>
    Assign,

    /// <summary>Stage Definition in a Bedroom-hosting room whose temperature suits the colonists still waiting; the next review assigns it.</summary>
    Build,

    /// <summary>Set Bed, a vacant colonist bed that would suit the waiting slave Pawn, for slaves (BuildingPatchIntent for_slaves); the next review assigns it.</summary>
    MarkSlaves,

    /// <summary>Nobody can be assigned and no bed definition is buildable now.</summary>
    Unavailable,
}

public sealed record SleepingRequest
{
    public Fact<EquatableArray<SleepingTarget>> Targets { get; init; }

    public Fact<SleepingObservation> Sleeping { get; init; }

    public Fact<RoomObservation> Rooms { get; init; }

    public EquatableArray<BenchDefinition> Definitions { get; init; }

    /// <summary>Orders the beds the planner may stage (RoomFurniture).</summary>
    public RoomFurniture Furniture { get; init; } = new();

    /// <summary>Names the bedroll definitions whose stuff is on hand.</summary>
    public IReadOnlySet<string>? Stocked { get; init; }

    /// <summary>
    /// RoomQualityTargets keyed by room, and Traits, order the beds an assignment offers: a room marked
    /// NeverUpgrade is never left for a more impressive one, and an ascetic takes the plainest bed. Null
    /// leaves the lowest-bed-ID order.
    /// </summary>
    public IReadOnlyDictionary<string, RoomTarget>? RoomTargets { get; init; }

    public IReadOnlyDictionary<PawnId, TraitEffects>? Traits { get; init; }
}

public sealed record SleepingChoice
{
    public SleepingMethod Method { get; init; }

    /// <summary>Names the unobserved fact behind SleepingMethod.Unknown.</summary>
    public string Missing { get; init; } = "";

    /// <summary>
    /// Waiting counts the colonists still without observed use of a suitable owned bed; Unhoused counts those
    /// among them with no bed to be assigned, the number of beds still to stage.
    /// </summary>
    public int Waiting { get; init; }

    public int Unhoused { get; init; }

    public PawnId Pawn { get; init; }

    public string Bed { get; init; } = "";

    /// <summary>The pawn's currently owned bed the assignment expects, empty when none.</summary>
    public string PreviousBed { get; init; } = "";

    public string Definition { get; init; } = "";

    /// <summary>The hosting-room cells whose observed temperature suits the waiting colonists; a build is sited only there.</summary>
    public EquatableArray<Cell> Cells { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Staged first when a waiting colonist has a couple partner.</summary>
    public const string SleepingCoupleBedDefinition = "DoubleBed";

    public const string SleepingBedrollDefinition = "Bedroll";
    public const string SleepingCoupleBedrollDefinition = "BedrollDouble";
    public const string SleepingSpotDefinition = "SleepingSpot";

    /// <summary>The definitions staged only with their stuff on hand.</summary>
    internal static readonly HashSet<string> SleepingBedrolls = new(StringComparer.Ordinal) { SleepingBedrollDefinition, SleepingCoupleBedrollDefinition };

    /// <summary>
    /// The best buildable rung of the ladder: the first available definition, a bedroll only when Stocked
    /// names it. Unknown is returned when an unknown row precedes it and nothing is buildable. A sleeping spot
    /// is never a suitable bed, so only spot true, a bedroom furnished to move a spot owner in, walks down to it.
    /// </summary>
    public static (string Definition, SleepingMethod Method) SleepingDefinition(
        RoomFurniture furniture, IReadOnlyList<BenchDefinition> definitions, IReadOnlySet<string>? stocked, bool couple, bool spot)
    {
        var byName = new Dictionary<string, BenchDefinition>(StringComparer.Ordinal);
        foreach (var d in definitions)
        {
            byName[d.Name] = d;
        }
        var unknown = false;
        foreach (var name in furniture.SleepingLadder(couple))
        {
            if (name == SleepingSpotDefinition && !spot)
            {
                continue;
            }
            var exists = byName.TryGetValue(name, out var d);
            var ak = exists && d!.Available.TryGetValue(out var available);
            if (!exists || !ak)
            {
                unknown = true;
                continue;
            }
            d!.Available.TryGetValue(out var avail);
            if (avail && (!SleepingBedrolls.Contains(name) || (stocked is not null && stocked.Contains(name))))
            {
                return (name, SleepingMethod.Build);
            }
        }
        if (unknown)
        {
            return ("", SleepingMethod.Unknown);
        }
        return ("", SleepingMethod.Unavailable);
    }

    /// <summary>Filters and orders the vacant beds offered to t.</summary>
    private static List<string> SleepingBedOrder(SleepingRequest r, SleepingTarget t, List<string> beds)
    {
        if (!r.Sleeping.TryGetValue(out var sleeping) || (r.RoomTargets is null && r.Traits is null))
        {
            return beds;
        }
        var impressiveness = new Dictionary<string, double>(StringComparer.Ordinal);
        if (sleeping.Rooms.TryGetValue(out var rooms))
        {
            foreach (var room in rooms)
            {
                if (room.Quality.TryGetValue(out var q))
                {
                    impressiveness[room.Id] = q.Impressiveness;
                }
            }
        }
        var quality = new Dictionary<string, double>(StringComparer.Ordinal);
        var roomOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in sleeping.Beds)
        {
            if (b.Room.TryGetValue(out var room))
            {
                roomOf[b.Id] = room;
                if (impressiveness.TryGetValue(room, out var v))
                {
                    quality[b.Id] = v;
                }
            }
        }
        if (r.RoomTargets is not null && roomOf.TryGetValue(t.PreviousBed, out var previousRoom)
            && r.RoomTargets.TryGetValue(previousRoom, out var target) && t.PreviousBed != "" && target.NeverUpgrade)
        {
            var ck = quality.TryGetValue(t.PreviousBed, out var current);
            var kept = new List<string>();
            foreach (var bed in beds)
            {
                var vk = quality.TryGetValue(bed, out var v);
                if (vk && (target.Max <= 0 || v < target.Max) && (!ck || v <= current))
                {
                    kept.Add(bed);
                }
            }
            beds = kept;
        }
        if (r.Traits is not null && r.Traits.TryGetValue(t.Pawn, out var effects) && effects.Ascetic)
        {
            // Stable: LINQ OrderBy keeps equal elements in order.
            beds = beds.OrderBy(bed => quality.TryGetValue(bed, out _) ? 0 : 1)
                .ThenBy(bed => quality.TryGetValue(bed, out var q) ? q : 0)
                .ToList();
        }
        return beds;
    }

    /// <summary>
    /// Picks one bounded step for the sleeping deficit. Assignment comes first (the lowest pawn ID with a
    /// vacant suitable bed, the lowest bed ID), since it costs nothing and the review already vetted the bed's
    /// roof, temperature and access for that pawn. Otherwise one bed is staged in a hosting room warm enough
    /// for the waiting colonists; a room too cold or hot for them is not a site, so the temperature family
    /// keeps that deficit. It issues no orders and reserves nothing. Throws DomainException when the bedroom
    /// facility is missing.
    /// </summary>
    public static SleepingChoice SelectSleepingMethod(SleepingRequest r)
    {
        if (!r.Targets.TryGetValue(out var targets))
        {
            return new SleepingChoice { Method = SleepingMethod.Unknown, Missing = "sleeping_targets" };
        }
        var choice = new SleepingChoice { Waiting = targets.Count };
        if (choice.Waiting == 0)
        {
            return choice with { Method = SleepingMethod.NoDemand };
        }
        // A hosted guest is assigned after every colonist, and never causes a bed to be built.
        var guests = new HashSet<PawnId>();
        if (r.Sleeping.TryGetValue(out var census))
        {
            foreach (var g in census.Guests)
            {
                guests.Add(g.Id);
            }
        }
        var ordered = targets
            .OrderBy(t => guests.Contains(t.Pawn) ? 1 : 0)
            .ThenBy(t => t.Pawn)
            .ToList();
        // A bed already owned by the pawn is never reassigned to itself; the review lists only vacant beds as
        // available, so that cannot arise, but the assignment contract requires it.
        foreach (var t in ordered)
        {
            if (t.Kind == SleepingNeedKind.UseNeeded || t.Available.Count == 0)
            {
                continue;
            }
            // The review lists beds in preference order (a couple's double bed first); room targets and traits reorder stably.
            var beds = SleepingBedOrder(r, t, t.Available.ToList());
            foreach (var bed in beds)
            {
                if (bed != t.PreviousBed)
                {
                    return choice with { Method = SleepingMethod.Assign, Pawn = t.Pawn, Bed = bed, PreviousBed = t.PreviousBed };
                }
            }
        }
        // Everyone still waiting either has a bed to use or has nothing to be assigned; only the latter needs a bed built.
        double lo = -1e9, hi = 1e9;
        bool needsBed = false, couple = false;
        if (!r.Sleeping.TryGetValue(out var sleeping))
        {
            return choice with { Method = SleepingMethod.Unknown, Missing = "sleeping_census" };
        }
        var (pawn, markBed, marked) = SlaveBedToMark(ordered, sleeping);
        if (marked)
        {
            return choice with { Method = SleepingMethod.MarkSlaves, Pawn = pawn, Bed = markBed };
        }
        var band = new Dictionary<PawnId, (double Min, double Max)>();
        foreach (var p in sleeping.People.Concat(sleeping.Slaves))
        {
            var mk = p.ComfortableMin.TryGetValue(out var min);
            var xk = p.ComfortableMax.TryGetValue(out var max);
            if (mk && xk)
            {
                band[p.Id] = (min, max);
            }
        }
        var unhoused = 0;
        foreach (var t in ordered)
        {
            if (t.Kind == SleepingNeedKind.UseNeeded || guests.Contains(t.Pawn))
            {
                continue;
            }
            needsBed = true;
            couple = couple || t.Partner.Value != "";
            unhoused++;
            if (!band.TryGetValue(t.Pawn, out var b))
            {
                return choice with { Unhoused = unhoused, Method = SleepingMethod.Unknown, Missing = "sleeper_comfort_range" };
            }
            lo = Math.Max(lo, b.Min);
            hi = Math.Min(hi, b.Max);
        }
        choice = choice with { Unhoused = unhoused };
        if (!needsBed)
        {
            return choice with { Method = SleepingMethod.NoDemand };
        }
        var facility = Facility(RoomRole.Bedroom);
        if (!r.Rooms.TryGetValue(out var rooms))
        {
            return choice with { Method = SleepingMethod.Unknown, Missing = "rooms" };
        }
        var cells = new List<Cell>();
        foreach (var room in rooms.Rooms)
        {
            if (!room.Role.TryGetValue(out var role))
            {
                return choice with { Method = SleepingMethod.Unknown, Missing = "room_role" };
            }
            if (!facility.Hosts(role))
            {
                continue;
            }
            if (!room.Temperature.TryGetValue(out var temperature))
            {
                return choice with { Method = SleepingMethod.Unknown, Missing = "room_temperature" };
            }
            if (temperature < lo || temperature > hi)
            {
                continue;
            }
            cells.AddRange(room.Cells);
        }
        var (definition, method) = SleepingDefinition(r.Furniture, r.Definitions, r.Stocked, couple, false);
        choice = choice with { Cells = EquatableArray.From(cells), Definition = definition, Method = method };
        if (method == SleepingMethod.Unknown)
        {
            choice = choice with { Missing = "bed_definitions" };
        }
        return choice;
    }

    /// <summary>
    /// The first waiting slave with nothing to be assigned and the lowest-ID vacant colonist bed that would
    /// suit it set for slaves: a standing bed no waiting colonist can take (assignment already ran), so
    /// marking it costs no colonist a bed. None leaves the build path to stage one more bed, which a later
    /// review marks.
    /// </summary>
    private static (PawnId Pawn, string Bed, bool Found) SlaveBedToMark(IReadOnlyList<SleepingTarget> targets, SleepingObservation sleeping)
    {
        var slaves = new Dictionary<PawnId, SleepingPerson>();
        foreach (var p in sleeping.Slaves)
        {
            slaves[p.Id] = p;
        }
        var beds = sleeping.Beds.OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
        foreach (var t in targets)
        {
            if (!slaves.TryGetValue(t.Pawn, out var p) || t.Kind == SleepingNeedKind.UseNeeded || t.Available.Count != 0)
            {
                continue;
            }
            var lk = p.ComfortableMin.TryGetValue(out var lo);
            var hk = p.ComfortableMax.TryGetValue(out var hi);
            if (!lk || !hk)
            {
                continue;
            }
            foreach (var b in beds)
            {
                b.Humanlike.TryGetValue(out var human);
                var mk = b.Medical.TryGetValue(out var medical);
                var pk = b.Prisoners.TryGetValue(out var prisoner);
                b.Roofed.TryGetValue(out var roof);
                b.RestEffectiveness.TryGetValue(out var rest);
                var tk = b.Temperature.TryGetValue(out var temperature);
                if (!human || !mk || medical || !pk || prisoner || b.Slaves || !roof || b.Owners.Count != 0 || b.Definition.Value == SleepingSpotDefinition
                    || rest <= 0 || !tk || temperature < lo || temperature > hi || !b.AccessibleTo.Contains(p.Id))
                {
                    continue;
                }
                return (p.Id, b.Id, true);
            }
        }
        return (default, "", false);
    }
}
