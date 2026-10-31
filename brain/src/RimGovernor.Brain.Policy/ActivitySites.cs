using ActivityAccess = RimGovernor.Brain.GameFacts.Observations.ActivityAccess;
using ComfortFacts = RimGovernor.Brain.GameFacts.Observations.ComfortFacts;
using ComfortHost = RimGovernor.Brain.GameFacts.Observations.ComfortHost;
using Engagement = RimGovernor.Brain.GameFacts.Observations.Engagement;

namespace RimGovernor.Brain.Policy;

// Activity sites (habitat 3.3): where a party, a marriage or a ritual gathers its participants. A site is provision
// only: it never decides whether or when the activity happens (rooms/gatherings and ideology/rituals do), and the
// activity can complete without one (native falls back to a gathering cell, RCellFinder.TryFindGatheringSpot and
// TryFindMarriageSite). Each kind's demand comes from facts: a party from the free colonists native counts
// (GatheringsUtility.AcceptableGameConditionsToStartGathering: at least four), a marriage from an engaged couple
// (a Fiance relation; native starts the ceremony itself), a ritual from a held ritual the colony can start on its own
// that names a required building (absent without Ideology).
//
// Where a site stands is not chosen here. The station facts carry each kind's options (ActivitySiteAsk): the standing
// sites, the site the plan holds, a spot in each eligible existing room and a new site room. The layout comparator
// (LayoutStationPlanRules.HostedRegroupInputsOf, LayoutRegroupRules.RegroupAsync) compares one alternative per kind and
// option against the baseline by the canonical ledger, the baseline keeping ties; a new room is a room node of the
// station graph whose shell the ledger charges like any planned room's. The ledger credits the site set a candidate's
// plan holds (ForecastActivitySites): attendance from access and capacity, a ritual's surroundings, competing uses.
// Sites have no lifecycle: a standing site is never retired, moved or upgraded; the placement step binds the site the
// adopted plan holds without choosing again.

/// <summary>The activity a site hosts.</summary>
public enum ActivityKind
{
    Party,
    Marriage,
    Ritual,
}

/// <summary>Whether a catalog list of site definitions was read: the defs, or the fact that names what is missing.</summary>
public abstract record SiteDefsRead
{
    private SiteDefsRead() { }

    public sealed record Read(EquatableArray<string> Defs) : SiteDefsRead;

    public sealed record Unread(string Fact) : SiteDefsRead;
}

/// <summary>An attendance memory's catalog row (ThoughtDef): its first stage's mood and its duration, or the fact that names the missing row.</summary>
public abstract record AttendedThought
{
    private AttendedThought() { }

    public sealed record Read(double Mood, double DurationDays) : AttendedThought;

    public sealed record Unread(string Fact) : AttendedThought;
}

/// <summary>
/// The site definitions the catalog names: the buildings native's party worker picks (GatheringDefOf.Party's
/// gatherSpotDefs), the marriage spot (TryFindMarriageSite's first choice) and the ritual spot. Each placed kind
/// builds its spot; a ritual is held at what its pattern's obligation target filter accepts (RitualDef.RequiredBuildings).
/// Party and Wedding are the memories attending one leaves (ThoughtDefOf.AttendedParty, AttendedWedding).
/// </summary>
public sealed record ActivitySiteDefs
{
    public required SiteDefsRead PartySpots { get; init; }

    public required string PartySpot { get; init; }

    public required string MarriageSpot { get; init; }

    public required string RitualSpot { get; init; }

    public required AttendedThought Party { get; init; }

    public required AttendedThought Wedding { get; init; }

    /// <summary>Every definition a site placement builds; the room reconcile carries them over and never removes one.</summary>
    public IReadOnlyList<string> Placed => [PartySpot, MarriageSpot, RitualSpot];
}

/// <summary>
/// One kind's demand: the participants, the attendance native requires (a party's guest minimum, a marriage's couple,
/// a ritual's organizer), the buildings a standing site may be (Accepted) and the one a placement builds (Place, ""
/// when no spot hosts the kind). Unread names a site fact the demand could not read: the kind is then held.
/// </summary>
public sealed record ActivityDemand
{
    public required ActivityKind Kind { get; init; }

    public required EquatableArray<PawnId> Participants { get; init; }

