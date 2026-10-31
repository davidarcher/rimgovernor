using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The runtime half of the rooms family (#3255): its read (the room census with its lighting, terrain and comfort,
/// the colony's construction claims, the review's proof-of-use history, the activity site Needs' spent Methods and the
/// mood census) and the work of its ready Needs, committed under the Need and admitted by claims. The lighting,
/// flooring, comfort and activity site Needs each work through their own building planner; the art Need's sculpture bills
/// are the work ledger's (<see cref="ArtPlanner"/>), and a party is held by the family's Propose.
/// </summary>
public sealed class RoomsReview
{
    private readonly ColonyReview _reviewer;
    private readonly BuildingPlanner? _lighting;
    private readonly BuildingPlanner? _flooring;
    private readonly BuildingPlanner? _comfort;
    private readonly BuildingPlanner? _hosting;
    private readonly IReadOnlyList<BuildingPlanner> _sites;
    private readonly BuildingPlanner? _workChairs;
    private readonly bool _art;
    private readonly bool _gatherings;

    /// <summary>
    /// A <paramref name="lighting"/> or <paramref name="flooring"/> planner derives rooms/lighting or rooms/flooring;
    /// the <paramref name="comfort"/>, <paramref name="hosting"/>, activity <paramref name="sites"/> (one per kind) and
    /// <paramref name="workChairs"/> planners, composed together, derive rooms/comfort, rooms/hosting, the activity site
    /// Needs and rooms/work-chairs. <paramref name="art"/> and <paramref name="gatherings"/> derive rooms/art and rooms/gatherings.
    /// </summary>
    public RoomsReview(ColonyReview reviewer, BuildingPlanner? lighting, BuildingPlanner? flooring,
        BuildingPlanner? comfort, BuildingPlanner? hosting, IReadOnlyList<BuildingPlanner> sites, BuildingPlanner? workChairs, bool art, bool gatherings)
    {
        _reviewer = reviewer ?? throw new ControlException("RoomsReview: reviewer is missing");
        sites ??= [];
        if ((comfort is null) != (hosting is null) || (comfort is null) != (sites.Count == 0) || (comfort is null) != (workChairs is null))
        {
            throw new ControlException("RoomsReview: the comfort, hosting, activity site and work-chair planners are composed together");
        }
        if (sites.Count != 0 && !Enum.GetValues<ActivityKind>().All(k => sites.Count(s => s.Need == RoomsFamily.SiteNeed(k)) == 1))
        {
            throw new ControlException("RoomsReview: one activity site planner is composed per kind");
        }
        if (lighting is null && flooring is null && comfort is null && !art && !gatherings)
        {
            throw new ControlException("RoomsReview: no rooms Need is composed");
        }
        Owned(lighting, RoomsFamily.Lighting);
        Owned(flooring, RoomsFamily.Flooring);
        Owned(comfort, RoomsFamily.Comfort);
        Owned(hosting, RoomsFamily.Hosting);
        foreach (var site in sites)
        {
            Owned(site, RoomsFamily.SiteNeed(site.SiteKind));
        }
        Owned(workChairs, RoomsFamily.WorkChairs);
        _lighting = lighting;
        _flooring = flooring;
        _comfort = comfort;
        _hosting = hosting;
        _sites = sites;
        _workChairs = workChairs;
        _art = art;
        _gatherings = gatherings;
    }

    private static void Owned(BuildingPlanner? planner, NeedId need)
    {
        if (planner is not null && (planner.Need != need || planner.Concern != need.FilingKey))
        {
            throw new ControlException($"RoomsReview: a planner is not owned by the {need.Value} Need");
        }
    }

    private bool ComfortComposed => _comfort is not null;

    /// <summary>The family's scheduler entry: it wakes on the room facts and on its building outcomes.</summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<RoomsFacts>(new RoomsFamily(_lighting is not null, _flooring is not null, _art, ComfortComposed, _gatherings), ReadAsync, Schedule, WorkAsync);

    /// <summary>The outcomes the family's work commits: buildings (lamps, floors, furniture and the activity sites).</summary>
    public static FamilySchedule Schedule { get; } = new() { Kinds = [ActionKind.Building] };

    /// <summary>
    /// The art Need's work: none of the family's own. Its one remedy is the pinned sculpture bill the work ledger
    /// places (<see cref="ArtPlanner"/>).
    /// </summary>
    internal static readonly Verdict ArtBillVerdict = Verdict.WaitFor(Cause.Facility, "art_bills");

    /// <summary>The work of a ready Need: its building planner's step, or the ledger's sculpture bills.</summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, RoomsFacts facts, CancellationToken call)
    {
        var id = work.Need.Id;
        var planner = id == RoomsFamily.Lighting ? _lighting
            : id == RoomsFamily.Flooring ? _flooring
            : id == RoomsFamily.Comfort ? _comfort
            : id == RoomsFamily.Hosting ? _hosting
            : RoomsFamily.SiteStep(id.FilingKey) ? _sites.FirstOrDefault(s => s.Need == id)
            : id == RoomsFamily.WorkChairs ? _workChairs
            : null;
        if (planner is not null)
        {
            return (await planner.Copy().StepAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false)).Verdict;
        }
        if (id == RoomsFamily.Art)
        {
            return ArtBillVerdict;
        }
        // The gatherings Need is held by Propose; the runner filed its verdict.
        return Verdict.AwaitingPlan(id.Value, "no_method");
    }

    /// <summary>The activity site spots, whose builder gate the decisions read; lamps and floors join ordinary catalog composition.</summary>
    private IReadOnlyList<string> Definitions() => ComfortComposed ? Bridge.ActivitySiteReads.Placed : [];

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world: the room census with the colony's construction
    /// claims, the review's comfort history and the activity site Needs' Methods. Throws when the read is from another
    /// world, older than the review, or slower than the review's age bound.
    /// </summary>
    internal async Task<RoomsFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        var started = _reviewer.Clock.Now();
        var journal = _reviewer.Player.Journal;
        var review = await journal.LoadReviewAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, ct).ConfigureAwait(false);
        if (!BuildingPlanner.BuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("rooms read: the scope is another world or older than the review");
        }
        var claims = journal.ConstructionClaims(scope.World, expected.Tick, ct);
        var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, Definitions(), ct).ConfigureAwait(false);
        var sites = ComfortComposed
            ? Enum.GetValues<ActivityKind>().ToDictionary(k => k, k => journal.LoadNeed(RoomsFamily.SiteNeed(k), scope.World, ct))
            : null;
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("rooms read: the read took too long");
        }
        var facts = Facts(reading.Reading, _reviewer.Policy, review.Comfort, sites, _flooring);
        return _gatherings ? facts with { Gathering = ColonyReview.GatheringPlanOf(reading, scope.World) } : facts;
    }

    /// <summary>
    /// The family's facts in <paramref name="projection"/>, each measured fresh: the lighting review with the plan's
    /// unlit rooms, the flooring review of the traffic-priced census, the artists and owed sculpture rooms, the
    /// basic and hosted comfort against <paramref name="history"/>, each activity's site state against the adopted plan and its Need's
    /// Methods (<paramref name="sites"/>, none while the comfort Needs are not composed), and the work chairs the plan
    /// owes standing benches.
    /// </summary>
    internal static RoomsFacts Facts(ColonyReading projection, PlannerSettings policy, ComfortHistory history, IReadOnlyDictionary<ActivityKind, NeedOwner>? sites, BuildingPlanner? flooring)
    {
        var pending = projection.Facts.LayoutPlan is null;
        var census = flooring is not null ? flooring.FlooringCensus(projection) : BuildingPlanner.TrafficFlooringFacts(projection);
        var floorReview = FlooringRules.ReviewFlooring(census, projection.Facts.Upkeep.Rooms, policy.Flooring);
        var profiles = projection.Facts.WorkProfiles;
        var hosting = ComfortRules.ReviewComfort(projection.Facts.RequireComfort(), history, projection.Identity.Tick);
        var facts = new RoomsFacts
        {
            LayoutPlanPending = pending,
            Flooring = floorReview,
            Profiles = profiles,
            SculptureRoomsOwed = SleepingUpkeepPlanner.SculptureRoomsOwedOf(projection),
            BasicComfort = projection.Facts.BasicComfort,
            Hosting = hosting,
            Sites = sites is null ? [] : BuildingPlanner.SiteStates(projection, k => sites[k].Methods.Select(m => m.Method)),
        };
        return pending ? facts : facts with
        {
            Lighting = BuildingPlanner.LightingReviewOf(projection, policy.Lighting),
            WorkChairsOwed = WorkChairsOwed(projection),
        };
    }

    /// <summary>Whether a standing bench's chosen work chair is missing; unknown while the plan or the census is unread.</summary>
    internal static bool WorkChairsOwed(ColonyReading projection) =>
        WorkChairRules.OwedSeats(projection.Facts.LayoutPlan ?? throw new RequiredFactException("layout plan"), projection.Facts.CurrentConstruction, projection.Facts.Shapes).Count > 0;
}
