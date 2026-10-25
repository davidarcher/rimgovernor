namespace RimGovernor.Brain.Policy;

// Individual bedrooms. The sleeping planner walks each colonist, a bedless joiner included, into a room of
// their own in the bedroom wing, in the wing's slot order: raise the room's shell, stage one bed in it, then
// move the colonist's ownership there.

/// <summary>The next bedroom step.</summary>
public enum BedroomStepKind
{
    /// <summary>Every colonist owns a bed in a Bedroom-role room or a planned barracks, or no planned bedroom is left to build.</summary>
    None,

    /// <summary>Assign Bed, a vacant bed in a Bedroom-role room (or, with none, in a planned barracks), to Pawn.</summary>
    Move,

    /// <summary>
    /// Room, a planned bedroom with no bed (not standing, or standing empty) or a planned barracks with fewer beds
    /// than its tiled bed slots, is reconciled by the build side (ReconcileRoom) to the plan and the bed template
    /// (BedroomFurnishing). Its state is whatever the diff leaves: no shell or furnish step.
    /// </summary>
    Reconcile,

    /// <summary>
    /// Deconstruct Bed, a vacant day-one stand-in sleeping spot (#3659) at Cells[0], once the beds in enclosed rooms
    /// take everyone without it.
    /// </summary>
    Clear,
}

/// <summary>One bounded step towards individual bedrooms.</summary>
public sealed record BedroomStep
{
    public BedroomStepKind Kind { get; init; }

    public PawnId Pawn { get; init; }

    public string Bed { get; init; } = "";

    public string PreviousBed { get; init; } = "";

    /// <summary>The step's room.</summary>
    public PlannedRoom Room { get; init; } = new();

    /// <summary>
    /// The bedroom step's owed rooms in plan order, <see cref="Room"/> first: the build side falls through a room the
    /// portfolio holds to the next (#3743). Empty for a suite or migration step, which reconciles <see cref="Room"/> alone.
    /// </summary>
    public EquatableArray<PlannedRoom> Owed { get; init; }

    /// <summary>A Clear's one cell.</summary>
    public EquatableArray<Cell> Cells { get; init; }

    public int Unhoused { get; init; }
}

public static partial class SleepingRules
{
    /// <summary>
    /// The bed slots a planned barracks is furnished to: the beds the tiler placed in it (LayoutPlan.Pieces), one bed
    /// each. Null for every other role, which takes the bedroom template's one bed slot.
    /// </summary>
    public static IReadOnlyList<InteriorPiece>? BedSlotsOf(LayoutPlan plan, PlannedRoom room, RoomFurniture furniture)
    {
        if (room.Role != PlannedRole.Barracks)
        {
            return null;
        }
        return plan.Pieces.TryGetValue(room.Id, out var pieces) ? [.. pieces.Where(p => furniture.IsBed(p.Def))] : [];
    }

