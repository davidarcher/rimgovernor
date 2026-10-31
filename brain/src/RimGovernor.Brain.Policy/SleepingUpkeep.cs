
namespace RimGovernor.Brain.Policy;

/// <summary>The observed person and the title requirements derived from the definition catalog.</summary>
public sealed record SleepingPerson
{
    public required RimGovernor.Brain.GameFacts.Observations.UpkeepPerson Native { get; init; }
    public required RimGovernor.Brain.GameFacts.Observations.PawnState Pawn { get; init; }
    public PawnId Id => Native.Pawn;
    public string? OwnedBed => Pawn.OwnedBed?.Id;
    public double ComfortableMin => Pawn.Settings.Body.ComfortableMinC;
    public double ComfortableMax => Pawn.Settings.Body.ComfortableMaxC;
    public EquatableArray<PawnId> Partners => Native.Partners;
    public bool BedSharingAllowed => Native.BedSharingAllowed;
    public RoyalTitle? Title { get; init; }

    /// <summary>
    /// A newborn or baby (Biotech developmental stage): it sleeps in a crib, never a regular bed, so its slot is crib
    /// demand (ChildRoomsRules.ChildRoomNeeds' nursery), not a sleeping target. A pawn without Biotech is no baby.
    /// </summary>
    public bool Baby => Pawn.Biotech is RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Active { Facts.DevelopmentalStage: RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Newborn or RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Baby };
}

/// <summary>The actual upkeep and building rows, joined with catalog bed properties.</summary>
public sealed record SleepingBed
{
    public required RimGovernor.Brain.GameFacts.Observations.UpkeepBed Native { get; init; }
    public required RimGovernor.Brain.GameFacts.Observations.BuildingState Building { get; init; }
    public string Id => Native.Id;
    public int Slots => Native.Slots;
    public Resource Definition => new(Building.Building.DefName);
    public bool Humanlike { get; init; }
    public bool Crib { get; init; }
    public double RestEffectiveness { get; init; }
    public bool Medical => Native.Medical;
    public bool Prisoners => Native.Prisoners;
    public bool Roofed => Building.Roofed;
    public bool Slaves => Native.ForSlaves;
    public double Temperature => Native.TemperatureC;
    public EquatableArray<PawnId> Owners => Native.Owners;
    public EquatableArray<PawnId> Users => Native.Users;
    public EquatableArray<PawnId> AccessibleTo => Native.AccessibleTo;
    public string Room => Native.Room;
    public RimGovernor.Brain.GameFacts.Observations.BedQuality Quality => Native.Quality;
    public string? Stuff => Building.Stuff;
    public Cell Cell => Building.Building.Place is RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap at ? at.Cell
        : throw new DomainException("sleeping building is not on the map");
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
    /// The hosted guests (HostFaction is the player, quest lodgers included), outside Colonists and People and
    /// never workers: each is offered a vacant suitable bed through Assign after every colonist, a titled one the
    /// beds fitting its title first, and a guest with none to take has a bed built for it like a colonist.
    /// </summary>
    public EquatableArray<SleepingPerson> Guests { get; init; }

    /// <summary>The hosted people outside Colonists who sleep in a regular bed: the slaves and the guests (prisoners sleep in prison beds).</summary>
    public int Hosted => Slaves.Count + Guests.Count;

    /// <summary>
    /// The slaves the colony's colonist count (ColonyFactsSnapshot.ColonistCount, native IsFreeColonist) also counts:
    /// the secure slaves. They are Slaves here, never People, so a sum of the colonist count and Hosted counts them once
    /// only after taking these off.
    /// </summary>
    public int CensusSlaves { get; init; }

    /// <summary>
    /// The hosted people beside the colonist count who need a regular bed slot: Hosted less its babies (a crib each,
    /// <see cref="HostedBabies"/>) and less the secure slaves the colonist count already holds.
    /// </summary>
    public int HostedSleepers => Math.Max(0, Slaves.Concat(Guests).Count(p => !p.Baby) - CensusSlaves);

    /// <summary>The hosted slaves' and guests' babies: crib demand beside the colonists' babies.</summary>
    public int HostedBabies => Slaves.Concat(Guests).Count(p => p.Baby);

    public EquatableArray<SleepingBed> Beds { get; init; }

    /// <summary>The room quality census: every room holding a colonist bed or carrying a common role.</summary>
    public EquatableArray<UpkeepRoom> Rooms { get; init; }