    public required int Minimum { get; init; }

    public required EquatableArray<string> Accepted { get; init; }

    public required string Place { get; init; }

    public EquatableArray<Engagement> Couples { get; init; }

    /// <summary>The held rituals the colony can start on its own (WorshipFacts.Rituals), each priced at the sites it accepts.</summary>
    public IReadOnlyList<RitualDef> Rituals { get; init; } = [];

    public string Unread { get; init; } = "";
}

/// <summary>Which deviation of a kind's site a layout alternative is: a spot in an existing room, a new site room, or none.</summary>
public enum ActivityAlternative
{
    PlaceInRoom,
    NewRoom,
    Defer,
}

/// <summary>One site a layout candidate holds, before tiling: a standing one, a spot in an existing room, or a new site room.</summary>
public abstract record ActivityPlacement(ActivityKind Kind, string Definition)
{
    /// <summary>A site of the kind standing in <paramref name="Room"/> (or "" outdoors) on <paramref name="Anchor"/>: kept as it stands.</summary>
    public sealed record Standing(ActivityKind Kind, string Definition, string Room, Cell Anchor) : ActivityPlacement(Kind, Definition);

    /// <summary>A spot placed on <paramref name="Anchor"/>, a free cell of the existing room <paramref name="Room"/>.</summary>
    public sealed record InRoom(ActivityKind Kind, string Definition, string Room, Cell Anchor) : ActivityPlacement(Kind, Definition);

    /// <summary>A spot in a new site room of the station graph (LayoutPlan.ActivityRoomNodes) of <paramref name="Area"/> interior cells.</summary>
    public sealed record NewRoom(ActivityKind Kind, string Definition, int Area) : ActivityPlacement(Kind, Definition);
}

/// <summary>A site the plan holds (LayoutPlan.ActivitySites), after tiling.</summary>
public sealed record PlannedActivitySite
{
    public ActivityKind Kind { get; init; }

    public string Definition { get; init; } = "";

    /// <summary>The existing room the site stands or is placed in; "" for a site in a planned room.</summary>
    public string NativeRoom { get; init; } = "";

    /// <summary>The planned room (PlannedRoom.Id) of a new site room; 0 for an existing room.</summary>
    public int Room { get; init; }

    /// <summary>The cell the spot stands or is placed on.</summary>
    public Cell Anchor { get; init; }

    /// <summary>Whether the site stands: the plan keeps it and the placement step owes nothing for it.</summary>
    public bool Standing { get; init; }
}

/// <summary>
/// One kind's options for the layout comparator: its demand, whether a site of it is in flight (no alternative is then
/// compared), the standing sites, the unplaced site the plan holds (none or one), a spot in each eligible existing room
/// within <see cref="ActivitySiteRules.MaxRoomTrials"/> and the rooms past the bound (Untried), and a new site room
/// (none or one).
/// </summary>
public sealed record ActivitySiteOptions
{
    public required ActivityDemand Demand { get; init; }

    public bool Building { get; init; }

    public EquatableArray<ActivityPlacement> Standing { get; init; }

    public EquatableArray<ActivityPlacement> Held { get; init; }

    public EquatableArray<ActivityPlacement> Rooms { get; init; }

    public int Untried { get; init; }

    public EquatableArray<ActivityPlacement> NewRoom { get; init; }
}

/// <summary>The activity sites the station facts ask the layout to compare (StationFacts.ActivitySites); its default asks none.</summary>
public sealed record ActivitySiteAsk
{
    public EquatableArray<ActivitySiteOptions> Kinds { get; init; }

    /// <summary>The sites a candidate of this ask holds: every standing site and each kind's held one.</summary>
    public IReadOnlyList<ActivityPlacement> Placements() => [.. Kinds.SelectMany(k => k.Standing.Concat(k.Held))];

    /// <summary>The new site rooms a candidate of this ask holds.</summary>
    public IReadOnlyList<ActivityPlacement.NewRoom> NewRooms() => [.. Kinds.SelectMany(k => k.Held.OfType<ActivityPlacement.NewRoom>())];

