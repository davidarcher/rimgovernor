using ActivityRoom = RimGovernor.Brain.GameFacts.Observations.ActivityRoom;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the activity-site credit reads (habitat 3.3, ForecastCoupledContext.ActivitySites): each kind's demand, the
/// sites the candidate's plan holds (LayoutPlan.ActivitySites), each existing room's reach and standable cells, the
/// existing rooms' native quality, each existing room's competing users (ActivitySiteRules.CompetingUses) and the
/// attendance memories. Unread names the census a held site set could not be credited without. Its default holds no site.
/// </summary>
public sealed record ActivitySiteContext
{
    public EquatableArray<ActivityDemand> Demands { get; init; }

    public EquatableArray<PlannedActivitySite> Sites { get; init; }

    public EquatableArray<ActivityRoom> Access { get; init; }

    public EquatableArray<UpkeepRoom> Qualities { get; init; }

    public IReadOnlyDictionary<string, int> Uses { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    public AttendedThought Party { get; init; } = new AttendedThought.Unread(ForecastActivitySites.PartyThought);

    public AttendedThought Wedding { get; init; } = new AttendedThought.Unread(ForecastActivitySites.WeddingThought);

    public string Unread { get; init; } = "";

    /// <summary>The context of <paramref name="facts"/>: its plan's sites over its comfort census, rooms and worship; no site without a plan holding one.</summary>
    public static ActivitySiteContext Of(ReviewFacts facts)
    {
        if (facts.LayoutPlan is not { ActivitySites.Count: > 0 } plan)
        {
            return new();
        }
        var sites = EquatableArray.From(plan.ActivitySites);
        if (facts.BasicComfort is not { } comfort)
        {
            return new() { Sites = sites, Unread = "basic comfort census" };
        }
        return new()
        {
            Demands = EquatableArray.From(ActivitySiteRules.Demands(comfort.Census.Activities, comfort.Sites, facts.Worship)),
            Sites = sites,
            Access = comfort.Census.Activities.Rooms,
            Qualities = facts.Sleeping.Rooms,
            Uses = ActivitySiteRules.CompetingUses(comfort.Census, facts.Upkeep.Rooms),
            Party = comfort.Sites.Party,
            Wedding = comfort.Sites.Wedding,
        };
    }
}

/// <summary>
/// The activity-site consequence term of the canonical ledger (habitat 3.3): what the site set a layout candidate holds
/// (LayoutPlan.ActivitySites) gives each participant, the way ForecastHerdShelter's barn credit gives its herd. For each
/// demanded kind and each of its sites:
/// <list type="bullet">
/// <item>Attendance: the participants who reach an existing room (ActivityRoom.Reachers), capped at its standable cells,
/// none when that leaves fewer than the activity's minimum; a planned site room is reached by every participant (named) and holds its interior
/// less the spot. A marriage site hosts only while an engaged couple attends.</item>
/// <item>Party: each attendee's AttendedParty mood over min(duration, the gathering interval) / interval
/// (VoluntarilyJoinableLordsStarter.StartGatheringMTBDays); a marriage: each attendee's AttendedWedding memory once per
/// engaged couple; a ritual: each held ritual at its best accepting site, its expected memory mood (ForecastWorship) at
/// the attendance and the room's impressiveness and cleanliness (the native stats, or the planned room's prediction),
/// none while a planned worship room's credit prices the rituals there or a ritual's outcome is not modelled (ForecastWorship names it).
/// A party or marriage spreads over its sites (native picks among valid spots at random).</item>
/// <item>Competing uses: an existing room's users (ActivitySiteRules.CompetingUses) each lose the gathering's hours, an
/// upper bound (named).</item>
/// </list>
/// Each mood is priced through <see cref="PlannedRoomCredits.MoodGain"/> from the site's service day: now for an existing
/// room, the planned room's own for a new one (its shell the ledger charges like any planned room's). No site is the
/// reference zero: a party or marriage then falls back to a native cell this term does not price (named). A fact the term
/// needs unread throws <see cref="RequiredFactException"/>: the candidate is unassessed naming it.
/// </summary>
internal static class ForecastActivitySites
{
    /// <summary>The unread attendance memories: the catalog's AttendedParty and AttendedWedding rows.</summary>
    internal const string PartyThought = "thought_def:AttendedParty";