    /// <summary>
    /// Bed's native availability: while it is false a bedroll is a suitable bed, and while it is true an
    /// upgrade target. A spot is never suitable.
    /// </summary>
    public bool BedBuildable { get; init; }
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
            if (!FoodForecastRules.FoodId(use.Pawn.Value) || !FoodForecastRules.FoodId(use.Bed) || use.Tick.Value < 0 || !seen.Add(use.Pawn))
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

    public EquatableArray<SleepingTarget> Targets { get; init; }

    /// <summary>Every colonist, slave and hosted guest owns, and was seen using, a suitable bed.</summary>
    public bool Recovered() => Targets.Count == 0;
}

public static partial class SleepingRules
{
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
        static bool Willing(SleepingPerson p) => p.BedSharingAllowed;
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
    /// <summary>
    /// Whether a bed of this class takes <paramref name="who"/> (RestUtility.CanUseBedNow's ForPrisoners and ForSlaves checks and
    /// CanUseBedEver's humanlike check): an animal only an animal bed; a prisoner only a prison bed; a slave only a slave bed that
    /// is no prison bed; a colonist or guest only a bed for neither. The one legality core of sleeping slots
    /// (<see cref="CompatibleSlot"/>) and care beds (<see cref="CareSlot"/>); medical use and body size are the caller's.
    /// </summary>
    public static bool LegalBed(bool humanlike, bool prisoners, bool slaves, RimGovernor.Brain.GameFacts.Observations.BedOccupant who) =>
        who == RimGovernor.Brain.GameFacts.Observations.BedOccupant.Animal ? !humanlike
        : who == RimGovernor.Brain.GameFacts.Observations.BedOccupant.Prisoner ? humanlike && prisoners
        : who == RimGovernor.Brain.GameFacts.Observations.BedOccupant.Slave ? humanlike && !prisoners && slaves
        : humanlike && !prisoners && !slaves;

    /// <summary>
    /// Whether <paramref name="b"/> is a slot <paramref name="pawn"/> may legally sleep in and reach: a regular humanlike
    /// bed (no crib or medical bed) that <see cref="LegalBed"/> gives a colonist or a slave (a colonist never in a slave or prison
    /// bed, a slave only in a slave bed), that the pawn can reach. Occupancy is the census's: capacity is these slots, never a
    /// second count; a medical bed is never a free sleeping slot.
    /// </summary>
    public static bool CompatibleSlot(SleepingBed b, PawnId pawn, bool slave) =>
        !b.Crib && !b.Medical && LegalBed(b.Humanlike, b.Prisoners, b.Slaves, slave ? RimGovernor.Brain.GameFacts.Observations.BedOccupant.Slave : RimGovernor.Brain.GameFacts.Observations.BedOccupant.Colonist) && b.AccessibleTo.Contains(pawn);

    /// <summary>
    /// The state of <paramref name="bed"/> as the place <paramref name="patient"/> is cared in (task 4.1): Ready when it lies
    /// there now, or the bed is a medical bed <see cref="LegalBed"/> gives it with a slot free or held by the patient and the
    /// native's own CanUseBedNow passes; else the first missing prerequisite: IllegalBed, BedTaken (every slot held by another
    /// occupant), ConversionRequired (a legal free bed that is not medical; converting it is a separate decision) or
    /// NativeRefused (CanUseBedNow fails, a prison bed outside a prison cell for one).
    /// </summary>
    public static CareBedState CareSlot(RimGovernor.Brain.GameFacts.Observations.SupportCareBed bed, RimGovernor.Brain.GameFacts.Observations.SupportCarePatient patient)
    {
        if (bed.Movement is RimGovernor.Brain.GameFacts.Observations.CareMovement.AlreadyHere) return CareBedState.Ready;
        if (!LegalBed(bed.Humanlike, bed.Prisoners, bed.Slaves, patient.Occupant)) return CareBedState.IllegalBed;
        if (!bed.Occupants.Contains(patient.Patient) && bed.Occupants.Count >= bed.Slots) return CareBedState.BedTaken;
        if (!bed.Medical) return CareBedState.ConversionRequired;
        return bed.UsableNow ? CareBedState.Ready : CareBedState.NativeRefused;
    }

