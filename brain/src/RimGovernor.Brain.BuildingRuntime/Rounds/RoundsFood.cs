namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the food family (#3254): its read (the room census the kitchen and a superseded cooking
/// campfire stand in, with the colony's construction claims) and the cooking Need's building work, which the shared
/// building planner commits under the Need and its claims admit.
/// </summary>
public sealed class FoodRounds
{
    private readonly Rounder _reviewer;
    private readonly RoundsBuildingPlanner? _cooking;

    /// <summary>A <paramref name="cooking"/> planner (NewRoundsCookingPlanner) derives food/cooking and does its building work.</summary>
    public FoodRounds(Rounder reviewer, RoundsBuildingPlanner? cooking)
    {
        _reviewer = reviewer ?? throw new ControlException("FoodRounds: reviewer is missing");
        _cooking = cooking ?? throw new ControlException("FoodRounds: no food Need is composed");
        if (!_cooking.Need.IsValid || _cooking.Need != PolicyFunctions.CookingNeed)
        {
            throw new ControlException("FoodRounds: the cooking planner is not owned by the food/cooking Need");
        }
    }

    /// <summary>The family's scheduler entry: it wakes on the colony facts and on its building outcomes.</summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<FoodFacts>(new FoodFamily(_cooking is not null), ReadAsync, new FamilySchedule { Kinds = [ActionKind.Building] }, WorkAsync);

    /// <summary>The building work of a ready Need: the cooking Need's campfire, paste dispenser, kitchen and stove.</summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, FoodFacts facts, CancellationToken call)
    {
        if (work.Need.Id != PolicyFunctions.CookingNeed || _cooking is null)
        {
            return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        }
        return (await _cooking.Copy().StepAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false)).Verdict;
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
        return Facts(reading.Projection);
    }

    /// <summary>The family's facts in <paramref name="projection"/>: its cooking fact and the campfire it owes down.</summary>
    internal static FoodFacts Facts(ColonyProjection projection) =>
        FoodFacts.From(projection.Facts) with { CampfireRetireOwed = CampfireRetireOwed(projection) };
}