    internal const string WeddingThought = "thought_def:AttendedWedding";

    /// <summary>A kind with no site: native falls back to a gathering cell, which the term does not price.</summary>
    internal const string FindingNativeFallback = "activity_site_native_fallback_unpriced";

    /// <summary>A planned site room is assumed reached by every participant.</summary>
    internal const string FindingPlannedReach = "activity_site_planned_reach_assumed";

    /// <summary>A planned site room the outcome does not serve within the horizon earns nothing.</summary>
    internal const string FindingRoomUnserved = "activity_site_room_unserved";

    /// <summary>The competing uses' loss is an upper bound: every user loses the whole gathering.</summary>
    internal const string FindingCompetingBound = "activity_site_competing_use_bound";

    /// <summary>The days between two parties native starts on its own (VoluntarilyJoinableLordsStarter.StartGatheringMTBDays).</summary>
    internal const double PartyIntervalDays = 40;

    /// <summary>A gathering's mean length in days: native's party lord lasts 5000 to 15000 ticks.</summary>
    internal const double GatheringDays = 10000.0 / GameTime.TicksPerDay;

    /// <summary>One attendee's gain at one site: its per-day mood delta, its once memory (delta and days), or a room's competing loss.</summary>
    internal sealed record Gain(ActivityKind Kind, string Site, int Room, string Pawn, double Delta, double OnceDelta, double OnceDays, double Loss);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, IReadOnlyList<Gain>> Cache = new();

    /// <summary>The per-site gains of the context's site set (see <see cref="ForecastActivitySites"/>). Cached per context; throws when a fact it needs is unread.</summary>
    internal static IReadOnlyList<Gain> Gains(ForecastCoupledContext context) => Cache.GetValue(context, GainsOf);

