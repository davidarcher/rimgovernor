namespace RimGovernor.Brain.Policy;

// Suites for qualifying pawns. Where the gap closer and the bed ladder skip a room whose weakest stat is
// space, the pawn is given a suite instead: the plan sites a suite block holding one suite sized for the
// pawn's target (SuiteTargets into the suite blocks), the bedroom ladder shells and furnishes it and moves
// the pawn in (NextBedroomStep), and the standard room left behind is vacant for the next unhoused pawn. No
// claim is stored: a pawn qualifies while it holds a standard room its target outgrows, and a planned suite
// nobody owns is the next claimant's.

/// <summary>Why a pawn is owed a suite.</summary>
public enum SuiteClaimReason
{
    [WireName("")]
    None = 0,

    /// <summary>The standard room is below target and space is its weakest stat.</summary>
    [WireName("space_weakest")]
    Space,

    /// <summary>The target needs more floor than the room has.</summary>
    [WireName("target_outgrows_room")]
    Floor,

    /// <summary>The pawn's suite is smaller than its target needs; a suite never grows, so it is owed a new one.</summary>
    [WireName("suite_too_small")]
    TooSmall,
}

/// <summary>One pawn a suite is wanted for: the owner of Bed, a solo standard bedroom that cannot reach Target.</summary>
public sealed record SuiteClaim
{
    public PawnId Pawn { get; init; }

    public string Bed { get; init; } = "";

    public double Target { get; init; }

