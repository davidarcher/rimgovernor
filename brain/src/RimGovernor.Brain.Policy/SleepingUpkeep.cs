namespace RimGovernor.Brain.Policy;

public sealed record SleepingPerson
{
    public PawnId Id { get; init; }

    public Fact<string> OwnedBed { get; init; }

    public Fact<double> ComfortableMin { get; init; }

    public Fact<double> ComfortableMax { get; init; }

    /// <summary>The lover, spouse and fiance relations to living pawns on the same map.</summary>
    public EquatableArray<PawnId> Partners { get; init; }

    /// <summary>The native willingness (ideo precepts) to share a bed with every partner; true without Ideology.</summary>
    public Fact<bool> BedSharingAllowed { get; init; }

    /// <summary>The most senior royal title; null without one or without Royalty.</summary>
    public RoyalTitle? Title { get; init; }
}

public sealed record SleepingBed
{
    public string Id { get; init; } = "";

    public Resource Definition { get; init; }

    public Fact<bool> Humanlike { get; init; }

    public Fact<bool> Medical { get; init; }

    public Fact<bool> Prisoners { get; init; }

    public Fact<bool> Roofed { get; init; }

    /// <summary>Building_Bed.ForSlaves; false without Ideology.</summary>
    public bool Slaves { get; init; }

    /// <summary>The def's bed_crib flag: native never makes a crib a prisoner bed.</summary>
    public bool Crib { get; init; }

    public Fact<double> RestEffectiveness { get; init; }

    public Fact<double> Temperature { get; init; }

    public EquatableArray<PawnId> Owners { get; init; }

    public EquatableArray<PawnId> Users { get; init; }

    public EquatableArray<PawnId> AccessibleTo { get; init; }

    /// <summary>The native room id (RoomQuality.ID).</summary>
    public Fact<string> Room { get; init; }

    /// <summary>The native QualityCategory name, unknown for a bed without quality.</summary>
    public Fact<string> Quality { get; init; }

    /// <summary>The native stuff defName, unknown for a stuffless bed.</summary>
    public Fact<string> Stuff { get; init; }

    /// <summary>The bed's position (its head cell).</summary>
    public Cell Cell { get; init; }
}

public sealed record SleepingObservation
{
    public int Colonists { get; init; }

    public EquatableArray<SleepingPerson> People { get; init; }

    /// <summary>
    /// The colony's slaves (Ideology), outside Colonists and People: each needs a bed set for slaves
    /// (Building_Bed.ForSlaves), which a free colonist may never own. They are never partnered.
    /// </summary>
    public EquatableArray<SleepingPerson> Slaves { get; init; }

    /// <summary>
    /// The hosted guests (HostFaction is the player, quest lodgers included), outside Colonists and People:
    /// each is offered a vacant suitable bed through Assign, a titled one the beds fitting its title first. A
    /// guest never causes a bed to be built.
    /// </summary>
    public EquatableArray<SleepingPerson> Guests { get; init; }

    public EquatableArray<SleepingBed> Beds { get; init; }

    /// <summary>The room quality census, unknown when its section is.</summary>
    public Fact<EquatableArray<UpkeepRoom>> Rooms { get; init; }

    /// <summary>
    /// Bed's native availability: while it is known false a bedroll is a suitable bed, and while it is true an
    /// upgrade target. Unknown keeps a bedroll suitable. A spot is never suitable.
    /// </summary>
    public Fact<bool> BedBuildable { get; init; }
}

public sealed record SleepingUse(PawnId Pawn, string Bed, Tick Tick);

public sealed record SleepingHistory
{
    public EquatableArray<SleepingUse> Uses { get; init; }

    public void Validate()
    {
        var seen = new HashSet<PawnId>();
        foreach (var use in Uses)
        {
            if (!PolicyFunctions.FoodId(use.Pawn.Value) || !PolicyFunctions.FoodId(use.Bed) || use.Tick.Value < 0 || !seen.Add(use.Pawn))
            {
                throw new DomainException("invalid sleeping use history");
            }
        }
    }
}

public enum SleepingNeedKind
{
    [WireName("upgrade")]
    Upgrade,

    [WireName("unsafe")]
    Unsafe,

    [WireName("use")]
    UseNeeded,

    /// <summary>The pawn owns a suitable bed, but not one shared with its couple partner (the same bed, or a bed in the partner's room).</summary>
    [WireName("share")]
    Share,
}

public sealed record SleepingTarget
{
    public PawnId Pawn { get; init; }

    public SleepingNeedKind Kind { get; init; }

    public string PreviousBed { get; init; } = "";

    /// <summary>Lists assignable beds in preference order.</summary>
    public EquatableArray<string> Available { get; init; }

    /// <summary>The pawn's couple partner, empty when single.</summary>
    public PawnId Partner { get; init; }
}

