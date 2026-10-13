namespace RimGovernor.Brain.Policy;

// Go: colony_stage.go

/// <summary>
/// How far the colony has come by outcome: the one ordered fact that sets the goal budgets (the research
/// ladder's pace, the stall deadline) and which goals the review raises at all (StageGoalAllowed). It is
/// derived from colony facts and the goal progress records, never from research: TechTier is what the colony
/// can build, the stage is what it has achieved.
///
/// Each stage has explicit exit criteria, all read from the review's one live facts (StageColonyFacts):
///
///   Foothold     roofed sleeping for every colonist, a cooking bill, a food stockpile, food >=
///                FootholdFoodDays, two armed fighters
///   Reserves     food >= FoodTargetDays, the growing field sown, the wood floor met, a research bench built
///   Stable       power online, the season's climate answered (refrigeration in spring and summer, sleeping
///                warmth in fall and winter), production unblocked for StableTicks
///   Development  Stable held for DevelopmentTicks with the reserve doubled
/// </summary>
public enum ColonyStage
{
    Foothold,
    Reserves,
    Stable,
    Development,
}

public static class ColonyStageExtensions
{
    private static readonly string[] _names = ["Foothold", "Reserves", "Stable", "Development"];

    /// <summary>Go's <c>ColonyStage.String()</c>.</summary>
    public static string Name(this ColonyStage s) => s.Valid() ? _names[(int)s] : "Foothold";

    public static bool Valid(this ColonyStage s) => s >= ColonyStage.Foothold && s <= ColonyStage.Development;
}

/// <summary>The first exit criterion of the current stage the colony has not met.</summary>
public enum StageBlocker
{
    None,

    [WireName("shelter")]
    Shelter,

    [WireName("cooking")]
    Cooking,

    [WireName("food_storage")]
    FoodStorage,

    [WireName("starvation")]
    Starvation,

    [WireName("defense")]
    Defense,

    [WireName("food_reserve")]
    Food,

    [WireName("field")]
    Field,

    [WireName("wood")]
    Wood,

    [WireName("research_bench")]
    ResearchBench,

    [WireName("power")]
    Power,

    [WireName("climate")]
    Climate,

    [WireName("production_blocked")]
    Production,

    /// <summary>Every condition holds and the stage waits out its settling time (StableTicks, DevelopmentTicks).</summary>
    [WireName("settling")]
    Settling,

    [WireName("unknown")]
    Unknown,
}

/// <summary>
/// The stage as the last review left it, persisted with the review so the transitions carry their history
/// (hysteresis).
/// </summary>
public sealed record ColonyStageRecord
{
    public ColonyStage Stage { get; init; }

    /// <summary>The review tick the stage was entered.</summary>
    public Tick Since { get; init; }

    /// <summary>The first unmet exit criterion; none at Development.</summary>
    public StageBlocker Blocker { get; init; }

    /// <summary>The same in words, with the measured values.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Foothold's unmet shelter condition, used by NeedsShelter to promote shelter planning into the critical wave.</summary>
    public bool Held { get; init; }

    /// <summary>The production clock: whether a production goal's progress record was blocked at the last review.</summary>
    public bool ProductionBlocked { get; init; }

    /// <summary>The tick the production state was entered.</summary>
    public Tick ProductionSince { get; init; }

    /// <summary>Promotes shelter planning into the critical wave at Foothold.</summary>
    public bool NeedsShelter() => Stage == ColonyStage.Foothold && Held;
}

/// <summary>
/// What one review measured for the stage, all of it the review's gates, colony facts or goal progress.
/// Unknown facts never advance a stage.
/// </summary>
public sealed record ColonyStageFacts
{
    /// <summary>Foothold's exit: indoor and bed capacity for every colonist.</summary>
    public Fact<bool> Shelter { get; init; }

    /// <summary>An active meal bill on a usable bench.</summary>
    public Fact<bool> Cooking { get; init; }

    /// <summary>A food stockpile.</summary>
    public Fact<bool> FoodStorage { get; init; }

    /// <summary>Two armed fighters (every colonist when fewer).</summary>
    public Fact<bool> Armed { get; init; }

    /// <summary>The food runway the review measured.</summary>
    public Fact<double> FoodDays { get; init; }

    /// <summary>Reserves' exit: growing cells for every colonist, all of them sown.</summary>
    public Fact<bool> FieldSown { get; init; }

    /// <summary>A built research bench.</summary>
    public Fact<bool> ResearchBench { get; init; }

    /// <summary>The wood latch.</summary>
    public bool WoodShort { get; init; }

    /// <summary>Stable's exit: the power gate.</summary>
    public Fact<bool> Power { get; init; }