    /// <summary>Why the pawn qualifies, for the claim log line.</summary>
    public SuiteClaimReason Reason { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>The interior area whose space alone meets target (see SuiteSize), before snapping.</summary>
    internal static int SuiteCells(double target)
    {
        var space = 125 * Math.Max(target, 0) / 100;
        return (int)Math.Ceiling((space + (0.9 * SuiteFurnitureTiles)) / 1.4);
    }

    /// <summary>Maps the census id of each standing planned room of role to its plan room.</summary>
    internal static Dictionary<string, PlannedRoom> PlannedRoomIds(LayoutPlan plan, RoomObservation rooms, PlannedRole role)
    {
        var output = new Dictionary<string, PlannedRoom>(StringComparer.Ordinal);
        foreach (var r in plan.Rooms)
        {
            if (r.Role != role)
            {
                continue;
            }
            // Census: the room's id keys the quality targets.
            var (room, ok) = CensusRoomIn(r, rooms);
            if (ok)
            {
                output[room!.Id] = r;
            }
        }
        return output;
    }

    /// <summary>The census ids of the plan's standing suites.</summary>
    public static HashSet<string> SuiteRoomIds(LayoutPlan plan, RoomObservation rooms) =>
        new(PlannedRoomIds(plan, rooms, PlannedRole.Suite).Keys, StringComparer.Ordinal);

    /// <summary>
    /// Every pawn owed a suite, most suite pressure first: the sole owner of a standing planned standard
    /// bedroom below its target's Min whose space is the weakest stat, or whose target needs more floor than
    /// the room has (Greedy, Jealous of a suite, a title), and the owner of a suite smaller than its target.
    /// An ascetic never gets one. A claim the owner's remaining share cannot furnish is dropped (RoomGate).
    /// </summary>
    public static IReadOnlyList<SuiteClaim> SuiteClaims(
        LayoutPlan plan,
        RoomObservation rooms,
        SleepingObservation sleeping,
        IReadOnlyDictionary<string, RoomTarget> targets,
        IReadOnlyDictionary<PawnId, TraitEffects> traits,
        IReadOnlyDictionary<PawnId, double> pressure,
        RoomGate gate)
    {
        if (!sleeping.Rooms.TryGetValue(out var census))
        {
            return [];
        }
        var quality = new Dictionary<string, RoomQuality>(StringComparer.Ordinal);
        foreach (var r in census)
        {
            if (r.Quality.TryGetValue(out var rq))
            {
                quality[r.Id] = rq;
            }
        }
        var standard = PlannedRoomIds(plan, rooms, PlannedRole.Bedroom);
        var suites = PlannedRoomIds(plan, rooms, PlannedRole.Suite);
        var output = new List<SuiteClaim>();
        foreach (var s in SoloBedrooms(sleeping))
        {
            var tk = targets.TryGetValue(s.Room, out var t);
            var qk = quality.TryGetValue(s.Room, out var q);
            var ascetic = traits.TryGetValue(s.Owner, out var e) && e.Ascetic;
            if (!tk || !qk || ascetic || t!.NeverUpgrade || t.Min <= 0 || q!.Impressiveness >= t.Min)
            {
                continue;
            }
            // A suite below its owner's target is left for a larger new one; a built suite never grows.
            if (suites.TryGetValue(s.Room, out var suite))
            {
                var area = suite.Region.Bounds().Width * suite.Region.Bounds().Height;
                var (w, d) = SuiteSize(t.Min);
                if (SuiteCells(t.Min) <= area || w * d <= area)
                {
                    continue;
                }
                output.Add(new SuiteClaim { Pawn = s.Owner, Bed = s.Bed, Target = t.Min, Reason = SuiteClaimReason.TooSmall });
                continue;
            }
            if (!standard.TryGetValue(s.Room, out var r2))
            {
                continue;
            }
            var reason = SuiteClaimReason.Space;
            // Only a trait or title raises a target past what space alone must reach in a standard room; a
            // tier-only target upgrades in place and claims only when space is its weakest stat.
            if (RaisedTarget(t) && SuiteCells(t.Min) > r2.Region.Bounds().Width * r2.Region.Bounds().Height)
            {
                reason = SuiteClaimReason.Floor;
            }
            else if (WeakestRoomStat(q) != RoomStat.Space)
            {
                continue;
            }
            output.Add(new SuiteClaim { Pawn = s.Owner, Bed = s.Bed, Target = t.Min, Reason = reason });
        }
        return GateSuiteClaims(OrderSuiteClaims(output, pressure), plan, rooms, sleeping, gate);
    }

    /// <summary>Whether Greedy, Jealous or a title raised the target above the tier baseline (RoomQualityTargets names them only then).</summary>
    internal static bool RaisedTarget(RoomTarget t) => t.Reasons.Any(r => r is "greedy" or "jealous" or "title");

    /// <summary>
    /// targets without the rooms claims are leaving (suite first): a pawn owed a suite gets no in-place
    /// quality upgrade of its standard room; the suite steps walk it out instead.
    /// </summary>
    public static IReadOnlyDictionary<string, RoomTarget> UpgradeTargets(
        IReadOnlyDictionary<string, RoomTarget> targets, SleepingObservation sleeping, IReadOnlyList<SuiteClaim> claims)
    {
        if (claims.Count == 0)
        {
            return targets;
        }
        var claimed = new HashSet<string>(claims.Select(c => c.Bed), StringComparer.Ordinal);
        var leaving = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in sleeping.Beds)
        {
            if (b.Room.TryGetValue(out var room) && claimed.Contains(b.Id))
            {
                leaving.Add(room);
            }
        }
        var output = new Dictionary<string, RoomTarget>(targets.Count, StringComparer.Ordinal);
        foreach (var (id, t) in targets)
        {
            if (!leaving.Contains(id))
            {
                output[id] = t;
            }
        }
        return output;
    }