    /// <summary>This ask with <paramref name="kind"/>'s held site replaced by <paramref name="held"/> (none to defer).</summary>
    public ActivitySiteAsk With(ActivityKind kind, IReadOnlyList<ActivityPlacement> held) =>
        this with { Kinds = EquatableArray.From(Kinds.Select(k => k.Demand.Kind == kind ? k with { Held = EquatableArray.From(held) } : k).ToList()) };
}

/// <summary>The activity-site deviation a layout alternative is (HostedRegroupInput.Site): none for a host or bed-sharing alternative.</summary>
public abstract record SiteDeviation
{
    private SiteDeviation() { }

    public sealed record None : SiteDeviation;

    /// <summary><paramref name="Kind"/>'s site moved to <paramref name="Alternative"/>, in <paramref name="Room"/> for a spot in an existing room.</summary>
    public sealed record Move(ActivityKind Kind, ActivityAlternative Alternative, string Room) : SiteDeviation;
}

/// <summary>A kind's site rooms past <see cref="ActivitySiteRules.MaxRoomTrials"/>: one skipped decision of the comparison.</summary>
public sealed record SiteTruncation(ActivityKind Kind, int Rooms);

/// <summary>What a kind's site Need reads (RoomsFacts.Sites).</summary>
public abstract record ActivitySiteState(ActivityKind Kind)
{
    /// <summary>The standing sites host the activity and the plan holds no further site: provision is met.</summary>
    public sealed record Standing(ActivityKind Kind) : ActivitySiteState(Kind);

    /// <summary>A site of the kind is a blueprint or frame: the step in flight.</summary>
    public sealed record Building(ActivityKind Kind) : ActivitySiteState(Kind);

    /// <summary>The adopted plan holds an unplaced site of the kind: the placement step binds it.</summary>
    public sealed record Planned(ActivityKind Kind) : ActivitySiteState(Kind);

    /// <summary>No site stands and the plan holds none: the reason names the fact or the state.</summary>
    public sealed record Unplanned(ActivityKind Kind, string Reason) : ActivitySiteState(Kind);
}

public static class ActivitySiteRules
{
    /// <summary>The most existing rooms one kind offers the comparator as placements; the rest are one skipped decision.</summary>
    public const int MaxRoomTrials = 6;

    /// <summary>The regroup decision kind of an activity-site deviation.</summary>
    public const string SiteMove = "activity_site";

    /// <summary>The state a placement whose Method was already admitted leaves when nothing of it stands: never retried.</summary>
    public const string SpentRoom = "activity_site_spent";

    /// <summary>The state of a kind no spot hosts: no definition it accepts can be placed.</summary>
    public const string NoSite = "activity_site_none";

    /// <summary>The state of a kind whose sites the adopted plan does not hold: the comparator kept the baseline without one.</summary>
    public const string Unplanned = "activity_site_unplanned";

    /// <summary>Why the station graph holds a site room.</summary>
    public const string ReasonSiteRoom = "activity_site_room";

    /// <summary>The planned role of a new site room: a recreation room that holds the activity's spot.</summary>
    public const PlannedRole SiteRoomRole = PlannedRole.Rec;

    /// <summary>A room the activity's site may not stand in: native refuses a prison cell, and a bedroom, barracks or hospital is its occupants' rest.</summary>
    private static bool Excluded(RoomRole role) =>
        role is RoomRole.PrisonCell or RoomRole.PrisonBarracks or RoomRole.Bedroom or RoomRole.Barracks or RoomRole.Hospital;

    /// <summary>The room roles whose own work an activity interrupts.</summary>
    private static bool WorkRoom(RoomRole role) => role is RoomRole.Workshop or RoomRole.Kitchen or RoomRole.Laboratory;

    /// <summary>The activity a site room is keyed under (PlannedRoom.Activity).</summary>
    public static string ActivityKey(ActivityKind kind) => Wire.Name(kind);