public sealed record SleepingReview
{
    public SleepingHistory History { get; init; } = new();

    public Fact<EquatableArray<SleepingTarget>> Targets { get; init; }

    public Fact<bool> Recovered()
    {
        if (!Targets.TryGetValue(out var rows))
        {
            return Fact.Unknown<bool>();
        }
        return Fact.Known(rows.Count == 0);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The bed definitions two partners can own together.</summary>
    public static readonly IReadOnlySet<Resource> SleepingDoubleBeds = new HashSet<Resource>
    {
        new("DoubleBed"),
        new("RoyalBed"),
        new(SleepingCoupleBedrollDefinition),
    };

    /// <summary>
    /// Maps each pawn to its couple partner: the lowest-ID love partner that names it back, both willing to
    /// share a bed. Unknown willingness pairs nobody, so non-partners are never put together.
    /// </summary>
    internal static Dictionary<PawnId, PawnId> SleepingCouples(IEnumerable<SleepingPerson> people)
    {
        var list = people.ToList();
        var byId = new Dictionary<PawnId, SleepingPerson>();
        foreach (var p in list)
        {
            byId[p.Id] = p;
        }
        static bool Willing(SleepingPerson p) => p.BedSharingAllowed.TryGetValue(out var w) && w;
        static bool Names(SleepingPerson p, PawnId id) => p.Partners.Contains(id);
        var couples = new Dictionary<PawnId, PawnId>();
        foreach (var p in list)
        {
            var partners = p.Partners.ToList();
            partners.Sort();
            foreach (var id in partners)
            {
                if (byId.TryGetValue(id, out var q) && id != p.Id && Willing(p) && Willing(q) && Names(q, p.Id))
                {
                    couples[p.Id] = id;
                    break;
                }
            }
        }
        // Keep only reciprocal choices (a pawn with two partners picks one).
        foreach (var (p, q) in couples.ToList())
        {
            if (!couples.TryGetValue(q, out var back) || back != p)
            {
                couples.Remove(p);
            }
        }
        return couples;
    }

    /// <summary>
    /// Use is retained only for the same pawn and exact bed identity. The journal resets history when the
    /// colony/load/map changes or time moves backwards. Throws DomainException on an invalid census or history.
    /// </summary>
    public static SleepingReview ReviewSleeping(Fact<SleepingObservation> observed, SleepingHistory previous, Tick tick)
    {
        var r = new SleepingReview { History = new SleepingHistory { Uses = previous.Uses } };
        var invalid = new DomainException("invalid sleeping census");
        previous.Validate();
        if (tick.Value < 0)
        {
            throw invalid;
        }
        var uses = new Dictionary<PawnId, SleepingUse>();
        foreach (var use in previous.Uses)
        {
            if (use.Tick.Value > tick.Value)
            {
                throw invalid;
            }
            uses[use.Pawn] = use;
        }
        if (!observed.TryGetValue(out var v))
        {
            return r;
        }
        if (v.Colonists < 0)
        {
            throw invalid;
        }
        if (v.People.Count != v.Colonists)
        {
            return r;
        }
        static bool Finite(Fact<double> f) => f.TryGetValue(out var n) && !double.IsNaN(n) && !double.IsInfinity(n);
        var people = new HashSet<PawnId>();
        var complete = true;
        // Slaves sleep only in beds set for slaves, colonists never in one.
        var everyone = v.People.Concat(v.Slaves).Concat(v.Guests).ToList();
        var slave = new HashSet<PawnId>();
        var guest = new HashSet<PawnId>();
        foreach (var p in v.Guests)
        {
            guest.Add(p.Id);
        }
        foreach (var p in v.Slaves)
        {
            slave.Add(p.Id);
        }
        foreach (var p in everyone)
        {
            if (!FoodId(p.Id.Value) || !people.Add(p.Id))
            {
                throw invalid;
            }
            var bk = p.OwnedBed.TryGetValue(out var bed);
            if (bk && bed != "" && !FoodId(bed!))
            {
                throw invalid;
            }
            if (!bk || !Finite(p.ComfortableMin) || !Finite(p.ComfortableMax))
            {
                complete = false;
                continue;
            }
            p.ComfortableMin.TryGetValue(out var lo);
            p.ComfortableMax.TryGetValue(out var hi);
            if (lo > hi)
            {
                throw invalid;
            }
        }
        var beds = new Dictionary<string, SleepingBed>(StringComparer.Ordinal);
        foreach (var b in v.Beds)
        {
            if (!FoodId(b.Id) || !ValidResource(b.Definition))
            {
                throw invalid;
            }
            if (!beds.TryAdd(b.Id, b))
            {
                throw invalid;
            }
            if (!Finite(b.RestEffectiveness) || !Finite(b.Temperature))
            {
                complete = false;
            }
            foreach (var f in new[] { b.Humanlike, b.Medical, b.Prisoners, b.Roofed })
            {
                if (!f.TryGetValue(out _))
                {
                    complete = false;
                }
            }
            foreach (var ids in new[] { b.Owners, b.Users, b.AccessibleTo })
            {
                var seen = new HashSet<PawnId>();
                foreach (var id in ids)
                {
                    if (!FoodId(id.Value) || !seen.Add(id))
                    {
                        throw invalid;
                    }
                }
            }
        }
        if (!complete)
        {
            return r;
        }
        var couples = SleepingCouples(v.People);
        var ownedBy = new Dictionary<PawnId, string>();
        foreach (var p in v.People)
        {
            p.OwnedBed.TryGetValue(out var ob);
            ownedBy[p.Id] = ob ?? "";
        }
        (string Id, bool Known) RoomOf(string bed)
        {
            if (!beds.TryGetValue(bed, out var b))
            {
                return ("", false);
            }
            var known = b.Room.TryGetValue(out var id);
            return (id ?? "", known && id != "");
        }
        // shared: the bed holds nobody but p and its partner, and a couple sleeps together (one bed, or one room).
        bool Shared(SleepingBed b, PawnId p)
        {
            var coupled = couples.TryGetValue(p, out var q);
            foreach (var o in b.Owners)
            {
                if (o != p && (!coupled || o != q))
                {
                    return false;
                }
            }
            if (!coupled || b.Owners.Contains(q))
            {
                return true;
            }
            var (mine, mk) = RoomOf(b.Id);
            var (theirs, tk) = RoomOf(ownedBy.GetValueOrDefault(q) ?? "");
            return mk && tk && mine == theirs;
        }
        var targets = new List<SleepingTarget>();
        var next = new List<SleepingUse>();
        var ordered = everyone.OrderBy(p => p.Id).ToList();
        foreach (var p in ordered)
        {
            p.ComfortableMin.TryGetValue(out var lo);
            p.ComfortableMax.TryGetValue(out var hi);
            bool Safe(SleepingBed b)
            {
                b.Humanlike.TryGetValue(out var human);
                b.Medical.TryGetValue(out var medical);
                b.Prisoners.TryGetValue(out var prisoner);
                b.Roofed.TryGetValue(out var roof);
                b.Temperature.TryGetValue(out var temperature);
                return human && !medical && !prisoner && b.Slaves == slave.Contains(p.Id) && roof && b.AccessibleTo.Contains(p.Id) && temperature >= lo && temperature <= hi;
            }
            bool Suitable(SleepingBed b)
            {
                b.RestEffectiveness.TryGetValue(out var rest);
                return Safe(b) && SleepingRung(b.Definition, v.BedBuildable) && rest > 0;
            }
            p.OwnedBed.TryGetValue(out var ownedId);
            var bedId = ownedId ?? "";
            var exists = beds.TryGetValue(bedId, out var owned);
            if (guest.Contains(p.Id))
            {
                // A guest is only offered a vacant bed: housed in a suitable bed it owns, or with none to take, it needs nothing from us.
                if (exists && Suitable(owned!) && owned!.Owners.Contains(p.Id))
                {
                    continue;
                }
                var availableGuest = GuestBeds(v, p, Suitable);
                if (availableGuest.Count == 0)
                {
                    continue;
                }
                var guestKind = SleepingNeedKind.Upgrade;
                if (exists && !Safe(owned!))
                {
                    guestKind = SleepingNeedKind.Unsafe;
                }
                targets.Add(new SleepingTarget { Pawn = p.Id, Kind = guestKind, PreviousBed = bedId, Available = EquatableArray.From(availableGuest) });
                continue;
            }
            var kind = SleepingNeedKind.Upgrade;
            var coupled = couples.TryGetValue(p.Id, out var q);
            // A double bed only p's partner may join: p waits for the partner's assignment rather than moving out of it.
            var waiting = exists && coupled && Suitable(owned!) && owned!.Owners.Contains(p.Id) && owned.Owners.Count == 1
                && SleepingDoubleBeds.Contains(owned.Definition) && !Shared(owned, p.Id);
            if (exists && Suitable(owned!) && owned!.Owners.Contains(p.Id) && (Shared(owned, p.Id) || waiting))
            {
                if (waiting)
                {
                    if (uses.TryGetValue(p.Id, out var waitUse))
                    {
                        next.Add(waitUse);
                    }
                    targets.Add(new SleepingTarget { Pawn = p.Id, Kind = SleepingNeedKind.UseNeeded, PreviousBed = bedId, Partner = q });
                    continue;
                }
                if (owned.Users.Contains(p.Id))
                {
                    uses[p.Id] = new SleepingUse(p.Id, owned.Id, tick);
                }
                if (uses.TryGetValue(p.Id, out var use) && use.Bed == owned.Id)
                {
                    next.Add(use);
                    continue;
                }
                kind = SleepingNeedKind.UseNeeded;
            }
            else if (exists && !Safe(owned!))
            {
                kind = SleepingNeedKind.Unsafe;
            }
            else if (exists && Suitable(owned!) && owned!.Owners.Contains(p.Id) && coupled)
            {
                kind = SleepingNeedKind.Share;
            }
            // Keep previous exact-bed proof while temporarily unsafe; it cannot clear a need unless that same
            // assignment is observed suitable again.
            if (uses.TryGetValue(p.Id, out var kept))
            {
                next.Add(kept);
            }
            var available = new List<string>();
            if (coupled)
            {
                // A double bed empty or held by the partner comes first, then a vacant bed in the partner's room.
                var (theirs, tk) = RoomOf(ownedBy.GetValueOrDefault(q) ?? "");
                var doubles = new List<string>();
                var singles = new List<string>();
                foreach (var b in v.Beds)
                {
                    if (b.Id == bedId || !Suitable(b) || b.Owners.Contains(p.Id))
                    {
                        continue;
                    }
                    var partnerOnly = b.Owners.Count == 0 || (b.Owners.Count == 1 && b.Owners[0] == q);
                    if (SleepingDoubleBeds.Contains(b.Definition) && partnerOnly)
                    {
                        doubles.Add(b.Id);
                    }
                    else
                    {
                        var (mine, mk) = RoomOf(b.Id);
                        if (b.Owners.Count == 0 && tk && mk && mine == theirs)
                        {
                            singles.Add(b.Id);
                        }
                    }
                }
                doubles.Sort(StringComparer.Ordinal);
                singles.Sort(StringComparer.Ordinal);
                available = [.. doubles, .. singles];
            }
            // Without a suitable bed at all, any vacant bed beats none.
            if (available.Count == 0 && kind != SleepingNeedKind.Share)
            {
                foreach (var b in v.Beds)
                {
                    if (Suitable(b) && b.Owners.Count == 0)
                    {
                        available.Add(b.Id);
                    }
                }
                available.Sort(StringComparer.Ordinal);
            }
            targets.Add(new SleepingTarget { Pawn = p.Id, Kind = kind, PreviousBed = bedId, Available = EquatableArray.From(available), Partner = q });
        }
        return new SleepingReview
        {
            History = new SleepingHistory { Uses = EquatableArray.From(next) },
            Targets = Known(EquatableArray.From(targets)),
        };
    }

    /// <summary>
    /// Whether a bed of this definition is a suitable bed on the ladder: a bedroll only while Bed is
    /// unavailable, a spot never, so a spot owner stays a bedroom and bedroll target.
    /// </summary>
    internal static bool SleepingRung(Resource definition, Fact<bool> bed)
    {
        var known = bed.TryGetValue(out var buildable);
        switch (definition.Value)
        {
            case SleepingSpotDefinition:
                return false;
            case SleepingBedrollDefinition:
            case SleepingCoupleBedrollDefinition:
                return !known || !buildable;
        }
        return true;
    }

    /// <summary>
    /// The vacant beds suitable for guest p, best first. A titled guest's beds come first by how many of its
    /// title's bedroom requirements they meet: the title's bed, a room of the minimum area and
    /// impressiveness. The rest, and an untitled guest's, go by bed ID.
    /// </summary>
    private static List<string> GuestBeds(SleepingObservation v, SleepingPerson p, Func<SleepingBed, bool> suitable)
    {
        var rooms = new Dictionary<string, UpkeepRoom>(StringComparer.Ordinal);
        if (v.Rooms.TryGetValue(out var census))
        {
            foreach (var r in census)
            {
                rooms[r.Id] = r;
            }
        }
        int Met(SleepingBed b)
        {
            var t = p.Title;
            if (t is null)
            {
                return 0;
            }
            var n = 0;
            foreach (var thing in t.BedroomThings)
            {
                if (IsReplacementBed(thing.AnyOf) && thing.AnyOf.Contains(b.Definition))
                {
                    n++;
                }
            }
            b.Room.TryGetValue(out var id);
            var room = rooms.GetValueOrDefault(id ?? "") ?? new UpkeepRoom();
            if (room.Cells.TryGetValue(out var cells) && cells >= t.BedroomMinArea)
            {
                n++;
            }
            if (room.Quality.TryGetValue(out var q) && q.Impressiveness >= t.BedroomMinImpressiveness)
            {
                n++;
            }
            return n;
        }
        return v.Beds
            .Where(b => b.Owners.Count == 0 && suitable(b))
            .OrderByDescending(Met)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .Select(b => b.Id)
            .ToList();
    }
}
