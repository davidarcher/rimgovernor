namespace RimGovernor.Brain.Policy;

// Individual bedrooms. The sleeping planner walks each colonist, a bedless joiner included, into a room of
// their own in the bedroom wing, in the wing's slot order: raise the room's shell, stage one bed in it, then
// move the colonist's ownership there. The shelter bed left behind stays as a spare for joiners
// (MaintainHousing keeps one beyond the population).

/// <summary>The next bedroom step.</summary>
public enum BedroomStepKind
{
    /// <summary>Every colonist owns a bed in a Bedroom-role room, or no planned bedroom is left to build.</summary>
    None,

    /// <summary>Assign Bed, a vacant bed in a Bedroom-role room, to Pawn.</summary>
    Move,

    /// <summary>
    /// Room, a planned bedroom with no bed (not standing, or standing empty), is reconciled by the build side
    /// (ReconcileRoom) to the plan and a bed template (BedroomTemplate). Its state is whatever the diff
    /// leaves: no shell or furnish step.
    /// </summary>
    Reconcile,

    /// <summary>Deconstruct Bed, a vacant sleeping spot left in the starter shell (the planned shelter) at Cells[0].</summary>
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

    /// <summary>A Clear's one cell.</summary>
    public EquatableArray<Cell> Cells { get; init; }