    /// <summary>
    /// Each kind's demand from facts; a kind with no demand is absent. A party needs native's four free colonists
    /// (GatheringRules.GatheringMinColonists) and takes clamp(round(0.65 n), 2, 10) of them as its guest minimum
    /// (GatheringsUtility.EnoughPotentialGuestsToStartGathering); a marriage needs an engaged couple; a ritual needs
    /// Ideology and a held ritual the colony can start on its own (<paramref name="worship"/>'s rituals, empty without
    /// the expansion), and is held at its believers.
    /// </summary>
    public static IReadOnlyList<ActivityDemand> Demands(ActivityAccess access, ActivitySiteDefs defs, WorshipFacts worship)
    {
        var output = new List<ActivityDemand>(3);
        var colonists = access.Colonists;
        if (colonists.Count >= GatheringRules.GatheringMinColonists)
        {
            var accepted = defs.PartySpots is SiteDefsRead.Read read ? read.Defs : EquatableArray.From(new List<string>());
            var unread = defs.PartySpots is SiteDefsRead.Unread missing ? missing.Fact : "";
            output.Add(new ActivityDemand
            {
                Kind = ActivityKind.Party,
                Participants = colonists,
                Minimum = Math.Clamp((int)Math.Round(GatheringRules.GatheringGuestShare * colonists.Count, MidpointRounding.AwayFromZero), 2, 10),
                Accepted = accepted,
                Place = accepted.Contains(defs.PartySpot) ? defs.PartySpot : "",
                Unread = unread,
            });
        }
        if (access.Engaged.Count > 0)
        {
            output.Add(new ActivityDemand
            {
                Kind = ActivityKind.Marriage,
                Participants = colonists,
                Minimum = 2,
                Accepted = [defs.MarriageSpot],
                Place = defs.MarriageSpot,
                Couples = access.Engaged,
            });
        }
        var rituals = worship.Rituals.Where(r => r.RequiredBuildings.Count > 0 && r.Outcome is not null).ToList();
        if (rituals.Count > 0)
        {
            output.Add(new ActivityDemand
            {
                Kind = ActivityKind.Ritual,
                Participants = EquatableArray.From(worship.Believers.ToList()),
                Minimum = 1,
                Accepted = EquatableArray.From(rituals.SelectMany(r => r.RequiredBuildings).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()),
                Place = rituals.Any(r => r.RequiredBuildings.Contains(defs.RitualSpot)) ? defs.RitualSpot : "",
                Rituals = rituals,
            });
        }
        return output;
    }

    /// <summary>The definitions a standing or in-flight site of the demand may be.</summary>
    private static HashSet<string> Defs(ActivityDemand demand)
    {
        var defs = new HashSet<string>(demand.Accepted, StringComparer.Ordinal);
        if (demand.Place.Length != 0)
        {
            defs.Add(demand.Place);
        }
        return defs;
    }

    private static Dictionary<Cell, string> RoomOf(RoomObservation rooms)
    {
        var roomOf = new Dictionary<Cell, string>();
        foreach (var room in rooms.Rooms)
        {
            foreach (var cell in rooms.CellsOf(room))
            {
                roomOf[cell] = room.Id;
            }
        }
        return roomOf;
    }