    /// <summary>The plan's suites nobody owns a bed in, in plan order: unbuilt, standing empty, or holding only unowned beds.</summary>
    internal static List<PlannedRoom> VacantSuites(LayoutPlan plan, RoomObservation rooms, SleepingObservation sleeping)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in sleeping.Beds)
        {
            if (b.Owners.Count > 0)
            {
                owned.Add(b.Id);
            }
        }
        var output = new List<PlannedRoom>();
        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedRole.Suite)
            {
                continue;
            }
            // Census: the room's beds say whether a suite is taken.
            var (room, ok) = CensusRoomIn(r, rooms);
            var taken = false;
            if (ok)
            {
                foreach (var b in room!.Beds)
                {
                    taken = taken || owned.Contains(b);
                }
            }
            if (!taken)
            {
                output.Add(r);
            }
        }
        return output;
    }

    /// <summary>How many suites the plan holds.</summary>
    public static int SuiteRooms(this LayoutPlan p)
    {
        var n = 0;
        foreach (var r in p.Rooms)
        {
            if (r.Role == PlannedRole.Suite)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// The generator's suites argument: one entry per suite the plan holds, in plan order across its suite
    /// blocks and zero (kept as it is, a suite never grows), then the target of each claim no vacant suite
    /// answers.
    /// </summary>
    public static IReadOnlyList<double> SuiteTargets(
        LayoutPlan plan, RoomObservation rooms, SleepingObservation sleeping, IReadOnlyList<SuiteClaim> claims)
    {
        var output = new List<double>(new double[plan.SuiteRooms()]);
        foreach (var c in claims.Skip(Math.Min(VacantSuites(plan, rooms, sleeping).Count, claims.Count)))
        {
            output.Add(c.Target);
        }
        return output;
    }

    /// <summary>
    /// The suites the station graph sizes (StationGraphInput.Suites, #2939): one wish per suite the plan
    /// holds, in plan order, its target the sole owner's (an unowned or unread suite asks nothing beyond its
    /// own floor) and Kept the area it has, then one per claim of a standard room that no vacant suite
    /// answers. A suite smaller than its owner's target is one wish, not a second: its target outgrows the
    /// floor, and the successor replaces it (SuiteClaimReason.TooSmall).
    /// </summary>
    public static IReadOnlyList<SuiteWish> SuiteWishes(
        LayoutPlan plan,
        RoomObservation rooms,
        SleepingObservation sleeping,
        IReadOnlyDictionary<string, RoomTarget> targets,
        IReadOnlyList<SuiteClaim> claims)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in SoloBedrooms(sleeping))
        {
            owned.Add(s.Room);
        }
        var output = new List<SuiteWish>();
        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedRole.Suite)
            {
                continue;
            }
            var w = new SuiteWish { Kept = r.Region.Area() };
            var (room, ok) = CensusRoomIn(r, rooms);
            if (ok && owned.Contains(room!.Id))
            {
                w = w with { Target = targets.TryGetValue(room.Id, out var t) ? t.Min : 0 };
            }
            output.Add(w);
        }
        var fresh = claims.Where(c => c.Reason != SuiteClaimReason.TooSmall).ToList();
        foreach (var c in fresh.Skip(Math.Min(VacantSuites(plan, rooms, sleeping).Count, fresh.Count)))
        {
            output.Add(new SuiteWish { Target = c.Target });
        }
        return output;
    }

    /// <summary>
    /// Walks the first claim into its suite: the claims take the vacant suites in order, and the first whose
    /// suite needs anything gets its shell, then a bed, then the move.
    /// </summary>
    internal static BedroomStep NextSuiteStep(
        LayoutPlan plan, RoomObservation rooms, SleepingObservation sleeping, IReadOnlyList<SuiteClaim> claims)
    {
        var beds = new Dictionary<string, SleepingBed>(StringComparer.Ordinal);
        foreach (var b in sleeping.Beds)
        {
            beds[b.Id] = b;
        }
        var vacant = VacantSuites(plan, rooms, sleeping);
        for (var i = 0; i < claims.Count; i++)
        {
            var c = claims[i];
            if (i >= vacant.Count)
            {
                break;
            }
            var r = vacant[i];
            // Census: the room's beds decide the next bedroom step.
            var (room, ok) = CensusRoomIn(r, rooms);
            if (!ok || room!.Beds.Count == 0)
            {
                return new BedroomStep { Kind = BedroomStepKind.Reconcile, Room = r };
            }
            foreach (var id in room.Beds.Order(StringComparer.Ordinal))
            {
                if (beds.TryGetValue(id, out var b) && ContainsPawn(b.AccessibleTo, c.Pawn))
                {
                    return new BedroomStep { Kind = BedroomStepKind.Move, Pawn = c.Pawn, Bed = id, PreviousBed = c.Bed, Room = r };
                }
            }
        }
        return new BedroomStep();
    }
}