    /// <summary>The season's temperature answer.</summary>
    public Fact<bool> Climate { get; init; }

    /// <summary>The production goal whose record is blocked (ProductionBlockedGoal), empty when none.</summary>
    public ConcernId ProductionBlocked { get; init; }

    /// <summary>The production goal's blocked reason.</summary>
    public BlockedReason Blocked { get; init; }
}

/// <summary>
/// Holds the stage thresholds. Each transition has an enter threshold and a laxer exit threshold so a colony
/// oscillating around one does not flip stage each review. Zero fields take the defaults
/// (RoundsPolicy.Stages).
/// </summary>
public sealed record ColonyStagePolicy
{
    /// <summary>The food runway Foothold needs to exit.</summary>
    public double FootholdExitDays { get; init; }

    /// <summary>The runway Reserves needs to exit.</summary>
    public double ReserveEnterDays { get; init; }

    /// <summary>The runway under which Reserves (and every stage above) drops to Foothold.</summary>
    public double ReserveExitDays { get; init; }

    /// <summary>The food runway Development needs.</summary>
    public double DevelopmentEnterDays { get; init; }

    /// <summary>The runway under which Development drops to Stable.</summary>
    public double DevelopmentExitDays { get; init; }

    /// <summary>How long the production goals must stay unblocked to exit Stable.</summary>
    public Tick StableTicks { get; init; }

    /// <summary>How long one must stay blocked to drop Stable.</summary>
    public Tick StableExitTicks { get; init; }

    /// <summary>How long Stable must hold to enter Development.</summary>
    public Tick DevelopmentTicks { get; init; }

    /// <summary>
    /// The lowest stage whose goals and budgets StageRoundsPolicy applies, whatever the measured stage: a
    /// developed colony loaded mid-game, or a test of a later stage's planner, skips the ladder. Zero
    /// (Foothold) is no floor; the record keeps the measured stage.
    /// </summary>
    public ColonyStage Floor { get; init; }

    internal void Validate()
    {
        foreach (var n in new[] { FootholdExitDays, ReserveEnterDays, ReserveExitDays, DevelopmentEnterDays, DevelopmentExitDays })
        {
            DomainException.Require(!(double.IsNaN(n) || double.IsInfinity(n) || n < 0), "invalid colony stage threshold");
        }

        DomainException.Require(Floor.Valid(), "invalid colony stage floor");
        DomainException.Require(StableTicks.Value >= 0 && StableExitTicks.Value >= 0 && DevelopmentTicks.Value >= 0, "invalid colony stage settling time");
        DomainException.Require(
            !((FootholdExitDays > 0 && ReserveExitDays > 0 && ReserveExitDays > FootholdExitDays)
                || (ReserveEnterDays > 0 && ReserveExitDays > 0 && ReserveExitDays > ReserveEnterDays)
                || (DevelopmentEnterDays > 0 && DevelopmentExitDays > 0 && DevelopmentExitDays > DevelopmentEnterDays)),
            "unordered colony stage thresholds"
        );
    }
}

/// <summary>One exit criterion: unknown and unmet facts both hold the stage, each named.</summary>
internal readonly record struct StageCheck(Fact<bool> Fact, StageBlocker Blocker, string Name);

public static partial class PolicyFunctions
{
    /// <summary>
    /// The stage policy with the routine food thresholds as defaults: Foothold exits at FootholdFoodDays,
    /// Reserves at FoodTargetDays, and every stage above Foothold drops under two thirds of FootholdFoodDays;
    /// Development at twice the target, dropped under it; two days of unblocked production and three stable
    /// days for Development, one blocked day to drop Stable.
    /// </summary>
    public static ColonyStagePolicy Stages(this RoundsPolicy p)
    {
        var s = p.Stage;
        if (s.FootholdExitDays <= 0)
        {
            s = s with { FootholdExitDays = p.FootholdFoodDays };
        }

        if (s.ReserveEnterDays <= 0)
        {
            s = s with { ReserveEnterDays = p.FoodTargetDays };
        }

        if (s.ReserveExitDays <= 0)
        {
            s = s with { ReserveExitDays = s.FootholdExitDays * 2 / 3 };
        }

        if (s.DevelopmentEnterDays <= 0)
        {
            s = s with { DevelopmentEnterDays = 2 * p.FoodTargetDays };
        }

        if (s.DevelopmentExitDays <= 0)
        {
            s = s with { DevelopmentExitDays = p.FoodTargetDays };
        }

        if (s.StableTicks.Value <= 0)
        {
            s = s with { StableTicks = new Tick(2L * GameTime.TicksPerDay) };
        }

        if (s.StableExitTicks.Value <= 0)
        {
            s = s with { StableExitTicks = new Tick(GameTime.TicksPerDay) };
        }

        if (s.DevelopmentTicks.Value <= 0)
        {
            s = s with { DevelopmentTicks = new Tick(3L * GameTime.TicksPerDay) };
        }

        return s;
    }