    /// <summary>
    /// The sleepers a medical bed holds while their care lasts (tasks 4.1 and 4.2): a pawn who owns no regular slot and lies in a
    /// medical bed it may sleep in by class, or who seeks medical rest (HealthAIUtility.ShouldSeekMedicalRest) and can reach such a
    /// bed with a slot nobody lies in, each free slot holding one. The held bed is its slot while care lasts, so it is no free slot
    /// for another sleeper and no second slot is wanted for it: a patient whose own bed was converted counts once. Once care ends
    /// it seeks no medical rest, is held by nothing and wants a sleeping slot again, which the post-care review prices.
    /// </summary>
    public static IReadOnlySet<PawnId> HeldInMedicalBeds(SleepingObservation v, IEnumerable<SleepingPerson> who, bool slave)
    {
        var occupant = slave ? RimGovernor.Brain.GameFacts.Observations.BedOccupant.Slave : RimGovernor.Brain.GameFacts.Observations.BedOccupant.Colonist;
        bool Legal(SleepingBed b) => b.Medical && !b.Crib && LegalBed(b.Humanlike, b.Prisoners, b.Slaves, occupant);
        var held = new HashSet<PawnId>();
        var seeking = new List<SleepingPerson>();
        foreach (var p in who)
        {
            if (v.Beds.Any(b => b.Owners.Contains(p.Id) && CompatibleSlot(b, p.Id, slave))) continue;
            if (v.Beds.Any(b => Legal(b) && b.Users.Contains(p.Id))) held.Add(p.Id);
            else if (p.Pawn.Health.ShouldSeekMedicalRest) seeking.Add(p);
        }
        var free = v.Beds.Where(Legal).ToDictionary(b => b.Id, b => b.Slots - b.Users.Count, StringComparer.Ordinal);
        foreach (var p in seeking.OrderBy(p => p.Id.Value, StringComparer.Ordinal))
        {
            var bed = v.Beds.Where(b => Legal(b) && b.AccessibleTo.Contains(p.Id) && free[b.Id] > 0).OrderBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault();
            if (bed is null) continue;
            free[bed.Id]--;
            held.Add(p.Id);
        }
        return held;
    }

    /// <summary>
    /// The sleepers short of a compatible reachable slot (<see cref="CompatibleSlot"/>), per bed class: the colonists and
    /// guests (babies sleep in cribs, the nursery's demand) against the regular beds one of them can reach, the slaves
    /// against the beds set for slaves one of them can reach. A slot of the other class is never capacity. A sleeper a medical
    /// bed holds (<see cref="HeldInMedicalBeds"/>) is in its slot and wants no other.
    /// </summary>
    public static (int Regular, int Slave) SlotShortfall(SleepingObservation v)
    {
        var people = v.People.Concat(v.Guests).Where(p => !p.Baby).ToList();
        var bonded = v.Slaves.Where(p => !p.Baby).ToList();
        var heldPeople = HeldInMedicalBeds(v, people, slave: false); var heldSlaves = HeldInMedicalBeds(v, bonded, slave: true);
        var regular = people.Where(p => !heldPeople.Contains(p.Id)).ToList();
        var slaves = bonded.Where(p => !heldSlaves.Contains(p.Id)).ToList();
        int Slots(List<SleepingPerson> who, bool slave) => v.Beds.Where(b => who.Any(p => CompatibleSlot(b, p.Id, slave))).Sum(b => b.Slots);
        return (Math.Max(0, regular.Count - Slots(regular, false)), Math.Max(0, slaves.Count - Slots(slaves, true)));
    }