    /// <summary>
    /// Each demanded kind's options (<see cref="ActivitySiteOptions"/>) over the colony: a site of an accepted definition in
    /// flight compares nothing; the standing ones are kept; the plan's unplaced site of the kind (<paramref name="plan"/>) is
    /// held while its room stays eligible, its anchor free and its room unspent (a new site room is held while the plan
    /// holds the room); each eligible existing room (indoors, roofed, a proper room, not a rest or prison room, not
    /// hosting the kind, not spent) with a free cell offers a spot on its first free cell, most reachers first, bounded at
    /// <see cref="MaxRoomTrials"/>; and a new site room sized for every participant is offered whenever the kind's spot can
    /// be placed (<paramref name="placeable"/>). A demand with an unread fact offers nothing.
    /// </summary>
    public static ActivitySiteAsk Ask(IReadOnlyList<ActivityDemand> demands, RoomObservation rooms, CurrentConstruction census,
        IReadOnlyList<SiteCell> cells, ComfortFacts comfort, LayoutPlan? plan, Func<ActivityDemand, bool> placeable,
        Func<ActivityKind, IReadOnlySet<string>> spent)
    {
        var roomOf = RoomOf(rooms);
        var free = new HashSet<Cell>(cells.Where(c => c.Walkable && c.Roofed && c.Indoors && !c.Occupied()).Select(c => c.Cell));
        var access = comfort.Activities.Rooms.ToDictionary(r => r.Room, StringComparer.Ordinal);
        var output = new List<ActivitySiteOptions>(demands.Count);
        foreach (var demand in demands)
        {
            if (demand.Unread.Length != 0)
            {
                output.Add(new ActivitySiteOptions { Demand = demand });
                continue;
            }
            var defs = Defs(demand);
            if (census.Sites.Any(s => defs.Contains(s.Building.Definition)))
            {
                output.Add(new ActivitySiteOptions { Demand = demand, Building = true });
                continue;
            }
            var standing = census.Buildings
                .Where(b => demand.Accepted.Contains(b.Building.Definition) && b.Cells.Count > 0)
                .OrderBy(b => b.Id, StringComparer.Ordinal)
                .Select(b => (ActivityPlacement)new ActivityPlacement.Standing(demand.Kind, b.Building.Definition, roomOf.TryGetValue(b.Cells[0], out var r) ? r : "", b.Cells[0]))
                .ToList();
            var hosting = standing.Cast<ActivityPlacement.Standing>().Select(s => s.Room).ToHashSet(StringComparer.Ordinal);
            var used = spent(demand.Kind);
            var participants = new HashSet<PawnId>(demand.Participants);
            var canPlace = demand.Place.Length != 0 && placeable(demand);
            var eligible = rooms.Rooms
                .Where(r => !Excluded(r.Role) && r.ProperRoom && !r.Outdoors && !r.PsychologicallyOutdoors && r.OpenRoofCount == 0 && !hosting.Contains(r.Id) && !used.Contains(r.Id))
                .Select(r => (Room: r, Cells: rooms.CellsOf(r).Where(free.Contains).OrderBy(c => c.Z).ThenBy(c => c.X).ToList()))
                .Where(r => r.Cells.Count > 0)
                .OrderByDescending(r => access.TryGetValue(r.Room.Id, out var a) ? a.Reachers.Count(participants.Contains) : 0)
                .ThenBy(r => r.Room.Id, StringComparer.Ordinal).ToList();
            var options = canPlace
                ? eligible.Take(MaxRoomTrials).Select(r => (ActivityPlacement)new ActivityPlacement.InRoom(demand.Kind, demand.Place, r.Room.Id, r.Cells[0])).ToList()
                : [];
            var held = new List<ActivityPlacement>(1);
            foreach (var site in (plan?.ActivitySites ?? []).Where(s => s.Kind == demand.Kind && !s.Standing && s.Definition == demand.Place && canPlace))
            {
                if (site.Room != 0 && roomOf.TryGetValue(site.Anchor, out var built))
                {
                    // The site room stands: its spot is a placement in that room like any other.
                    if (!used.Contains(built) && free.Contains(site.Anchor))
                    {
                        held.Add(new ActivityPlacement.InRoom(demand.Kind, demand.Place, built, site.Anchor));
                    }
                }
                else if (site.Room != 0)
                {
                    if (plan!.Rooms.FirstOrDefault(r => r.Id == site.Room && r.Activity == ActivityKey(demand.Kind)) is { } room)
                    {
                        held.Add(new ActivityPlacement.NewRoom(demand.Kind, demand.Place, room.Region.Area()));
                    }
                }
                else if (eligible.Any(r => r.Room.Id == site.NativeRoom) && free.Contains(site.Anchor))
                {
                    held.Add(new ActivityPlacement.InRoom(demand.Kind, demand.Place, site.NativeRoom, site.Anchor));
                }
                break;
            }
            var newRoom = canPlace
                ? new List<ActivityPlacement> { new ActivityPlacement.NewRoom(demand.Kind, demand.Place, NewRoomArea(demand)) }
                : [];
            output.Add(new ActivitySiteOptions
            {
                Demand = demand,
                Standing = EquatableArray.From(standing),
                Held = EquatableArray.From(held),
                Rooms = EquatableArray.From(options),
                Untried = canPlace ? Math.Max(0, eligible.Count - MaxRoomTrials) : 0,
                NewRoom = EquatableArray.From(newRoom),
            });
        }
        return new ActivitySiteAsk { Kinds = EquatableArray.From(output) };
    }

    /// <summary>A new site room's interior: a cell for every participant and one for the spot, at least the least core room.</summary>
    private static int NewRoomArea(ActivityDemand demand) =>
        Math.Max(LayoutThroneRules.ThroneMinSide * LayoutThroneRules.ThroneMinSide, demand.Participants.Count + 1);