    private static (StageBlocker Blocker, string Reason) FirstUnmet(params StageCheck[] checks)
    {
        foreach (var c in checks)
        {
            if (!c.Fact.TryGetValue(out var v))
            {
                return (StageBlocker.Unknown, c.Name + " unknown");
            }

            if (!v)
            {
                return (c.Blocker, c.Name + " unmet");
            }
        }

        return (StageBlocker.None, "");
    }

    private static string Days1(double days) => days.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The stage transition: a pure function of the last record, this review's facts, the thresholds and the
    /// review tick. Drops are checked first and cascade (a starving colony is Foothold whatever it was), then
    /// the stage climbs at most one step per review, once every exit criterion of the current stage holds.
    /// Unknown facts never advance a stage and never drop one.
    /// </summary>
    public static ColonyStageRecord ReviewColonyStage(ColonyStageRecord previous, ColonyStageFacts f, ColonyStagePolicy p, Tick now)
    {
        var r = previous;
        if (!r.Stage.Valid() || r.Since > now || r.ProductionSince > now)
        {
            r = new ColonyStageRecord { Since = now };
        }

        var shelterKnown = f.Shelter.TryGetValue(out var shelter);
        if (shelterKnown)
        {
            r = r with { Held = !shelter };
        }

        var blocked = f.ProductionBlocked.Value.Length != 0;
        if (blocked != r.ProductionBlocked || r.ProductionSince.Value == 0)
        {
            r = r with { ProductionBlocked = blocked, ProductionSince = now };
        }

        var foodKnown = f.FoodDays.TryGetValue(out var food);
        var keepReserves = !(shelterKnown && !shelter) && !(foodKnown && food < p.ReserveExitDays);
        var keepStable = keepReserves && !(r.ProductionBlocked && now.Value - r.ProductionSince.Value >= p.StableExitTicks.Value);
        var keepDevelopment = keepStable && !(foodKnown && food < p.DevelopmentExitDays);
        var stage = r.Stage;
        if (stage == ColonyStage.Development && !keepDevelopment)
        {
            stage = ColonyStage.Stable;
        }

        if (stage == ColonyStage.Stable && !keepStable)
        {
            stage = ColonyStage.Reserves;
        }

        if (stage == ColonyStage.Reserves && !keepReserves)
        {
            stage = ColonyStage.Foothold;
        }

        var since = r.Since;
        if (stage != r.Stage)
        {
            since = now;
        }

        var productionBlocked = r.ProductionBlocked;
        var productionSince = r.ProductionSince;

        StageCheck FoodAtLeast(double days, StageBlocker blocker)
        {
            var name = "food";
            if (foodKnown && food < days)
            {
                name = $"food {Days1(food)} of {Days1(days)} days";
            }

            return new StageCheck(Measured(f.FoodDays, v => v >= days), blocker, name);
        }

        string BlockedReasonText() => $"{f.ProductionBlocked.Value} blocked {f.Blocked.Value}";

        // Reports the first unmet exit criterion of the given stage, none when the stage above is entered now.
        (StageBlocker Blocker, string Reason) Exit(ColonyStage at, Tick enteredAt)
        {
            switch (at)
            {
                case ColonyStage.Foothold:
                    return FirstUnmet(
                        new StageCheck(f.Shelter, StageBlocker.Shelter, "shelter"),
                        FoodAtLeast(p.FootholdExitDays, StageBlocker.Starvation),
                        new StageCheck(f.Cooking, StageBlocker.Cooking, "cooking"),
                        new StageCheck(f.FoodStorage, StageBlocker.FoodStorage, "food storage"),
                        new StageCheck(f.Armed, StageBlocker.Defense, "basic defense")
                    );
                case ColonyStage.Reserves:
                    var wood = Known(!f.WoodShort);
                    var (b, reason) = FirstUnmet(
                        FoodAtLeast(p.ReserveEnterDays, StageBlocker.Food),
                        new StageCheck(f.FieldSown, StageBlocker.Field, "growing field"),
                        new StageCheck(wood, StageBlocker.Wood, "wood floor"),
                        new StageCheck(f.ResearchBench, StageBlocker.ResearchBench, "research bench")
                    );
                    if (b != StageBlocker.None)
                    {
                        return (b, reason);
                    }

                    return productionBlocked ? (StageBlocker.Production, BlockedReasonText()) : (StageBlocker.None, "");
                case ColonyStage.Stable:
                    var (b2, reason2) = FirstUnmet(
                        new StageCheck(f.Power, StageBlocker.Power, "power"),
                        new StageCheck(f.Climate, StageBlocker.Climate, "seasonal climate")
                    );
                    if (b2 != StageBlocker.None)
                    {
                        return (b2, reason2);
                    }

                    if (productionBlocked)
                    {
                        return (StageBlocker.Production, BlockedReasonText());
                    }

                    // A runway under the development exit names food before the settling time: it is what
                    // dropped (or would drop) Development.
                    if (foodKnown && food < p.DevelopmentExitDays)
                    {
                        return (StageBlocker.Food, $"food {Days1(food)} of {Days1(p.DevelopmentEnterDays)} days");
                    }

                    // The clear time counts from the stage's own entry: a colony settles into Stable before it
                    // is judged ready to develop.
                    var clear = now.Value - Math.Max(productionSince.Value, enteredAt.Value);
                    if (clear < p.StableTicks.Value)
                    {
                        return (StageBlocker.Settling, $"production clear {Days1(Days(clear))} of {Days1(Days(p.StableTicks.Value))} days");
                    }

                    var held = now.Value - enteredAt.Value;
                    if (held < p.DevelopmentTicks.Value)
                    {
                        return (StageBlocker.Settling, $"stable {Days1(Days(held))} of {Days1(Days(p.DevelopmentTicks.Value))} days");
                    }

                    return FirstUnmet(FoodAtLeast(p.DevelopmentEnterDays, StageBlocker.Food));
                default:
                    return (StageBlocker.None, "");
            }
        }

        // Climb one step only from a stage that was kept, never in the review that dropped into it.
        if (stage == r.Stage && stage < ColonyStage.Development && Exit(stage, since).Blocker == StageBlocker.None)
        {
            stage++;
            since = now;
        }

        var (blocker, text) = stage < ColonyStage.Development ? Exit(stage, since) : (StageBlocker.None, "");
        return r with
        {
            Blocker = blocker,
            Reason = text,
            Stage = stage,
            Since = since,
        };
    }