    /// <summary>The beds standing in the planned barracks, the stand-in spots left out.</summary>
    private static HashSet<string> BarracksBedIds(LayoutPlan plan, RoomObservation rooms, HashSet<string> spots)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedRole.Barracks)
            {
                continue;
            }
            // Census: the barracks' beds are read off its room, whatever the game scores it.
            var (room, ok) = PlannedRoom.CensusRoomIn(r, rooms);
            if (ok)
            {
                output.UnionWith(room!.BedIds().Where(b => !spots.Contains(b)));
            }
        }
        return output;
    }

    private static bool NeverUpgradeOf(IReadOnlyDictionary<string, RoomTarget>? targets, string room) =>
        targets is not null && targets.TryGetValue(room, out var t) && t.NeverUpgrade;

    /// <summary>
    /// Picks the next bedroom step from the plan, the room census and the sleeping census. Move comes first
    /// (it costs nothing), into a vacant bedroom bed, else a vacant bed of a planned barracks; then a room to
    /// reconcile (the first in plan order that owes one: a bedroom with no bed in its enclosed room, a barracks with fewer
    /// beds than its tiled bed slots; the rest follow in BedroomStep.Owed), only while the standing rooms cannot take every colonist still outside one; a room
    /// dug into rock is a shell too, whose builder mines it first. A colonist owning a bed in a planned barracks
    /// is housed, but for the owners past its seats (CrowdedOut). It reports BedroomStepKind.None whenever a fact it needs is unknown. A colonist who owns alone a
    /// room RoomTargets marks NeverUpgrade (an ascetic) counts as housed: the move never takes them from the
    /// plainest room. A vacant bedroom bed goes to the unhoused colonist with the largest priced gain from it
    /// (#3656, ForecastHousing.LargestGainFirst over <paramref name="gains"/>, at its room's stage now); a barracks
    /// bunk, and every move without gains, in id order.
    /// </summary>
    public static BedroomStep NextBedroomStep(
        LayoutPlan plan,
        RoomObservation rooms,
        SleepingObservation sleeping,
        IReadOnlyDictionary<string, RoomTarget>? targets,
        IReadOnlyDictionary<PawnId, TraitEffects>? traits,
        IReadOnlyDictionary<PawnId, double>? pressure,
        BedroomGainFacts? gains = null)
    {
        if (CensusOf(plan, rooms, sleeping, targets, gains, null) is not { } census)
        {
            return new BedroomStep();
        }
        var (beds, people, unhoused) = (census.Beds, census.People, census.Unhoused);
        // A vacant stand-in spot (#3659) is taken down once the indoor beds take everyone without it: the clear
        // leaves an indoor bed for every colonist.
        var indoorBeds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in rooms.Rooms)
        {
            if (room.Enclosed())
            {
                foreach (var id in room.BedIds())
                {
                    if (beds.TryGetValue(id, out var b) && b.Humanlike)
                    {
                        indoorBeds.Add(id);
                    }
                }
            }
        }
        var left = new List<string>();
        foreach (var (id, b) in beds)
        {
            if (b.Definition.Value == SleepingSpotDefinition && b.Owners.Count == 0 && b.Humanlike && !b.Medical
                && indoorBeds.Count - (indoorBeds.Contains(id) ? 1 : 0) >= people.Count)
            {
                left.Add(id);
            }
        }
        if (left.Count > 0)
        {
            left.Sort(StringComparer.Ordinal);
            var b = beds[left[0]];
            return new BedroomStep { Kind = BedroomStepKind.Clear, Bed = b.Id, Cells = EquatableArray.From([b.Cell]), Unhoused = unhoused.Count };
        }
        // Once the standard rooms have nothing to do, a qualifying pawn is walked into a suite.
        BedroomStep SuiteStep()
        {
            var step = RoomQualityRules.NextSuiteStep(plan, rooms, sleeping, RoomQualityRules.SuiteClaims(plan, rooms, sleeping, targets!, traits!, pressure!));
            return step with { Unhoused = unhoused.Count };
        }
        if (unhoused.Count == 0)
        {
            return SuiteStep();
        }
        var (vacant, bunks) = FreeBedsOf(plan, rooms, census);
        // A barracks bed is taken only when no bedroom bed is vacant to the pawn.
        foreach (var (free, priced) in new[] { (vacant, true), (bunks, false) })
        {
            if (NextOpenBed(unhoused, free, beds, priced ? gains : null, null) is { } next)
            {
                next.Pawn.OwnedBed.TryGetValue(out var previous);
                return new BedroomStep { Kind = BedroomStepKind.Move, Pawn = next.Pawn.Id, Bed = next.Bed, PreviousBed = previous ?? "", Unhoused = unhoused.Count };
            }
        }
        var owed = OwedRoomsOf(plan, rooms, census.Spots);
        if (owed.Count > 0)
        {
            // One room per step: the first owed in plan order the build side does not hold (#3743).
            return new BedroomStep { Kind = BedroomStepKind.Reconcile, Room = owed[0].Room, Owed = EquatableArray.From(owed.Select(o => o.Room).ToArray()), Unhoused = unhoused.Count };
        }
        // No slot left: Unhoused still counts who stays outside a bedroom.
        return SuiteStep();
    }

    /// <summary>
    /// The move into <paramref name="free"/>: each waiting colonist's first open bed there, and the colonist taking it,
    /// the first in id order, or with <paramref name="gains"/> the one with the largest priced gain at its bed's stage
    /// (ForecastHousing.LargestGainFirst). Null when no free bed is open to anyone waiting.
    /// </summary>
    private static (SleepingPerson Pawn, string Bed)? NextOpenBed(List<SleepingPerson> waiting, List<string> free, Dictionary<string, SleepingBed> beds,
        BedroomGainFacts? gains, ISet<string>? findings)
    {
        List<(SleepingPerson Pawn, string Bed)> open = [.. waiting.Select(p => (Pawn: p, Bed: free.FirstOrDefault(id => beds[id].AccessibleTo.Contains(p.Id)) ?? "")).Where(c => c.Bed.Length != 0)];
        if (open.Count == 0)
        {
            return null;
        }
        return gains is null ? open[0]
            : ForecastHousing.LargestGainFirst(open, c => c.Pawn.Id.Value, c => gains.Gain(c.Pawn.Id.Value, gains.BedStage(c.Pawn.Id.Value, c.Bed)), findings)[0];
    }

    /// <summary>
    /// Per planned bedroom or barracks the bedroom step will build, the colonists it would then move in (#3655, #3658), following
    /// <see cref="NextBedroomStep"/>'s order: the unhoused first take the vacant bedroom beds, the largest priced gain first,
    /// and then the planned barracks' vacant bunks open to them in id order; the rest fill the owed rooms in plan order, a
    /// bedroom the colonist with the largest gain at its predicted stage (#3656, BedroomGainFacts.PlannedStages) and that
    /// colonist's waiting partner, a barracks one per missing bed slot in id order. Without <paramref name="gains"/> every
    /// choice is in id order; an unread gain keeps id order and is named in <paramref name="findings"/>. Only bedrooms and
    /// barracks get a row; none when a census fact is unknown.
    /// </summary>
    public static IReadOnlyDictionary<int, EquatableArray<string>> PlannedBedroomOccupants(
        LayoutPlan plan,
        RoomObservation rooms,
        SleepingObservation sleeping,
        IReadOnlyDictionary<string, RoomTarget>? targets,
        BedroomGainFacts? gains = null,
        ISet<string>? findings = null)
    {
        var occupants = new Dictionary<int, EquatableArray<string>>();
        if (CensusOf(plan, rooms, sleeping, targets, gains, findings) is not { } census || census.Unhoused.Count == 0)
        {
            return occupants;
        }
        var waiting = census.Unhoused.ToList();
        var (vacant, bunks) = FreeBedsOf(plan, rooms, census);
        foreach (var (free, priced) in new[] { (vacant, true), (bunks, false) })
        {
            var open = free.ToList();
            while (NextOpenBed(waiting, open, census.Beds, priced ? gains : null, findings) is { } next)
            {
                open.Remove(next.Bed);
                waiting.Remove(next.Pawn);
            }
        }
        var couples = SleepingCouples(sleeping.People);
        foreach (var (room, missing) in OwedRoomsOf(plan, rooms, census.Spots))
        {
            if (waiting.Count == 0)
            {
                break;
            }
            if (room.Role != PlannedRole.Bedroom)
            {
                var bunked = waiting.GetRange(0, Math.Min(missing, waiting.Count));
                if (room.Role == PlannedRole.Barracks)
                {
                    occupants[room.Id] = EquatableArray.From(bunked.Select(p => p.Id.Value).ToArray());
                }
                waiting.RemoveRange(0, bunked.Count);
                continue;
            }
            var stage = gains?.PlannedStages.GetValueOrDefault(room.Id) ?? default;
            var first = gains is null ? waiting[0] : ForecastHousing.LargestGainFirst(waiting, p => p.Id.Value, p => gains.Gain(p.Id.Value, stage), findings)[0];
            List<SleepingPerson> housed = [first, .. waiting.Where(p => couples.TryGetValue(first.Id, out var partner) && p.Id == partner)];
            occupants[room.Id] = EquatableArray.From(housed.Select(p => p.Id.Value).ToArray());
            waiting.RemoveAll(housed.Contains);
        }
        return occupants;
    }

    /// <summary>The bedroom step's census: the stand-in spots, bedroom and barracks beds, every bed by id, the colonists in id
    /// order and those still outside a bedroom. Null when the census is empty or a colonist's owned bed is unknown.</summary>
    private sealed record BedroomCensus(HashSet<string> Spots, Dictionary<string, bool> BedroomBed, HashSet<string> BarracksBed,
        Dictionary<string, SleepingBed> Beds, List<SleepingPerson> People, List<SleepingPerson> Unhoused);

    private static BedroomCensus? CensusOf(LayoutPlan plan, RoomObservation rooms, SleepingObservation sleeping, IReadOnlyDictionary<string, RoomTarget>? targets,
        BedroomGainFacts? gains, ISet<string>? findings)
    {
        if (sleeping.People.Count == 0 || sleeping.People.Count != sleeping.Colonists)
        {
            return null;
        }
        // A sleeping spot is a stand-in (#3659): it houses no one, wherever it stands, and no one is moved onto it.
        var spots = sleeping.Beds.Where(b => b.Definition.Value == SleepingSpotDefinition).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var bedroomBed = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var room in rooms.Rooms)
        {
            if (room.Role == RoomRole.Bedroom)
            {
                foreach (var b in room.BedIds())
                {
                    bedroomBed[b] = !spots.Contains(b);
                }
            }
        }
        // A barracks bed houses its owner, though a barracks of one bed reads as a bedroom.
        var barracksBed = BarracksBedIds(plan, rooms, spots);
        var beds = new Dictionary<string, SleepingBed>(StringComparer.Ordinal);
        var owners = new Dictionary<string, HashSet<PawnId>>(StringComparer.Ordinal);
        foreach (var b in sleeping.Beds)
        {
            beds[b.Id] = b;
            if (!owners.TryGetValue(b.Room, out var set))
            {
                owners[b.Room] = set = [];
            }
            set.UnionWith(b.Owners);
        }
        // A NeverUpgrade room (an ascetic's) houses its owner only while they own it alone (#3656): a shared barracks
        // gives an ascetic no room thought, so it is unhoused and ranked like anyone else.
        bool Kept(SleepingPerson p, string bed) => beds.TryGetValue(bed, out var b) && b.Room is var room
            && NeverUpgradeOf(targets, room) && owners[room].All(o => o == p.Id);
        var people = sleeping.People.OrderBy(p => p.Id).ToList();
        var couples = SleepingCouples(sleeping.People);
        var crowded = CrowdedOut(plan, rooms, spots, sleeping, couples, gains, findings);
        var unhoused = new List<SleepingPerson>();
        foreach (var p in people)
        {
            if (!p.OwnedBed.TryGetValue(out var bed))
            {
                return null;
            }
            // A colonist with no bed is unhoused too: with nothing better buildable they get a bedroom shell and a spot.
            if (bed == "")
            {
                unhoused.Add(p);
                continue;
            }
            // A couple is never split into single bedrooms: sleeping upkeep would reunite them at once.
            if (!couples.ContainsKey(p.Id) && !(bedroomBed.TryGetValue(bed, out var housed) && housed) && !(barracksBed.Contains(bed) && !crowded.Contains(p.Id)) && !Kept(p, bed))
            {
                unhoused.Add(p);
            }
        }
        return new(spots, bedroomBed, barracksBed, beds, people, unhoused);
    }

    /// <summary>
    /// The owners a planned barracks holds more of than it seats (#3680). The plan's free beds (each barracks' bed slots and
    /// each bedroom's bed, a Retiring room's left out) less the colonists are the spare places, which the bed merge seats
    /// in the barracks first at no loss, so a barracks seats its slots less the spare it holds. A split (#3661) keeps the
    /// standing barracks with every bed it stands with while it seats one fewer, so the owners past its seats are owed a
    /// bedroom: the largest loss a day sleeping in a barracks over a bedroom first (the bed merge's loss,
    /// BedroomGainFacts.BedMerge, at the planned stage), as the merge left them out. Id order without gains or with one
    /// unread (named in <paramref name="findings"/>); a couple stays housed.
    /// </summary>
    private static HashSet<PawnId> CrowdedOut(LayoutPlan plan, RoomObservation rooms, HashSet<string> spots, SleepingObservation sleeping,
        Dictionary<PawnId, PawnId> couples, BedroomGainFacts? gains, ISet<string>? findings)
    {
        var output = new HashSet<PawnId>();
        var retiring = RetiringRooms(plan);
        var pool = plan.Rooms.Where(r => !retiring.Contains(r.Region.Bounds())).ToList();
        var spare = Math.Max(0, pool.Sum(r => r.Role == PlannedRole.Bedroom ? 1 : BedSlotsOf(plan, r, rooms.Shapes.Furniture)?.Count ?? 0) - sleeping.Colonists);
        var stage = Known(LayoutMoodRules.MoodJudgment.PlannedStage);
        foreach (var r in pool)
        {
            if (BedSlotsOf(plan, r, rooms.Shapes.Furniture) is not { } slots)
            {
                continue;
            }
            var held = Math.Min(spare, slots.Count);
            spare -= held;
            var (room, ok) = PlannedRoom.CensusRoomIn(r, rooms);
            if (!ok)
            {
                continue;
            }
            var standing = room!.BedIds().Where(b => !spots.Contains(b)).ToHashSet(StringComparer.Ordinal);
            var owners = sleeping.Beds.Where(b => standing.Contains(b.Id)).SelectMany(b => b.Owners).Distinct().Order().ToList();
            var over = owners.Count - (slots.Count - held);
            if (over <= 0)
            {
                continue;
            }
            var single = owners.Where(p => !couples.ContainsKey(p)).ToList();
            var ranked = gains is null ? single
                : ForecastHousing.LargestGainFirst(single, p => p.Value, p => gains.Gain(p.Value, stage) - gains.Gain(p.Value, stage, barracks: true), findings);
            output.UnionWith(ranked.Take(over));
        }
        return output;
    }

    /// <summary>The vacant beds an unhoused colonist can be moved into, in order: bedroom beds (no suite's, no barracks'),
    /// then the planned barracks' bunks; a Retiring room's bed takes no one new.</summary>
    private static (List<string> Vacant, List<string> Bunks) FreeBedsOf(LayoutPlan plan, RoomObservation rooms, BedroomCensus census)
    {
        // A suite's bed is its claimant's, never an unhoused pawn's.
        var suiteBed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedRole.Suite)
            {
                continue;
            }
            // Census: the suite's beds are read off its room.
            var (room, ok) = PlannedRoom.CensusRoomIn(r, rooms);
            if (ok)
            {
                foreach (var b in room!.BedIds())
                {
                    suiteBed.Add(b);
                }
            }
        }
        var retiringBed = RetiringBeds(plan, rooms);
        bool Vacant(string id) => census.Beds.TryGetValue(id, out var b) && VacantColonistBed(b) && !retiringBed.Contains(id);
        var vacant = census.BedroomBed.Where(e => e.Value && !census.BarracksBed.Contains(e.Key) && !suiteBed.Contains(e.Key) && Vacant(e.Key)).Select(e => e.Key).Order(StringComparer.Ordinal).ToList();
        var bunks = census.BarracksBed.Where(Vacant).Order(StringComparer.Ordinal).ToList();
        return (vacant, bunks);
    }

    /// <summary>The planned bedrooms and barracks still owed beds, in plan order, with how many: a bedroom its one bed, a
    /// barracks a bed per tiled bed slot (BedSlotsOf). A Retiring wing is never built out further. A
    /// stand-in spot (<paramref name="spots"/>) is no bed.</summary>
    private static List<(PlannedRoom Room, int Missing)> OwedRoomsOf(LayoutPlan plan, RoomObservation rooms, HashSet<string> spots)
    {
        // Census: beds and roof, read off the room.
        var owed = new List<(PlannedRoom, int)>();
        var retiring = RetiringRooms(plan);
        foreach (var r in plan.Rooms)
        {
            if ((r.Role != PlannedRole.Bedroom && r.Role != PlannedRole.Barracks) || retiring.Contains(r.Region.Bounds()))
            {
                continue;
            }
            var (room, ok) = PlannedRoom.CensusRoomIn(r, rooms);
            var standing = ok ? room!.BedIds().Count(b => !spots.Contains(b)) : 0;
            var slots = BedSlotsOf(plan, r, rooms.Shapes.Furniture)?.Count ?? 1;
            if (standing < slots)
            {
                owed.Add((r, slots - standing));
            }
        }
        return owed;
    }

    /// <summary>
    /// A room's desired furniture where the bedroom owner and the room-quality levers share one list: bed (a
    /// definition name, "" for none) in the bedroom template's bed slot, and every lever piece that stands in
    /// the room (end table, dresser, lamp, plant pot, animal bed) where it stands, so the one reconcile never
    /// repacks it. add is the piece a lever asks for this round (title furniture, companion bed, room upgrade,
    /// beauty); it is listed beside the standing ones of its definition and is the only entry still to build.
    /// Suites take the bedroom's slot too; a barracks takes bedSlots (BedSlotsOf), one bed at each
    /// slot whose footprint bed fits. The list has no bed when the template does not fit the room.
    /// </summary>
    public static Furnishing BedroomFurnishing(PlannedRoom room, PieceShapes shapes, string bed, IReadOnlyList<CurrentBuilding> built, RoomUpgrade? add, IReadOnlyList<InteriorPiece>? bedSlots = null)
    {
        var wants = new List<FurnishingWant>();
        InteriorRoom? interior = null;
        var bedCount = 0;
        if (bed.Length > 0)
        {
            var (room0, ok) = InteriorRules.InteriorRoomFromLayout(room, shapes);
            if (!ok)
            {
                return new Furnishing();
            }
            interior = room0! with { Role = RoomRole.Bedroom };
            var (piece, pk) = interior.Piece(bed);
            if (!pk)
            {
                return new Furnishing();
            }
            if (bedSlots is null)
            {
                bedCount = 1;
                wants.Add(new FurnishingWant { Key = "bed", Things = [bed], Count = bedCount, Piece = piece });
            }
            else
            {
                List<InteriorPiece> slots = [.. bedSlots.Where(s => s.Size == piece.Size).Select(s => s with { Def = bed, InteractionOffset = piece.Interaction })];
                bedCount = slots.Count;
                wants.Add(new FurnishingWant { Things = [bed], Count = bedCount, Piece = piece, Slots = slots });
            }
        }
        var bounds = room.Region.Bounds();
        var furniture = shapes.Furniture;
        // Every bed standing in the room stays listed where it stands, not only the one asked for: the owner
        // removes what the list omits, and a bed the sleeping ladder did not choose is not stale.
        List<string> levers = [.. furniture.Beds.Select(b => b.Def), furniture.EndTable.Def, furniture.Dresser.Def, InteriorHelpersRules.StandingLampDef, RoomQualityRules.PlantPotDefinition, furniture.AnimalSpot, furniture.AnimalBed];
        if (add is not null && !levers.Contains(add.Def))
        {
            levers.Add(add.Def);
        }
        foreach (var def in levers.Where(d => d.Length > 0).Distinct())
        {
            var standing = built.Count(b => b.Cells.Count > 0 && b.Building!.Definition == def && FurnitureRoomsRules.RectInside(bounds, PlacementSearchRules.CellsRectangle(b.Cells.Items)));
            if (def == bed)
            {
                standing -= Math.Min(standing, bedCount); // the bed want above lists these
            }
            var asked = add is not null && add.Def == def;
            wants.Add(new FurnishingWant { Things = [def], Count = standing + (asked ? 1 : 0), Slots = asked ? [add!.Slotted()] : [], Piece = new InteriorPieceDef { Def = def } });
        }
        var furnishing = RoomFurnishingRules.RoomFurnishing(room, interior, wants, built);
        if (add is null || add.Stuff.Length == 0)
        {
            return furnishing;
        }

        // The asked piece is the one at its slot's cell: it is built in the material the lever chose.
        var slot = add.Slotted().Wanted();
        return furnishing with { Pieces = [.. furnishing.Pieces.Select(p => p.DefName == slot.DefName && p.Minimum == slot.Minimum && p.Maximum == slot.Maximum ? p with { Stuff = add.Stuff } : p)] };
    }

    /// <summary>
    /// Whether a bedroom step's room still owes its reconcile to <paramref name="furnishing"/> (#3743): a template piece
    /// is absent, or the room is not enclosed (no census room stands enclosed in it), so its ring is still owed though
    /// the bed stands. An enclosed, furnished room owes nothing, nor does a room the template does not fit.
    /// </summary>
    public static bool BedroomReconcileOwed(PlannedRoom room, RoomObservation rooms, Furnishing furnishing) =>
        furnishing.Pieces.Count > 0 && (furnishing.Absent || !PlannedRoom.CensusRoomIn(room, rooms).Ok);

    /// <summary>The no_space subject of a bedroom step whose room owes no reconcile (<see cref="BedroomReconcileOwed"/> false).</summary>
    public const string BedroomTemplateSubject = "bedroom_template";

    /// <summary>Unlocks Bed and DoubleBed.</summary>
    public const string BedResearch = "ComplexFurniture";

    /// <summary>
    /// Appends BedResearch to needs once every colonist owns a bed in an individual bedroom (NextBedroomStep
    /// has no step left and nobody is unhoused) while Bed is known unavailable. The bed ladder then upgrades
    /// bedrolls and spots once it is researched.
    /// </summary>
    public static IReadOnlyList<string> BedResearchRequest(
        IReadOnlyList<string> needs, LayoutPlan plan, RoomObservation rooms, SleepingObservation sleeping, IReadOnlyDictionary<string, RoomTarget>? targets)
    {
        if (!sleeping.BedBuildable.TryGetValue(out var buildable) || buildable)
        {
            return needs;
        }
        if (sleeping.People.Count == 0 || sleeping.People.Count != sleeping.Colonists)
        {
            return needs;
        }
        foreach (var p in sleeping.People)
        {
            if (!p.OwnedBed.TryGetValue(out var bed) || bed == "")
            {
                return needs;
            }
        }
        var step = NextBedroomStep(plan, rooms, sleeping, targets, null, null);
        if ((step.Kind != BedroomStepKind.None && step.Room.Role != PlannedRole.Suite) || step.Unhoused > 0)
        {
            return needs;
        }
        return [.. needs, BedResearch];
    }

    /// <summary>The review's bedroom deficit: known true while a bedroom step is due, unknown while the plan, room or sleeping census is.</summary>
    public static Fact<bool> BedroomsOwed(
        LayoutPlan? plan,
        Fact<RoomObservation> rooms,
        Fact<SleepingObservation> sleeping,
        IReadOnlyDictionary<string, RoomTarget>? targets,
        IReadOnlyDictionary<PawnId, TraitEffects>? traits,
        IReadOnlyDictionary<PawnId, double>? pressure)
    {
        var p = plan;
        var pk = p is not null;
        var rk = rooms.TryGetValue(out var r);
        var sk = sleeping.TryGetValue(out var s);
        if (!pk || !rk || !sk)
        {
            return Unknown<bool>();
        }
        return Known(NextBedroomStep(p!, r!, s!, targets, traits, pressure).Kind != BedroomStepKind.None);
    }
}
