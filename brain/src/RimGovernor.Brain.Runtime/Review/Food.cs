using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The runtime half of the food family (#3254): its read (the room census the kitchen, the butchery and a superseded
/// cooking campfire stand in, with the colony's construction claims, the food plan and the field budget) and the work
/// of its ready Needs, committed under the Need and admitted by claims. The supply Need's work is its butcher bench,
/// its forage and hunts, and its fishing zones, grower crops and food fields; the cooking Need's is its campfire,
/// paste dispenser, kitchen and stove. The baby feeding Need's baby food bill is the work ledger's
/// (<see cref="BabyFoodDeclarer"/>), and the blight Need's cut batches are the family's pure Methods.
/// </summary>
public sealed class FoodReview
{
    private readonly ColonyReview _reviewer;
    private readonly bool _supply;
    private readonly bool _babyFeeding;
    private readonly bool _blight;
    private readonly BuildingPlanner? _butcher;
    private readonly AcquisitionPlanner? _acquisition;
    private readonly FieldPlanner? _fields;
    private readonly BuildingPlanner? _cooking;

    /// <summary>
    /// With <paramref name="supply"/> the family derives food/supply and works it through whichever of
    /// <paramref name="butcher"/> (NewReviewButcherPlanner), <paramref name="acquisition"/> and
    /// <paramref name="fields"/> are composed; a <paramref name="cooking"/> planner (NewReviewCookingPlanner) derives
    /// food/cooking and does its building work. <paramref name="babyFeeding"/> and <paramref name="blight"/> derive
    /// food/baby-feeding and food/blight.
    /// </summary>
    public FoodReview(ColonyReview reviewer, bool supply, BuildingPlanner? butcher, AcquisitionPlanner? acquisition, FieldPlanner? fields, BuildingPlanner? cooking,
        bool babyFeeding = false, bool blight = false)
    {
        _reviewer = reviewer ?? throw new ControlException("FoodReview: reviewer is missing");
        if (!supply && cooking is null && !babyFeeding && !blight)
        {
            throw new ControlException("FoodReview: no food Need is composed");
        }
        _babyFeeding = babyFeeding;
        _blight = blight;
        if (cooking is not null && cooking.Need != FoodRules.CookingNeed)
        {
            throw new ControlException("FoodReview: the cooking planner is not owned by the food/cooking Need");
        }
        if (butcher is not null && (!supply || butcher.Need != FoodRules.FoodSupplyNeed))
        {
            throw new ControlException("FoodReview: the butcher planner is not owned by the food/supply Need");
        }
        if (!supply && (acquisition is not null || fields is not null))
        {
            throw new ControlException("FoodReview: supply work is composed without the food/supply Need");
        }
        _supply = supply;
        _butcher = butcher;
        _acquisition = acquisition;
        _fields = fields;
        _cooking = cooking;
    }

    /// <summary>The family's scheduler entry: it wakes on the colony facts and on its building, zone, crop, acquisition, cut and bill outcomes.</summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<FoodFacts>(new FoodFamily(_supply, _cooking is not null, _babyFeeding, _blight), ReadAsync, Schedule, WorkAsync);

    /// <summary>The outcomes the family's work commits: buildings, zones and their cells, grower crops, acquisitions and plant cuts.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Kinds = [ActionKind.Building, ActionKind.ZoneCreate, ActionKind.ZoneCellEdit, ActionKind.GrowerCrop, ActionKind.Acquisition, ActionKind.AcquisitionWithdraw, ActionKind.CutPlant],
        // The field coverage and the blight read the growing zones.
        Sections = [FactSection.Zones],
    };

    /// <summary>
    /// The baby feeding Need's work: none of the family's own. Its one remedy is the baby food bill the work ledger
    /// places (<see cref="BabyFoodDeclarer"/>).
    /// </summary>
    internal static readonly Verdict BabyFoodBillVerdict = Verdict.WaitFor(Cause.Facility, "baby_food_bill");

    /// <summary>The work of a ready Need: each composed supply step in turn, the cooking Need's buildings, or the ledger's baby food bill.</summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, FoodFacts facts, CancellationToken call)
    {
        if (work.Need.Id == FoodRules.CookingNeed && _cooking is not null)
        {
            return (await _cooking.Copy().StepAsync(work.Arbiter!, call, work.Epoch).ConfigureAwait(false)).Verdict;
        }
        if (work.Need.Id == FoodRules.BabyFeedingNeed)
        {
            return BabyFoodBillVerdict;
        }
        if (work.Need.Id == FoodRules.BlightNeed)
        {
            // The cut batches are the family's pure Methods; the runner filed their verdict.
            return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        }
        if (work.Need.Id != FoodRules.FoodSupplyNeed || !_supply)
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
            verdicts.Add((await _fields.StepAsync(work.Arbiter!, work.Owner, call, work.Epoch).ConfigureAwait(false)).Verdict);
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
        var review = await _reviewer.Player.Journal.LoadReviewAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, ct).ConfigureAwait(false);
        if (!BuildingPlanner.BuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("food read: the scope is another world or older than the review");
        }
        var journal = _reviewer.Player.Journal;
        var claims = journal.ConstructionClaims(scope.World, expected.Tick, ct);
        var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, claims, ct).ConfigureAwait(false);
        var refused = _blight ? BlightRefused(journal, scope.World, ct) : [];
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("food read: the read took too long");
        }
        return Facts(reading.Reading, _reviewer.Policy) with { BlightRefused = refused };
    }

    /// <summary>The plants a cut of the blight Need's unretired Methods could not designate, so the next batch leaves them out.</summary>
    internal static EquatableArray<string> BlightRefused(JournalStore journal, GenerationSnapshot world, CancellationToken ct)
    {
        var refused = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var method in journal.LoadNeed(FoodRules.BlightNeed, world, ct).Methods)
        {
            foreach (var progress in journal.LoadPlan(method.Plan, ct).Progress)
            {
                if (progress.Action.CutPlant is { } cut && progress.View.Stage == Stage.Unsuccessful)
                {
                    refused.Add(cut.Plant);
                }
            }
        }
        return EquatableArray.From(refused);
    }

    /// <summary>
    /// The family's facts in <paramref name="projection"/>: the field coverage against the season's reserve, the
    /// supply checks and the cooking fact, and the campfire it owes down.
    /// </summary>
    internal static FoodFacts Facts(ColonyReading projection, PlannerSettings policy)
    {
        var budgeted = ColonyPlanningRules.ApplyFieldBudget(projection.Facts, policy.Seasonal(projection.Facts.Calendar, projection.Facts.DisasterConditions).FoodTargetDays);
        return FoodFacts.From(budgeted, policy) with { CampfireRetireOwed = FacilityPlanningRules.CampfireRetireOwed(projection.Facts, nameof(RimWorld.ThingDefOf.Campfire)) };
    }
}

/// <summary>
/// Declares the food/baby-feeding Need's baby food bill to the work ledger, filed under the Need: while the Need is
/// unmet, the standing target-count bill for a baby-edible recipe; nothing while it is met.
/// </summary>
public sealed class BabyFoodDeclarer : IOrderDeclarer
{
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        Task.FromResult(Declare(projection, benches).For(FoodRules.BabyFeedingNeed.FilingKey.Value));

    internal static Declared Declare(ColonyReading projection, IReadOnlyList<GearBench> benches)
    {
        var babies = projection.Facts.BabyFeeding;
        if (!babies.IsShort)
        {
            return new Declared();
        }
        var (selected, ok) = ProductionRules.SelectProductionBill(BabyFeedingRules.BabyFoodBill, projection.Facts.EssentialInputs.Benches, projection.Facts.Census.ColonistCount, 0, 1, new ProductionBillContext { BabyFeeding = babies });
        return CareOrdersRules.DeclareSelection(selected, ok, benches);
    }
}