    /// <summary>Per attendee, the distinct shifts (delta / 100) its mood timeline integrates breaks at; none while a fact is unread (the credit then throws).</summary>
    internal static IReadOnlyDictionary<string, double[]> Shifts(ForecastCoupledContext context)
    {
        IReadOnlyList<Gain> gains;
        try
        {
            gains = Gains(context);
        }
        catch (RequiredFactException)
        {
            return new Dictionary<string, double[]>();
        }
        return gains.SelectMany(g => new[] { (g.Pawn, Shift: g.Delta / 100), (g.Pawn, Shift: g.OnceDelta / 100) }).Where(p => p.Pawn.Length != 0 && p.Shift != 0)
            .GroupBy(p => p.Pawn, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(p => p.Shift).Distinct().Order().ToArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The term's rows (ForecastValue): per site and attendee its gain × <see cref="RingClosure.CreditDays"/> of the site's
    /// service day plus its once memory, each a gain (negative), and each existing room's competing loss (positive).
    /// </summary>
    internal static void Credits(ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon,
        List<ForecastInvestmentContribution> rows, SortedSet<string> findings)
    {
        var days = context.InvestmentHorizonDays;
        var input = context.ActivitySites;
        if (days <= 0 || input.Sites.Count == 0)
        {
            return;
        }
        if (input.Unread.Length != 0)
        {
            throw new RequiredFactException(input.Unread);
        }
        foreach (var demand in input.Demands.Where(d => d.Kind != ActivityKind.Ritual && !input.Sites.Any(s => s.Kind == d.Kind)))
        {
            findings.Add(FindingNativeFallback + ":" + ActivitySiteRules.ActivityKey(demand.Kind));
        }
        foreach (var gain in Gains(context))
        {
            var servedAt = 0.0;
            if (gain.Room != 0)
            {
                findings.Add(FindingPlannedReach);
                if (outcome.ServedRooms.FirstOrDefault(s => s.Room == gain.Room) is not { } served)
                {
                    findings.Add(FindingRoomUnserved);
                    continue;
                }
                servedAt = served.ServedAt;
            }
            var units = RingClosure.CreditDays(days, servedAt);
            if (units <= 0)
            {
                continue;
            }
            var key = FormattableString.Invariant($"activity_site/{ActivitySiteRules.ActivityKey(gain.Kind)}/{gain.Site}/{gain.Pawn}");
            if (gain.Loss != 0)
            {
                findings.Add(FindingCompetingBound);
                rows.Add(new("", "", key, units, gain.Loss * units));
                continue;
            }
            var perDay = gain.Delta == 0 ? 0 : PlannedRoomCredits.MoodGain(gain.Delta, gain.Pawn, outcome, context, horizon);
            var once = gain.OnceDelta == 0 ? 0 : PlannedRoomCredits.MoodGain(gain.OnceDelta, gain.Pawn, outcome, context, horizon) * gain.OnceDays;
            rows.Add(new("", gain.Pawn, key, units, -((perDay * units) + once)));
        }
    }

    private static IReadOnlyList<Gain> GainsOf(ForecastCoupledContext context)
    {
        var input = context.ActivitySites;
        var output = new List<Gain>();
        if (input.Sites.Count == 0 || input.Unread.Length != 0)
        {
            return output;
        }
        var access = input.Access.ToDictionary(r => r.Room, StringComparer.Ordinal);
        var quality = input.Qualities.ToDictionary(q => q.Id, q => q.Quality, StringComparer.Ordinal);
        foreach (var demand in input.Demands)
        {
            var sites = input.Sites.Where(s => s.Kind == demand.Kind).ToList();
            if (sites.Count == 0)
            {
                continue;
            }
            var participants = demand.Participants.OrderBy(p => p.Value, StringComparer.Ordinal).ToList();
            List<PawnId> Attendees(PlannedActivitySite site)
            {
                List<PawnId> reach;
                int cap;
                if (site.Room != 0)
                {
                    var planned = context.LayoutRooms.TryGetValue(site.Room, out var room) ? room
                        : throw new RequiredFactException("planned_room_layout:" + site.Room.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    reach = participants;
                    cap = planned.Region.Area() - 1;
                }
                else
                {
                    var row = access.TryGetValue(site.NativeRoom, out var r) ? r : throw new RequiredFactException("activity_room_reach:" + site.NativeRoom);
                    var reachers = row.Reachers.ToHashSet();
                    reach = [.. participants.Where(reachers.Contains)];
                    cap = row.StandableCells;
                }
                var attending = reach.Take(Math.Max(0, cap)).ToList();
                if (attending.Count < demand.Minimum)
                {
                    return [];
                }
                if (demand.Kind == ActivityKind.Marriage)
                {
                    var set = attending.ToHashSet();
                    return demand.Couples.Any(c => set.Contains(c.First) && set.Contains(c.Second)) ? attending : [];
                }
                return attending;
            }
            string Key(PlannedActivitySite site) => site.Room != 0 ? "planned" + site.Room.ToString(System.Globalization.CultureInfo.InvariantCulture) : site.NativeRoom;
            void Losses(PlannedActivitySite site, double perDay)
            {
                if (site.Room == 0 && input.Uses.TryGetValue(site.NativeRoom, out var users) && perDay > 0)
                {
                    output.Add(new Gain(demand.Kind, Key(site) + "/uses", 0, "", 0, 0, 0, users * GatheringDays * perDay / sites.Count));
                }
            }
            if (demand.Kind == ActivityKind.Party)
            {
                var (mood, duration) = Thought(input.Party);
                var delta = mood * Math.Min(duration, PartyIntervalDays) / PartyIntervalDays / sites.Count;
                foreach (var site in sites)
                {
                    output.AddRange(Attendees(site).Select(p => new Gain(demand.Kind, Key(site), site.Room, p.Value, delta, 0, 0, 0)));
                    Losses(site, 1 / PartyIntervalDays);
                }
            }
            else if (demand.Kind == ActivityKind.Marriage)
            {
                var (mood, duration) = Thought(input.Wedding);
                var onceDays = duration * demand.Couples.Count / sites.Count;
                foreach (var site in sites)
                {
                    output.AddRange(Attendees(site).Select(p => new Gain(demand.Kind, Key(site), site.Room, p.Value, 0, mood, onceDays, 0)));
                    Losses(site, demand.Couples.Count / Math.Max(context.InvestmentHorizonDays, 1));
                }
            }
            else
            {
                if (context.PlannedRoomRoles.Values.Contains(PlannedRole.Worship))
                {
                    // The worship room's credit already prices each ritual at that room (ForecastWorship).
                    continue;
                }
                foreach (var (ritual, index) in demand.Rituals.Select((r, i) => (r, i)))
                {
                    if (ritual.Outcome is not { Unread.Length: 0 } model)
                    {
                        continue;
                    }
                    (PlannedActivitySite Site, List<PawnId> Attending, double Mood)? best = null;
                    foreach (var site in sites.Where(s => ritual.RequiredBuildings.Contains(s.Definition)))
                    {
                        var attending = Attendees(site);
                        if (Surroundings(context, site, quality) is not { } stats)
                        {
                            continue;
                        }
                        var mood = Methods.Generation.RitualPricing.ExpectedMood(model, Methods.Generation.RitualPricing.Quality(model, attending.Count, stats.Impressiveness, stats.Cleanliness), ritual.IntervalDaysMin);
                        if (best is null || mood * attending.Count > best.Value.Mood * best.Value.Attending.Count)
                        {
                            best = (site, attending, mood);
                        }
                    }
                    if (best is { } chosen && chosen.Mood != 0)
                    {
                        output.AddRange(chosen.Attending.Select(p => new Gain(demand.Kind, Key(chosen.Site) + "/ritual" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), chosen.Site.Room, p.Value, chosen.Mood, 0, 0, 0)));
                        if (ritual.IntervalDaysMin > 0)
                        {
                            Losses(chosen.Site, sites.Count / ritual.IntervalDaysMin);
                        }
                    }
                }
            }
        }
        return output;
    }

    private static (double Mood, double DurationDays) Thought(AttendedThought thought) =>
        thought is AttendedThought.Read read ? (read.Mood, read.DurationDays)
        : thought is AttendedThought.Unread unread ? throw new RequiredFactException(unread.Fact)
        : throw new DomainException("unknown attended thought");

    /// <summary>A site room's impressiveness and cleanliness: an existing room's native stats, a planned room's prediction (none for a shape whose ring closes no room).</summary>
    private static (double Impressiveness, double Cleanliness)? Surroundings(ForecastCoupledContext context, PlannedActivitySite site, Dictionary<string, RoomQuality> quality)
    {
        if (site.Room == 0)
        {
            return quality.TryGetValue(site.NativeRoom, out var q) ? (q.Impressiveness, q.Cleanliness)
                : throw new RequiredFactException("activity_room_quality:" + site.NativeRoom);
        }
        if (PlannedRoomCredits.Prediction(context, site.Room, "") is not { } closure)
        {
            return null;
        }
        var planned = context.LayoutRooms.TryGetValue(site.Room, out var room) ? room
            : throw new RequiredFactException("planned_room_layout:" + site.Room.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (closure.Prediction.Impressiveness, PlannedRoomCleanlinessRules.Steady(closure.Prediction, planned.FilthRuns.Values, context.Filth).Dirty);
    }
}