    private static double Days(long ticks) => (double)ticks / GameTime.TicksPerDay;

    /// <summary>Checks a persisted record against the review tick; throws when it is invalid.</summary>
    public static void ValidateColonyStage(ColonyStageRecord r, Tick tick) =>
        DomainException.Require(
            r.Stage.Valid() && r.Since.Value >= 0 && r.Since <= tick && r.ProductionSince.Value >= 0 && r.ProductionSince <= tick,
            "invalid colony stage"
        );

    /// <summary>
    /// The goals whose blocked progress record holds the colony out of Development: the food ladder, cooking,
    /// food storage and the resource floors (wood among them).
    /// </summary>
    private static readonly ConcernId[] _productionConcerns = [EnsureFoodSupply, EnsureCooking, MaintainFoodStorage, MaintainResource];

    /// <summary>
    /// The first production goal whose progress record is blocked on native evidence (no capable pawn, native
    /// refusal, every alternative cooled, a prerequisite goal): a goal merely between methods (no_method) or
    /// reconciling a write is not production stalled.
    /// </summary>
    public static (ConcernId Concern, BlockedReason Reason) ProductionBlockedConcern(IReadOnlyList<ConcernProgress> progress)
    {
        foreach (var goal in _productionConcerns)
        {
            foreach (var p in progress)
            {
                if (p.Concern != goal)
                {
                    continue;
                }

                if (
                    p.Blocked == BlockedReason.NoWorker
                    || p.Blocked == BlockedReason.NativeIneligible
                    || p.Blocked == BlockedReason.Cooldown
                    || p.Blocked.Prerequisite().Length != 0
                )
                {
                    return (goal, p.Blocked);
                }
            }
        }

        return (default, default);
    }

    /// <summary>
    /// Gathers the stage's facts from one review: the foothold facts read live, the food runway, the wood
    /// latch, the built research bench, the season, the doctors and the production goals' progress records.
    /// </summary>
    public static ColonyStageFacts StageColonyFacts(RoundsFindings needs, RoundsFacts f, RoundsPolicy p, IReadOnlyList<ConcernProgress> progress)
    {
        var facts = new ColonyStageFacts
        {
            Shelter = AllFacts(FootholdShelter(f), FootholdSleeping(f)),
            Cooking = f.Cooking,
            FoodStorage = FoodStorageStanding(f),
            Armed = FootholdArmed(f),
            FoodDays = f.FoodDays,
            FieldSown = AllFacts(FootholdProduction(f), Measured(f.FieldCoverage, v => v >= 1 - 1e-9)),
            WoodShort = needs.Latches.Wood && !Sourceless(progress, WoodLatchDefs),
            ResearchBench = ResearchBenchBuilt(f.CurrentConstruction),
            Power = FootholdPower(f),
            Climate = SeasonalClimate(f.Calendar, FootholdTemperature(f, p), needs.Latches.Refrigeration),
        };
        var (concern, reason) = ProductionBlockedConcern(progress);
        return facts with { ProductionBlocked = concern, Blocked = reason };
    }

