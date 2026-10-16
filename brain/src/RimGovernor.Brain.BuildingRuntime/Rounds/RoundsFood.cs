namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the food family (#3254): its read (the room census the kitchen, the butchery and a superseded
/// cooking campfire stand in, with the colony's construction claims, the food plan and the field budget) and the work
/// of its ready Needs, committed under the Need and admitted by claims. The supply Need's work is its butcher bench,
/// its forage and hunts, and its fishing zones, grower crops and food fields; the cooking Need's is its campfire,
/// paste dispenser, kitchen and stove.
/// </summary>
public sealed class FoodRounds
{
    private readonly Rounder _reviewer;
    private readonly bool _supply;
    private readonly RoundsBuildingPlanner? _butcher;
    private readonly RoundsAcquisitionPlanner? _acquisition;
    private readonly RoundsFieldPlanner? _fields;
    private readonly RoundsBuildingPlanner? _cooking;

    /// <summary>
    /// With <paramref name="supply"/> the family derives food/supply and works it through whichever of
    /// <paramref name="butcher"/> (NewRoundsButcherPlanner), <paramref name="acquisition"/> and
    /// <paramref name="fields"/> are composed; a <paramref name="cooking"/> planner (NewRoundsCookingPlanner) derives
    /// food/cooking and does its building work.
    /// </summary>
    public FoodRounds(Rounder reviewer, bool supply, RoundsBuildingPlanner? butcher, RoundsAcquisitionPlanner? acquisition, RoundsFieldPlanner? fields, RoundsBuildingPlanner? cooking)
    {
        _reviewer = reviewer ?? throw new ControlException("FoodRounds: reviewer is missing");
        if (!supply && cooking is null)
        {
            throw new ControlException("FoodRounds: no food Need is composed");
        }
        if (cooking is not null && cooking.Need != PolicyFunctions.CookingNeed)
        {
            throw new ControlException("FoodRounds: the cooking planner is not owned by the food/cooking Need");
        }
        if (butcher is not null && (!supply || butcher.Need != PolicyFunctions.FoodSupplyNeed))
        {
            throw new ControlException("FoodRounds: the butcher planner is not owned by the food/supply Need");
        }
        if (!supply && (acquisition is not null || fields is not null))
        {
            throw new ControlException("FoodRounds: supply work is composed without the food/supply Need");
        }
        _supply = supply;
        _butcher = butcher;
        _acquisition = acquisition;
        _fields = fields;
        _cooking = cooking;
    }

    /// <summary>The family's scheduler entry: it wakes on the colony facts and on its building, zone, crop, acquisition and bill outcomes.</summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<FoodFacts>(new FoodFamily(_supply, _cooking is not null), ReadAsync, Schedule, WorkAsync);

    /// <summary>The outcomes the family's work commits: buildings, zones and their cells, grower crops and acquisitions.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Kinds = [ActionKind.Building, ActionKind.ZoneCreate, ActionKind.ZoneCellEdit, ActionKind.GrowerCrop, ActionKind.Acquisition, ActionKind.AcquisitionWithdraw],
    };

    /// <summary>The work of a ready Need: each composed supply step in turn, or the cooking Need's buildings.</summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, FoodFacts facts, CancellationToken call)
    {
        if (work.Need.Id == PolicyFunctions.CookingNeed && _cooking is not null)
        {
            return (await _cooking.Copy().StepAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false)).Verdict;
        }
        if (work.Need.Id != PolicyFunctions.FoodSupplyNeed || !_supply)
        {
            return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        }
        // The supply steps are independent work under one Need: each runs, held only by its claims and its own
        // MethodIds. The Need reports the first admission, else the first step's reason.
        var verdicts = new List<Verdict>();
        if (_butcher is not null)
        {
            verdicts.Add((await _butcher.Copy().StepAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false)).Verdict);
        }
        if (_acquisition is not null)
        {
            verdicts.Add((await _acquisition.StepAsync(work.Arbiter!, call, work.Epoch, work.Owner).ConfigureAwait(false)).Verdict);
        }
        if (_fields is not null)
        {
            verdicts.Add((await _fields.StepAsync(work.Arbiter!, call, work.Epoch, work.Owner).ConfigureAwait(false)).Verdict);
        }
        return SupplyVerdict(work.Need.Id, verdicts);
    }

    /// <summary>The first admission among the supply steps' verdicts, else the first verdict, else no method.</summary>
    internal static Verdict SupplyVerdict(NeedId need, IReadOnlyList<Verdict> verdicts)
    {
        foreach (var verdict in verdicts)
        {
            if (verdict == BuildingReasonAdmitted)
            {
                return verdict;
            }
        }
        return verdicts.Count > 0 ? verdicts[0] : Verdict.AwaitingPlan(need.Value, "no_method");
    }

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world: the room census with the colony's construction
    /// claims, so a superseded campfire already being taken down is seen. Throws when the read is from another world,
    /// older than the review, or slower than the review's age bound.
    /// </summary>
    internal async Task<FoodFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        var started = _reviewer.Clock.Now();
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScopeAsync(_reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("food read: the scope is another world or older than the review");
        }
        var claims = _reviewer.Player.Journal.ConstructionClaims(scope.World, expected.Tick, ct);
        var reading = await _reviewer.ObserveRoomsAsync(_reviewer.Native, expected, claims, ct).ConfigureAwait(false);
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("food read: the read took too long");
        }
        return Facts(reading.Projection, _reviewer.Policy);
    }

    /// <summary>
    /// The family's facts in <paramref name="projection"/>: the field coverage against the season's reserve, the
    /// supply checks and the cooking fact, and the campfire it owes down.
    /// </summary>
    internal static FoodFacts Facts(ColonyProjection projection, RoundsPolicy policy)
    {
        var budgeted = projection.ApplyFieldBudget(policy.Seasonal(projection.Facts.Calendar, projection.Facts.DisasterConditions).FoodTargetDays);
        return FoodFacts.From(budgeted.Facts, policy) with { CampfireRetireOwed = CampfireRetireOwed(projection) };
    }
}