    /// <summary>
    /// Each kind's deviations from the ask's baseline, each the ask it leaves: a spot in each offered room the baseline does
    /// not hold, the new site room unless the baseline holds it, and no site when the baseline holds one. A kind in flight
    /// or with an unread demand deviates in nothing.
    /// </summary>
    public static IReadOnlyList<(SiteDeviation.Move Deviation, ActivitySiteAsk Ask)> Deviations(ActivitySiteAsk ask)
    {
        var output = new List<(SiteDeviation.Move, ActivitySiteAsk)>();
        foreach (var k in ask.Kinds.Where(k => !k.Building && k.Demand.Unread.Length == 0))
        {
            var kind = k.Demand.Kind;
            var heldRoom = k.Held.OfType<ActivityPlacement.InRoom>().Select(h => h.Room).FirstOrDefault() ?? "";
            foreach (var room in k.Rooms.OfType<ActivityPlacement.InRoom>().Where(r => r.Room != heldRoom))
            {
                output.Add((new SiteDeviation.Move(kind, ActivityAlternative.PlaceInRoom, room.Room), ask.With(kind, [room])));
            }
            if (!k.Held.OfType<ActivityPlacement.NewRoom>().Any())
            {
                foreach (var room in k.NewRoom)
                {
                    output.Add((new SiteDeviation.Move(kind, ActivityAlternative.NewRoom, ""), ask.With(kind, [room])));
                }
            }
            if (k.Held.Count > 0)
            {
                output.Add((new SiteDeviation.Move(kind, ActivityAlternative.Defer, ""), ask.With(kind, [])));
            }
        }
        return output;
    }

    /// <summary>The kinds whose eligible rooms passed <see cref="MaxRoomTrials"/>: each one skipped decision.</summary>
    public static IReadOnlyList<SiteTruncation> Truncations(ActivitySiteAsk ask) =>
        [.. ask.Kinds.Where(k => k.Untried > 0).Select(k => new SiteTruncation(k.Demand.Kind, k.Untried))];

    /// <summary>The host alternative id of a site deviation: distinct per kind, alternative and room, so its trials never share the baseline's evaluation.</summary>
    public static string IdOf(SiteDeviation.Move d) =>
        $"site:{ActivityKey(d.Kind)}:{Wire.Name(d.Alternative)}" + (d.Room.Length == 0 ? "" : ":" + d.Room);

    /// <summary>The regroup decision's B of a site deviation: its alternative and the room it places in.</summary>
    public static string NameOf(SiteDeviation.Move d) => Wire.Name(d.Alternative) + (d.Room.Length == 0 ? "" : ":" + d.Room);

    /// <summary>
    /// The plan's sites after tiling <paramref name="placements"/> into <paramref name="plan"/>: standing and existing-room
    /// sites as they are; a new site room's spot on the first interior cell of the planned room keyed to its kind
    /// (PlannedRoom.Activity) that no planned piece or lamp takes. A new room the tiler did not place holds no site.
    /// </summary>
    public static IReadOnlyList<PlannedActivitySite> Resolve(LayoutPlan plan, IReadOnlyList<ActivityPlacement> placements)
    {
        var output = new List<PlannedActivitySite>(placements.Count);
        foreach (var placement in placements)
        {
            if (placement is ActivityPlacement.Standing standing)
            {
                output.Add(new PlannedActivitySite { Kind = standing.Kind, Definition = standing.Definition, NativeRoom = standing.Room, Anchor = standing.Anchor, Standing = true });
            }
            else if (placement is ActivityPlacement.InRoom inRoom)
            {
                output.Add(new PlannedActivitySite { Kind = inRoom.Kind, Definition = inRoom.Definition, NativeRoom = inRoom.Room, Anchor = inRoom.Anchor });
            }
            else if (placement is ActivityPlacement.NewRoom newRoom)
            {
                if (plan.Rooms.FirstOrDefault(r => r.Activity == ActivityKey(newRoom.Kind)) is not { } room)
                {
                    continue;
                }
                var taken = new HashSet<Cell>(plan.Lamps.GetValueOrDefault(room.Id, []).Select(l => l.Cell));
                foreach (var piece in plan.Pieces.GetValueOrDefault(room.Id, []))
                {
                    taken.UnionWith(LayoutUtilityRules.RectangleCells(piece.Rect));
                }
                var anchor = room.Region.Cells().Where(c => !taken.Contains(c)).OrderBy(c => c.Z).ThenBy(c => c.X).ToList();
                if (anchor.Count == 0)
                {
                    continue;
                }
                output.Add(new PlannedActivitySite { Kind = newRoom.Kind, Definition = newRoom.Definition, Room = room.Id, Anchor = anchor[0] });
            }
            else
            {
                throw new DomainException("unknown site placement");
            }
        }
        return output;
    }