    public int Unhoused { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>The beds standing in the starter shell, the planned shelter.</summary>
    public static HashSet<string> ShellBedIds(LayoutPlan plan, RoomObservation rooms)
    {
        var shell = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedRole.Shelter)
            {
                continue;
            }
            // Census: the shelter's beds are read off its room.
            var (room, ok) = CensusRoomIn(r, rooms);
            if (ok)
            {
                foreach (var b in room!.Beds)
                {
                    shell.Add(b);
                }
            }
        }
        return shell;
    }

    private static bool NeverUpgradeOf(IReadOnlyDictionary<string, RoomTarget>? targets, string room) =>
        targets is not null && targets.TryGetValue(room, out var t) && t.NeverUpgrade;

    /// <summary>
    /// Picks the next bedroom step from the plan, the room census and the sleeping census. Move comes first
    /// (it costs nothing), then a room with no bed to reconcile (the first in plan order that owes one), only
    /// while the standing bedrooms cannot take every colonist still outside one; a room dug into rock is a
    /// shell too, whose builder mines it first. It reports BedroomStepKind.None whenever a fact it needs is
    /// unknown. A colonist whose current room RoomTargets marks NeverUpgrade (an ascetic) counts as housed:
    /// the move never takes them from the plainest room.
    /// </summary>
    public static BedroomStep NextBedroomStep(
        LayoutPlan plan,
        RoomObservation rooms,
        SleepingObservation sleeping,
        IReadOnlyDictionary<string, RoomTarget>? targets,
        IReadOnlyDictionary<PawnId, TraitEffects>? traits,
        IReadOnlyDictionary<PawnId, double>? pressure,
        RoomGate gate)
    {
        if (sleeping.People.Count == 0 || sleeping.People.Count != sleeping.Colonists)
        {
            return new BedroomStep();
        }
        // The starter shell is the planned shelter: the last spot left in it reads as a bedroom but is still the shell.
        var shell = ShellBedIds(plan, rooms);
        var bedroomBed = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var room in rooms.Rooms)
        {
            if (room.Role.TryGetValue(out var role) && role == RoomRole.Bedroom)
            {
                foreach (var b in room.Beds)
                {
                    bedroomBed[b] = !shell.Contains(b);
                }
            }
        }
        var beds = new Dictionary<string, SleepingBed>(StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in sleeping.Beds)
        {
            beds[b.Id] = b;
            if (b.Room.TryGetValue(out var room) && NeverUpgradeOf(targets, room))
            {
                kept.Add(b.Id);
            }
        }
        var people = sleeping.People.OrderBy(p => p.Id).ToList();
        var couples = SleepingCouples(sleeping.People);
        var unhoused = new List<SleepingPerson>();
        foreach (var p in people)
        {
            if (!p.OwnedBed.TryGetValue(out var bed))
            {
                return new BedroomStep();
            }
            // A colonist with no bed is unhoused too: with nothing better buildable they get a bedroom shell and a spot.
            if (bed == "")
            {
                unhoused.Add(p);
                continue;
            }
            // A couple is never split into single bedrooms: sleeping upkeep would reunite them at once.
            if (!couples.ContainsKey(p.Id) && !(bedroomBed.TryGetValue(bed, out var housed) && housed) && !kept.Contains(bed))
            {
                unhoused.Add(p);
            }
        }
        // A spot its owner left behind in the shell is taken down, so the shell ends with no bunks. Only a spare
        // one: a spot is never an owned bed, so a spot just built for the shelter reads vacant too, and clearing
        // it re-opened the shelter deficit until the epoch's bed cap ran out one spot short (campaign/foothold).
        // The clear leaves an indoor bed for every colonist.
        var indoor = 0;
        foreach (var room in rooms.Rooms)
        {
            if (room.Enclosed.TryGetValue(out var enclosed) && enclosed)
            {
                foreach (var id in room.Beds)
                {
                    if (beds.TryGetValue(id, out var b) && b.Humanlike.TryGetValue(out var human) && human)
                    {
                        indoor++;
                    }
                }
            }
        }
        var left = new List<string>();
        foreach (var id in shell)
        {
            if (beds.TryGetValue(id, out var b) && b.Definition.Value == SleepingSpotDefinition && b.Owners.Count == 0)
            {
                left.Add(id);
            }
        }
        if (left.Count > 0 && indoor > people.Count)
        {
            left.Sort(StringComparer.Ordinal);
            var b = beds[left[0]];
            return new BedroomStep { Kind = BedroomStepKind.Clear, Bed = b.Id, Cells = EquatableArray.From([b.Cell]), Unhoused = unhoused.Count };
        }
        // Once the standard rooms have nothing to do, a qualifying pawn is walked into a suite.
        BedroomStep SuiteStep()
        {
            var step = NextSuiteStep(plan, rooms, sleeping, SuiteClaims(plan, rooms, sleeping, targets!, traits!, pressure!, gate));
            return step with { Unhoused = unhoused.Count };
        }
        if (unhoused.Count == 0)
        {
            return SuiteStep();
        }
        // A suite's bed is its claimant's, never an unhoused pawn's.
        var suiteBed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedRole.Suite)
            {
                continue;
            }
            // Census: the suite's beds are read off its room.
            var (room, ok) = CensusRoomIn(r, rooms);
            if (ok)
            {
                foreach (var b in room!.Beds)
                {
                    suiteBed.Add(b);
                }
            }
        }
        var retiringBed = RetiringBeds(plan, rooms);
        var vacant = new List<string>();
        foreach (var (id, housed) in bedroomBed)
        {
            // A Retiring room's bed takes no one new.
            if (housed && beds.TryGetValue(id, out var b) && !suiteBed.Contains(id) && VacantColonistBed(b) && !retiringBed.Contains(id))
            {
                vacant.Add(id);
            }
        }
        vacant.Sort(StringComparer.Ordinal);
        foreach (var p in unhoused)
        {
            foreach (var id in vacant)
            {
                if (beds[id].AccessibleTo.Contains(p.Id))
                {
                    p.OwnedBed.TryGetValue(out var previous);
                    return new BedroomStep { Kind = BedroomStepKind.Move, Pawn = p.Id, Bed = id, PreviousBed = previous ?? "", Unhoused = unhoused.Count };
                }
            }
        }
        // Census: beds and roof, read off the room.
        var owed = new List<PlannedRoom>();
        var retiring = RetiringRooms(plan);
        foreach (var r in plan.Rooms)
        {
            // A Retiring wing is never built out further.
            if (r.Role != PlannedRole.Bedroom || retiring.Contains(r.Region.Bounds()))
            {
                continue;
            }
            var (room, ok) = CensusRoomIn(r, rooms);
            if (!ok)
            {
                owed.Add(r);
                continue;
            }
            if (room!.Beds.Count == 0)
            {
                owed.Add(r);
            }
        }
        if (owed.Count > 0)
        {
            // One bedroom per step: the first owed in plan order.
            return new BedroomStep { Kind = BedroomStepKind.Reconcile, Room = owed[0], Unhoused = unhoused.Count };
        }
        // No slot left: Unhoused still counts who stays outside a bedroom.
        return SuiteStep();
    }

    /// <summary>
    /// The furniture template of a Reconcile step's room: bed (a definition name) in the bedroom template's
    /// bed slot. Suites and the shelter take the bedroom's slot too. Null when the template does not fit the room.
    /// </summary>
    public static IReadOnlyList<WantedPiece>? BedroomTemplate(PlannedRoom room, PieceShapes shapes, string bed)
    {
        var (room0, ok) = InteriorRoomFromLayout(room, shapes);
        if (!ok)
        {
            return null;
        }
        var interior = room0! with { Role = RoomRole.Bedroom };
        var (piece, pk) = interior.Piece(bed);
        if (!pk)
        {
            return null;
        }
        var (plan, planOk) = PlanRoomInterior(interior, piece);
        if (!planOk)
        {
            return null;
        }
        foreach (var p in plan.Pieces)
        {
            if (p.Slot == "bed" && p.Def == bed)
            {
                return
                [
                    new WantedPiece
                    {
                        DefName = p.Def,
                        Minimum = new Cell(p.Rect.X, p.Rect.Z),
                        Maximum = new Cell(p.Rect.X + p.Rect.Width - 1, p.Rect.Z + p.Rect.Height - 1),
                        Slot = p.Slot,
                        Size = p.Size,
                        Rot = p.Rot,
                    },
                ];
            }
        }
        return null;
    }

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
        var step = NextBedroomStep(plan, rooms, sleeping, targets, null, null, new RoomGate());
        if ((step.Kind != BedroomStepKind.None && step.Room.Role != PlannedRole.Suite) || step.Unhoused > 0)
        {
            return needs;
        }
        return [.. needs, BedResearch];
    }

    /// <summary>The review's bedroom deficit: known true while a bedroom step is due, unknown while the plan, room or sleeping census is.</summary>
    public static Fact<bool> BedroomsOwed(
        Fact<LayoutPlan> plan,
        Fact<RoomObservation> rooms,
        Fact<SleepingObservation> sleeping,
        IReadOnlyDictionary<string, RoomTarget>? targets,
        IReadOnlyDictionary<PawnId, TraitEffects>? traits,
        IReadOnlyDictionary<PawnId, double>? pressure,
        RoomGate gate)
    {
        var pk = plan.TryGetValue(out var p);
        var rk = rooms.TryGetValue(out var r);
        var sk = sleeping.TryGetValue(out var s);
        if (!pk || !rk || !sk)
        {
            return Unknown<bool>();
        }
        return Known(NextBedroomStep(p!, r!, s!, targets, traits, pressure, gate).Kind != BedroomStepKind.None);
    }
}
