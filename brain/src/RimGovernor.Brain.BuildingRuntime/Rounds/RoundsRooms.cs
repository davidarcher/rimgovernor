using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the rooms family (#3255): its read (the room census with its lighting, terrain and comfort,
/// the colony's construction claims, the review's proof-of-use history, the party-spot Need's spent Methods and the
/// mood census) and the work of its ready Needs, committed under the Need and admitted by claims. The lighting,
/// flooring and four comfort Needs each work through their own building planner; the art Need's sculpture bills
/// are the work ledger's (<see cref="RoundsArtPlanner"/>), and a party is held by the family's Propose.
/// </summary>
public sealed class RoomsRounds
{
    private readonly Rounder _reviewer;
    private readonly RoundsBuildingPlanner? _lighting;
    private readonly RoundsBuildingPlanner? _flooring;
    private readonly RoundsBuildingPlanner? _comfort;
    private readonly RoundsBuildingPlanner? _hosting;
    private readonly RoundsBuildingPlanner? _partySpot;
    private readonly RoundsBuildingPlanner? _workChairs;
    private readonly bool _art;
    private readonly bool _gatherings;

    /// <summary>
    /// A <paramref name="lighting"/> or <paramref name="flooring"/> planner derives rooms/lighting or rooms/flooring;
    /// the <paramref name="comfort"/>, <paramref name="hosting"/>, <paramref name="partySpot"/> and <paramref name="workChairs"/> planners, composed
    /// together, derive rooms/comfort, rooms/hosting, rooms/party-spot and rooms/work-chairs. <paramref name="art"/> and
    /// <paramref name="gatherings"/> derive rooms/art and rooms/gatherings.
    /// </summary>
    public RoomsRounds(Rounder reviewer, RoundsBuildingPlanner? lighting, RoundsBuildingPlanner? flooring,
        RoundsBuildingPlanner? comfort, RoundsBuildingPlanner? hosting, RoundsBuildingPlanner? partySpot, RoundsBuildingPlanner? workChairs, bool art, bool gatherings)
    {
        _reviewer = reviewer ?? throw new ControlException("RoomsRounds: reviewer is missing");
        if ((comfort is null) != (hosting is null) || (comfort is null) != (partySpot is null) || (comfort is null) != (workChairs is null))
        {
            throw new ControlException("RoomsRounds: the comfort, hosting, party-spot and work-chair planners are composed together");
        }
        if (lighting is null && flooring is null && comfort is null && !art && !gatherings)
        {
            throw new ControlException("RoomsRounds: no rooms Need is composed");
        }
        Owned(lighting, RoomsFamily.Lighting);
        Owned(flooring, RoomsFamily.Flooring);
        Owned(comfort, RoomsFamily.Comfort);
        Owned(hosting, RoomsFamily.Hosting);
        Owned(partySpot, RoomsFamily.PartySpot);
        Owned(workChairs, RoomsFamily.WorkChairs);
        _lighting = lighting;
        _flooring = flooring;
        _comfort = comfort;
        _hosting = hosting;
        _partySpot = partySpot;
        _workChairs = workChairs;
        _art = art;
        _gatherings = gatherings;
    }

    private static void Owned(RoundsBuildingPlanner? planner, NeedId need)
    {
        if (planner is not null && (planner.Need != need || planner.Concern != need.FilingKey))
        {
            throw new ControlException($"RoomsRounds: a planner is not owned by the {need.Value} Need");
        }
    }

    private bool ComfortComposed => _comfort is not null;

    /// <summary>The family's scheduler entry: it wakes on the room facts and on its building outcomes.</summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<RoomsFacts>(new RoomsFamily(_lighting is not null, _flooring is not null, _art, ComfortComposed, _gatherings), ReadAsync, Schedule, WorkAsync);

    /// <summary>The outcomes the family's work commits: buildings (lamps, floors, furniture and the PartySpot).</summary>
    public static FamilySchedule Schedule { get; } = new() { Kinds = [ActionKind.Building] };

    /// <summary>
    /// The art Need's work: none of the family's own. Its one remedy is the pinned sculpture bill the work ledger
    /// places (<see cref="RoundsArtPlanner"/>).
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
            : id == RoomsFamily.PartySpot ? _partySpot
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

    /// <summary>The definitions the read carries: the lamps, the floors the traffic tier prices, and the PartySpot.</summary>
    private IReadOnlyList<string> Definitions()
    {
        var definitions = new List<string>();
        if (_lighting is not null)
        {
            definitions.AddRange(_lighting.LightingDefinitions());
        }
        if (_flooring is not null)
        {
            definitions.AddRange(_flooring.FlooringDefinitions());
        }
        if (ComfortComposed)
        {
            definitions.Add(PartySpotRules.PartySpotDefinition);
        }
        return [.. definitions.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world: the room census with the colony's construction
    /// claims, the review's comfort history and the party-spot Need's Methods. Throws when the read is from another
    /// world, older than the review, or slower than the review's age bound.
    /// </summary>
    internal async Task<RoomsFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        var started = _reviewer.Clock.Now();
        var journal = _reviewer.Player.Journal;
        var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("rooms read: the scope is another world or older than the review");
        }
        var claims = journal.ConstructionClaims(scope.World, expected.Tick, ct);
        var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, Definitions(), ct).ConfigureAwait(false);
        var spot = ComfortComposed ? journal.LoadNeed(RoomsFamily.PartySpot, scope.World, ct) : null;
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("rooms read: the read took too long");
        }
        var facts = Facts(reading.Projection, _reviewer.Policy, review.Comfort, spot, _flooring);
        return _gatherings ? facts with { Gathering = Rounder.GatheringPlanOf(reading, scope.World) } : facts;
    }

    /// <summary>
    /// The family's facts in <paramref name="projection"/>, each measured fresh: the lighting review with the plan's
    /// unlit rooms, the flooring review of the traffic-priced census, the artists and owed sculpture rooms, the
    /// basic and hosted comfort against <paramref name="history"/>, the PartySpot step against the
    /// <paramref name="spot"/> Need's Methods, and the work chairs the plan owes standing benches.
    /// </summary>
    internal static RoomsFacts Facts(ColonyProjection projection, RoundsPolicy policy, ComfortHistory history, NeedOwner? spot, RoundsBuildingPlanner? flooring)
    {
        var lighting = RoundsBuildingPlanner.LightingReviewOf(projection, policy.Lighting);
        var census = flooring is not null ? flooring.FlooringCensus(projection) : RoundsBuildingPlanner.TrafficFlooringFacts(projection, policy.Flooring);
        var floorReview = FlooringRules.ReviewFlooring(census, projection.Rooms, policy.Flooring);
        var profiles = projection.Facts.WorkProfiles;
        var hosting = ComfortRules.ReviewComfort(projection.Facts.RequireComfort(), history, projection.Identity.Tick);
        return new RoomsFacts
        {
            Lighting = lighting,
            Flooring = floorReview,
            Profiles = profiles,
            SculptureRoomsOwed = RoundsSleepingUpkeepPlanner.SculptureRoomsOwedOf(projection),
            BasicComfort = projection.Facts.BasicComfort,
            Hosting = hosting,
            PartySpotOwed = Rounder.PartySpotOwed(spot, projection),
            WorkChairsOwed = WorkChairsOwed(projection),
        };
    }

    /// <summary>Whether a standing bench's chosen work chair is missing; unknown while the plan or the census is unread.</summary>
    internal static bool WorkChairsOwed(ColonyProjection projection) =>
        WorkChairRules.OwedSeats(projection.LayoutPlan ?? throw new RequiredFactException("layout plan"), projection.Facts.CurrentConstruction, projection.Shapes).Count > 0;
}