    /// <summary>
    /// Each demanded kind's site state: in flight while a blueprint or frame of an accepted definition stands; planned
    /// while the adopted plan holds a site of it not yet standing on its anchor whose room's placement Method was not already admitted
    /// (<paramref name="spent"/>: whatever its receipt, a spent room is never retried); standing while a site of it stands;
    /// else unplanned, naming the demand's unread fact, the first plan pending, no placeable spot, a spent room or the
    /// comparator's baseline.
    /// </summary>
    public static IReadOnlyList<ActivitySiteState> States(IReadOnlyList<ActivityDemand> demands, CurrentConstruction census,
        RoomObservation rooms, LayoutPlan? plan, Func<ActivityKind, IReadOnlySet<string>> spent)
    {
        var roomOf = RoomOf(rooms);
        var output = new List<ActivitySiteState>(demands.Count);
        foreach (var demand in demands)
        {
            var kind = demand.Kind;
            var defs = Defs(demand);
            if (demand.Unread.Length != 0)
            {
                output.Add(new ActivitySiteState.Unplanned(kind, demand.Unread));
                continue;
            }
            if (census.Sites.Any(s => defs.Contains(s.Building.Definition)))
            {
                output.Add(new ActivitySiteState.Building(kind));
                continue;
            }
            var used = spent(kind);
            var held = plan?.ActivitySites.Where(s => s.Kind == kind && !s.Standing).ToList() ?? [];
            var placed = census.Buildings.Where(b => defs.Contains(b.Building.Definition)).SelectMany(b => b.Cells).ToHashSet();
            var open = held.Where(s => !placed.Contains(s.Anchor) && !used.Contains(s.Room == 0 ? s.NativeRoom : roomOf.GetValueOrDefault(s.Anchor, ""))).ToList();
            var stands = census.Buildings.Any(b => demand.Accepted.Contains(b.Building.Definition));
            output.Add(open.Count > 0 ? new ActivitySiteState.Planned(kind)
                : stands ? new ActivitySiteState.Standing(kind)
                : plan is null ? new ActivitySiteState.Unplanned(kind, LayoutPlanPending)
                : held.Count > 0 ? new ActivitySiteState.Unplanned(kind, SpentRoom)
                : demand.Place.Length == 0 ? new ActivitySiteState.Unplanned(kind, NoSite)
                : new ActivitySiteState.Unplanned(kind, Unplanned));
        }
        return output;
    }

    /// <summary>The state of a kind while no layout plan is held.</summary>
    public const string LayoutPlanPending = "layout_plan";

    /// <summary>
    /// The users of each existing room's other uses an activity there interrupts (the competing uses the ledger prices,
    /// ForecastActivitySites): one for a work room, plus each distinct user of a dining seat or a recreation source in it.
    /// A room with none is absent.
    /// </summary>
    public static IReadOnlyDictionary<string, int> CompetingUses(ComfortFacts comfort, RoomObservation rooms)
    {
        var output = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var room in rooms.Rooms)
        {
            var users = new HashSet<PawnId>();
            foreach (var seat in comfort.Dining.Where(d => d.Room == room.Id))
            {
                users.UnionWith(seat.Users);
            }
            foreach (var source in comfort.Recreation.Where(r => r.Host is ComfortHost.Indoors indoors && indoors.Room == room.Id))
            {
                users.UnionWith(source.Users);
            }
            var uses = users.Count + (WorkRoom(room.Role) ? 1 : 0);
            if (uses > 0)
            {
                output[room.Id] = uses;
            }
        }
        return output;
    }
}