    public static SleepingReview ReviewSleeping(SleepingObservation v, SleepingHistory previous, Tick tick)
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
        if (v.Colonists < 0 || v.People.Count != v.Colonists)
        {
            throw invalid;
        }
        var people = new HashSet<PawnId>();
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
            if (!FoodForecastRules.FoodId(p.Id.Value) || !people.Add(p.Id))
            {
                throw invalid;
            }
            if (p.OwnedBed is { } ownedBed && !FoodForecastRules.FoodId(ownedBed))
            {
                throw invalid;
            }
            if (!double.IsFinite(p.ComfortableMin) || !double.IsFinite(p.ComfortableMax) || p.ComfortableMin > p.ComfortableMax)
            {
                throw invalid;
            }
        }
        var beds = new Dictionary<string, SleepingBed>(StringComparer.Ordinal);
        foreach (var b in v.Beds)
        {
            if (!FoodForecastRules.FoodId(b.Id) || !AdmissionRules.ValidResource(b.Definition))
            {
                throw invalid;
            }
            if (!beds.TryAdd(b.Id, b))
            {
                throw invalid;
            }
            if (!double.IsFinite(b.Temperature))
            {
                throw invalid;
            }
            foreach (var ids in new[] { b.Owners, b.Users, b.AccessibleTo })
            {
                var seen = new HashSet<PawnId>();
                foreach (var id in ids)
                {
                    if (!FoodForecastRules.FoodId(id.Value) || !seen.Add(id))
                    {
                        throw invalid;
                    }
                }
            }
        }
        var couples = SleepingCouples(v.People);
        var ownedBy = new Dictionary<PawnId, string>();
        foreach (var p in v.People)
        {
            ownedBy[p.Id] = p.OwnedBed ?? "";
        }
        (string Id, bool Known) RoomOf(string bed)
        {
            if (!beds.TryGetValue(bed, out var b))
            {
                return ("", false);
            }
            return (b.Room, b.Room != "");
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
            // A baby's slot is a crib the nursery need sizes, never a regular bed.
            if (p.Baby)
            {
                continue;
            }
            var (lo, hi) = (p.ComfortableMin, p.ComfortableMax);
            bool Safe(SleepingBed b) => CompatibleSlot(b, p.Id, slave.Contains(p.Id)) && b.Roofed && b.Temperature >= lo && b.Temperature <= hi;
            bool Suitable(SleepingBed b) => Safe(b) && SleepingRung(b.Definition, v.BedBuildable) && b.RestEffectiveness > 0;
            var bedId = p.OwnedBed ?? "";
            var exists = beds.TryGetValue(bedId, out var owned);
            if (guest.Contains(p.Id))
            {
                // A guest is housed like a colonist, by observed use of a suitable bed it owns; without one it stays a
                // target whether or not a vacant bed stands, so the Need holds and a bed is built for it.
                if (exists && Suitable(owned!) && owned!.Owners.Contains(p.Id))
                {
                    if (owned.Users.Contains(p.Id))
                    {
                        uses[p.Id] = new SleepingUse(p.Id, owned.Id, tick);
                    }
                    if (uses.TryGetValue(p.Id, out var guestUse) && guestUse.Bed == owned.Id)
                    {
                        next.Add(guestUse);
                        continue;
                    }
                    targets.Add(new SleepingTarget { Pawn = p.Id, Kind = SleepingNeedKind.UseNeeded, PreviousBed = bedId });
                    continue;
                }
                if (uses.TryGetValue(p.Id, out var guestKept))
                {
                    next.Add(guestKept);
                }
                var guestKind = SleepingNeedKind.Upgrade;
                if (exists && !Safe(owned!))
                {
                    guestKind = SleepingNeedKind.Unsafe;
                }
                targets.Add(new SleepingTarget { Pawn = p.Id, Kind = guestKind, PreviousBed = bedId, Available = EquatableArray.From(GuestBeds(v, p, Suitable)) });
                continue;
            }
            var kind = SleepingNeedKind.Upgrade;
            var coupled = couples.TryGetValue(p.Id, out var q);
            // A double bed only p's partner may join: p waits for the partner's assignment rather than moving out of it.
            var waiting = exists && coupled && Suitable(owned!) && owned!.Owners.Contains(p.Id) && owned.Owners.Count == 1
                && owned.Slots >= 2 && !Shared(owned, p.Id);
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
                    if (b.Slots >= 2 && partnerOnly)
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
            Targets = EquatableArray.From(targets),
        };
    }

    /// <summary>
    /// Whether a bed of this definition is a suitable bed on the ladder: a bedroll only while Bed is
    /// unavailable, a spot never, so a spot owner stays a bedroom and bedroll target.
    /// </summary>
    internal static bool SleepingRung(Resource definition, bool buildable)
    {
        switch (definition.Value)
        {
            case SleepingSpotDefinition:
                return false;
            case SleepingBedrollDefinition:
            case SleepingCoupleBedrollDefinition:
                return !buildable;
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
        foreach (var r in v.Rooms)
        {
            rooms[r.Id] = r;
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
                if (thing.AnyOf.Contains(b.Definition))
                {
                    n++;
                }
            }
            if (!rooms.TryGetValue(b.Room, out var room))
            {
                return n;
            }
            if (room.Cells >= t.BedroomMinArea)
            {
                n++;
            }
            if (room.Quality.Impressiveness >= t.BedroomMinImpressiveness)
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