    /// <summary>
    /// That the resource floor's planner holds every one of the resources as having no source
    /// (held:no_source): the floor can never be reached, so the stage works from stock instead of waiting on
    /// it.
    /// </summary>
    private static bool Sourceless(IReadOnlyList<ConcernProgress> progress, IEnumerable<string> resources) =>
        progress.Any(p => p.Concern == MaintainResource && resources.All(r => p.SourcelessResource(new Resource(r))));

    /// <summary>A built research bench of the colony's own.</summary>
    public static Fact<bool> ResearchBenchBuilt(Fact<CurrentConstruction> observed)
    {
        if (!observed.TryGetValue(out var census) || !census.Colony)
        {
            return Unknown<bool>();
        }

        return Known(census.Buildings.Items.Any(row => row.Building.Definition.Contains("ResearchBench", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The season's temperature answer: in spring and summer no perishable food is latched warm
    /// (refrigeration), in fall and winter the sleeping rooms hold their temperature (heat). An unknown
    /// season asks both.
    /// </summary>
    public static Fact<bool> SeasonalClimate(Fact<Calendar> calendar, Fact<bool> temperature, bool refrigerating)
    {
        var cooled = Known(!refrigerating);
        if (!calendar.TryGetValue(out var c))
        {
            return AllFacts(cooled, temperature);
        }

        if (c.Season.Contains("Spring", StringComparison.Ordinal) || c.Season.Contains("Summer", StringComparison.Ordinal))
        {
            return cooled;
        }

        return temperature;
    }

    /// <summary>
    /// How many rungs of the research ladder each stage walks: the masonry and power rungs at Foothold,
    /// through solar at Reserves, through the medieval crafts and the multi-analyzer at Stable, the whole
    /// ladder at Development.
    /// </summary>
    private static readonly int[] _stageLadderRungs = [2, 5, 10, int.MaxValue];

    /// <summary>
    /// The research ladder paced to the stage: its first rungs only, so a colony without reserves researches
    /// what its shell and power need and no further.
    /// </summary>
    public static IReadOnlyList<string> StageResearchLadder(ColonyStage stage, IReadOnlyList<string> ladder)
    {
        if (!stage.Valid())
        {
            stage = ColonyStage.Foothold;
        }

        var n = _stageLadderRungs[(int)stage];
        return ladder.Count > n ? [.. ladder.Take(n)] : ladder;
    }

    /// <summary>
    /// The factor ConcernStallTicks shrinks by at a stage: six in-game hours (1/4 of the configured deadline,
    /// which defaults to one day) at Foothold, so a stuck method rotates within the day while the colony has
    /// no shelter or starvation runway yet, without churning methods that are merely slow (one hour cooled
    /// them before a hauler arrived); unchanged from Reserves on.
    /// </summary>
    public static double StageConcernStallScale(ColonyStage stage) => stage == ColonyStage.Foothold ? 1.0 / 4 : 1;

    /// <summary>
    /// The policy with its budgets set by the stage: the research ladder (StageResearchLadder) and the
    /// goal-progress stall deadline (StageGoalStallScale over ConcernStallTicks). A stage that has not been
    /// reviewed yet (the zero record) is Foothold.
    /// </summary>
    public static RoundsPolicy StageRoundsPolicy(RoundsPolicy p, ColonyStage stage)
    {
        if (!stage.Valid())
        {
            stage = ColonyStage.Foothold;
        }

        if (p.Stage.Floor.Valid() && stage < p.Stage.Floor)
        {
            stage = p.Stage.Floor;
        }

        p = p with { ColonyStage = stage, ResearchLadder = EquatableArray.From(StageResearchLadder(stage, p.ResearchLadder.Items)) };
        var stallScale = StageConcernStallScale(stage);
        if (stallScale != 1 && p.ConcernStallTicks > 0)
        {
            p = p with { ConcernStallTicks = (long)Math.Ceiling(p.ConcernStallTicks * stallScale) };
        }

        return p;
    }
}
