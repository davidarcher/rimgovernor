using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

partial class BuildingPlanner
{
    /// <summary>The activity whose site this planner places (habitat 3.3); only read on an activity site planner.</summary>
    internal ActivityKind SiteKind { get; set; }

    /// <summary>The existing room the site step places in, from the adopted plan's site.</summary>
    internal string SiteRoom { get; set; } = "";

    /// <summary>The MethodId of placing <paramref name="kind"/>'s site in <paramref name="room"/>: one Method per room, never retried once admitted.</summary>
    internal static MethodId SitePlaceMethod(ActivityKind kind, string room)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes("room:" + room));
        return new MethodId($"{SitePrefix(kind)}-site-place-{Convert.ToHexStringLower(sum.AsSpan(0, 8))}");
    }

    private static string SitePrefix(ActivityKind kind) =>
        kind == ActivityKind.Party ? "party" : kind == ActivityKind.Marriage ? "marriage" : "ritual";

    /// <summary>
    /// The census rooms a placement Method of <paramref name="kind"/> was already admitted for under the Need's unretired
    /// Methods. Whatever its receipt (accepted, refused, unsent, unconfirmed), the next review reads the room as spent and
    /// does not offer it again: a refusal is a fact, never a retry loop.
    /// </summary>
    internal static IReadOnlySet<string> SiteSpent(ActivityKind kind, IEnumerable<MethodId> methods, RoomObservation rooms)
    {
        var used = methods.ToHashSet();
        return rooms.Rooms.Select(r => r.Id).Where(id => used.Contains(SitePlaceMethod(kind, id))).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Each demanded kind of <paramref name="facts"/> (ActivitySiteRules.Demands over the comfort census and the ideoligion).</summary>
    private static IReadOnlyList<ActivityDemand> SiteDemands(ColonyReading facts)
    {
        var comfort = facts.Facts.RequireBasicComfort();
        return ActivitySiteRules.Demands(comfort.Census.Activities, comfort.Sites, WorshipRules.FactsOf(facts.Facts.Ideology));
    }

    /// <summary>
    /// The activity sites the layout comparator weighs over <paramref name="plan"/> (StationFacts.ActivitySites,
    /// ActivitySiteRules.Ask); <paramref name="methods"/> gives each kind's Need's unretired Methods, whose rooms are spent.
    /// None while the comfort census is unread.
    /// </summary>
    internal static ActivitySiteAsk SiteAsk(ColonyReading facts, LayoutPlan? plan, Func<ActivityKind, IEnumerable<MethodId>> methods)
    {
        if (facts.Facts.BasicComfort is not { } comfort)
        {
            return new();
        }
        var rooms = facts.Facts.Upkeep.Rooms;
        return ActivitySiteRules.Ask(SiteDemands(facts), rooms, facts.Facts.CurrentConstruction, facts.PlanningWindow.Cells, comfort.Census, plan,
            d => ComfortBuilderAvailable(facts, d.Place), k => SiteSpent(k, methods(k), rooms));
    }

    /// <summary>Each demanded kind's site state in <paramref name="facts"/> against the adopted plan; <paramref name="methods"/> gives the kind's Need's unretired Methods.</summary>
    internal static EquatableArray<ActivitySiteState> SiteStates(ColonyReading facts, Func<ActivityKind, IEnumerable<MethodId>> methods)
    {
        var rooms = facts.Facts.Upkeep.Rooms;
        return EquatableArray.From(ActivitySiteRules.States(SiteDemands(facts), facts.Facts.CurrentConstruction, rooms, facts.Facts.LayoutPlan,
            k => SiteSpent(k, methods(k), rooms)));
    }

    /// <summary>
    /// The site step binds the adopted plan's site of the kind without choosing again (the layout comparator chose it):
    /// a site in an existing room, or in a planned site room once that room stands, resolves the planner to the plan's
    /// definition on its anchor (its Method the room's); a planned room not yet standing waits for its shell; a site in
    /// flight waits; an unplanned kind is the family's held Method; a standing site owes nothing. There is no retire,
    /// move or upgrade: a standing site is never touched.
    /// </summary>
    internal (BuildingResult Result, BuildingPlanner? Placing) ActivitySiteStep(NeedOwner goal, ColonyReading reading)
    {
        var state = SiteStates(reading, _ => goal.OpenMethods.Select(m => m.Method)).FirstOrDefault(s => s.Kind == SiteKind);
        if (state is ActivitySiteState.Building)
        {
            return (new BuildingResult { Verdict = WaitFor(Pol.Cause.Facility, "activity_site_building") }, null);
        }
        if (state is ActivitySiteState.Unplanned unplanned)
        {
            return (new BuildingResult { Verdict = AwaitingPlan(RoomsFamily.SiteNeed(SiteKind).Value, unplanned.Reason) }, null);
        }
        if (state is not ActivitySiteState.Planned || reading.Facts.LayoutPlan is not { } plan)
        {
            return (new BuildingResult { Verdict = BuildingReasonNoDeficit }, null);
        }
        var rooms = reading.Facts.Upkeep.Rooms;
        var roomOf = rooms.Rooms.SelectMany(r => rooms.CellsOf(r).Select(c => (Cell: c, r.Id))).GroupBy(p => p.Cell).ToDictionary(g => g.Key, g => g.First().Id);
        var site = plan.ActivitySites.First(s => s.Kind == SiteKind && !s.Standing);
        var room = site.Room == 0 ? site.NativeRoom : roomOf.GetValueOrDefault(site.Anchor, "");
        if (room.Length == 0)
        {
            return (new BuildingResult { Verdict = WaitFor(Pol.Cause.Facility, SiteRoomUnbuilt) }, null);
        }
        var placing = Copy();
        placing.Definition = site.Definition;
        placing.Cells = [site.Anchor];
        placing.SiteRoom = room;
        return (new BuildingResult(), placing);
    }

    /// <summary>The planned site room's shell does not stand yet: the spot waits for it.</summary>
    internal const string SiteRoomUnbuilt = "activity_site_room_unbuilt";

    /// <summary>The rooms/party-site, rooms/marriage-site or rooms/ritual-site Need's planner: one spot placed where the adopted plan holds it.</summary>
    public static BuildingPlanner NewReviewActivitySitePlanner(ColonyReview reviewer, IReviewBuildingSource native, ActivityKind kind)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewActivitySitePlanner: reviewer or native is missing");
        }
        if (native is not IReviewSource)
        {
            throw new ControlException("NewReviewActivitySitePlanner: native is not a rounds source");
        }
        var need = RoomsFamily.SiteNeed(kind);
        return new BuildingPlanner
        {
            Reviewer = reviewer, Native = native, Concern = need.FilingKey, Need = need, Phase = Pol.Phase.ActivitySite, SiteKind = kind,
            Definition = Bridge.ActivitySiteReads.PlaceDefinition(kind), Environment = Pol.PlacementEnvironment.Indoors,
        };
    }
}
