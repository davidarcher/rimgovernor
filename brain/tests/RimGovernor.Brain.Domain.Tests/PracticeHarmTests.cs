using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3852: the tier-2 Melee and Shooting skill stocks are priced by the raid harm a level saves, the sparring ring's path (stored
/// level -> power curve -> defense capacity shift -> raid loss), and a practice session is credited the XP it applies at that price.
/// Anna (one raid a day, the only doctor) has Melee and Shooting at 3 of twenty-one 100 000 XP levels without decay; her power
/// curves add 4 DPS at level 4, 20 raid points of defense, which lifts 990 to 1010 against 1000 raid points: the ratio leaves the
/// 1-2 exposure bin (5 hits) for 0.5-1 (1 hit).
/// </summary>
public sealed class PracticeHarmTests
{
    private const double RequiredXp = 100_000;
    private static readonly string MeleeKey = ProjectGraph.SkillKey("Anna", "Melee"), ShootingKey = ProjectGraph.SkillKey("Anna", "Shooting");

    private static readonly SkillStatCurve TendCurve = new(ColonyValue.TendQualityStat, "Medicine",
        EquatableArray.From(Enumerable.Range(0, 21).Select(l => new SkillStatLevel(l, 0.2 + (0.1 * l)))), "test");

    private static ExposureTable Exposure() => new(RaidExposure.Schema,
    [
        new ExposureCell("1-2", ExposureRoles.Noncombatant, 100, 5, 3, 20, 0.5, 0.4, 0.6),
        new ExposureCell("0.5-1", ExposureRoles.Noncombatant, 100, 1, 1, 3, 0.1, 0.05, 0.15),
    ]);

    private static HarmInputs Raids(double raidsPerDay = 1) => ForecastValueTests.RaidDay(Exposure(), raidsPerDay) with { Raid = new HarmRaid.Assessed(new RaidDefense(1000, 990)) };

    private static PracticeTrainee Trainee(string pawn = "Anna") =>
        new(pawn, SkillStockTests.Mechanics(3, 0), 20, EquatableArray.From(Enumerable.Range(0, 21).Select(l => l >= 4 ? 6d : 2d)));

    private static ColonyModelInput Colony()
    {
        var anna = SkillStockTests.WithMedicine(ProjectGraphTests.Pawn("Anna", null, null), SkillStockTests.Mechanics(3, 0), "Melee", "Shooting");
        return ProjectGraphTests.Colony() with { Pawns = EquatableArray.From(new[] { anna with { SkillStatCurves = EquatableArray.From(new[] { TendCurve }) } }) };
    }

    private static ColonyValue Value(ColonyModelInput input, PracticeTrainees? sparring, PracticeTrainees? range, HarmInputs? harm = null)
    {
        var context = ForecastValueTests.Context(ForecastValueTests.HarmPawn() with { Pawn = "Anna" }, ForecastValueTests.HarmTables(),
            harm ?? Raids(), new Dictionary<string, EquatableArray<GearOption>> { ["Anna"] = ForecastValueTests.Cloth }) with { Sparring = sparring, Range = range };
        return new ColonyValue(context, [], CalendarRules.YearDays, input.Pawns, ItemFactsFixtureRules.CoreItemFacts());
    }

    private static PracticeTrainees Of(params PracticeTrainee[] trainees) => new(EquatableArray.From(trainees), 0);

    private static ColonyModelPolicy Policy() => new() { MedicineTendsPerDay = 0d };

    private static ProjectMarginals Marginals(ColonyValue value) => ProjectMarginalValue.Of(Colony(), Policy(), value);

    [Fact]
    public void AnXpOfAFightersMeleeOrShootingIsWorthTheRaidHarmItsNextLevelSaves()
    {
        var value = Value(Colony(), Of(Trainee()), Of(Trainee()));
        var marginals = Marginals(value);
        Assert.True(marginals.PerUnit[MeleeKey] > 0, "Melee 4 lifts the defense out of the 1-2 bin");
        Assert.True(marginals.PerUnit[ShootingKey] > 0, "Shooting 4 lifts the defense out of the 1-2 bin");

        // The marginal is the run's harm at Melee 4 all year less at Melee 3, over the level's XP: the defense shift prices it.
        var baseline = marginals.Trajectory;
        var raised = baseline with
        {
            Skills = EquatableArray.From(baseline.Skills.Select(t => t.Key == MeleeKey ? t with { Dates = [new SkillLevelDate(0, 4)] } : t)),
        };
        Assert.Equal((value.Price(raised).Value - value.Price(baseline).Value) / RequiredXp, marginals.PerUnit[MeleeKey], 12);
    }

    [Fact]
    public void NoRaidAPawnIncapableOfTheSkillOrUnreadWorkPawnsIsANamedState()
    {
        // No raid to expect: a fighter's level saves no harm.
        Assert.Equal(0, Marginals(Value(Colony(), Of(Trainee()), Of(Trainee()), Raids(0))).PerUnit[MeleeKey]);
        // A pawn no practice eligibility rule admits (Melee disabled, a pacifist) adds no defense by the skill.
        var none = Marginals(Value(Colony(), Of(), Of()));
        Assert.Equal((0d, 0d), (none.PerUnit[MeleeKey], none.PerUnit[ShootingKey]));
        // Unread work pawns are a required fact.
        Assert.Equal("work pawns", Assert.Throws<RequiredFactException>(() => Marginals(Value(Colony(), Of(), null))).Fact);
        var truncated = Trainee() with { Power = [2, 2, 2] };
        Assert.StartsWith("melee power curve", Assert.Throws<RequiredFactException>(() => Marginals(Value(Colony(), Of(truncated), Of()))).Fact, StringComparison.Ordinal);
    }

    private static PracticeWorker Worker(string pawn, string skill, SkillLearning learning) =>
        new(pawn, skill, "target/" + pawn, new(1, 1), "RimGovernor_Spar", "RimGovernor_Spar", 0d, 1000d, learning)
        { Equipment = [], Findings = [], PreparationTicks = 0, CallbackIntervalTicks = 100d, RawXpPerCallback = 10d, JoyGainPerTick = 0, Thoughts = [], Company = [] };

    [Fact]
    public void ASessionIsCreditedTheXpItAppliesAtTheStocksHarmPrice()
    {
        const double PerXp = 0.002;
        var key = ProjectGraph.SkillKey("a", "Melee");
        var task = new ForecastPracticeTask("practice/a", Worker("a", "Melee", SparringCreditTests.Melee()));
        ForecastValueResult Ledger(IReadOnlyDictionary<string, double> investment, ForecastPracticeTask session)
        {
            var context = new ForecastCoupledContext
            {
                Activities = new() { Deadline = 0, Activities = [new("practice/a", "ring", "ring", [])] }, Needs = NeedsFixtures.Empty, Resources = ForecastValueTests.Resources(),
                Investment = investment, PracticeTasks = [session],
            }.Priced();
            var plan = new ForecastActivityPlan([new("practice/a", "a", 0, 0, 0, 1000, 0, 0, 1000, 1000, 0)], 1000, 1000);
            return ForecastValue.Of(new(plan, true, new Dictionary<string, ForecastNeedsResult>(), []), context, GameTime.TicksPerDay,
                new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult>(), []));
        }

        // Nine callbacks of 10 raw XP inside the read's 1000-tick validity, unsaturated at a unit learn rate.
        var row = Assert.Single(Ledger(new Dictionary<string, double> { [key] = PerXp }, task).InvestmentContributions, c => c.Key == key);
        Assert.Equal(("practice/a", "a", 90d), (row.Activity, row.Pawn, row.Units));
        Assert.Equal(-90 * PerXp, row.Value, 12);
        // A stock tier 2 left unpriced adds no row; a pawn at the practice ceiling learns nothing.
        Assert.Empty(Ledger(new Dictionary<string, double>(), task).InvestmentContributions);
        Assert.Empty(Ledger(new Dictionary<string, double> { [key] = PerXp }, task with { Worker = task.Worker with { Learning = SparringCreditTests.Melee(2) } }).InvestmentContributions);
        // Unread learning is a required fact.
        var unread = task with { Worker = task.Worker with { Learning = SparringCreditTests.Melee() with { Xp = double.NaN } } };
        Assert.StartsWith("melee practice a", Assert.Throws<RequiredFactException>(() => Ledger(new Dictionary<string, double> { [key] = PerXp }, unread)).Fact, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheProductionComparisonSchedulesABoutWhoseXpTheHarmPricesAndAFreshColonyNone(bool priced)
    {
        // The production path (#3822): RoundsWork fills the work pawns' Skills and the practice read's workers;
        // GenerateCoupledActivities builds the bout's tasks and the context's practice sessions; the review's tier-2 marginals
        // (Rounder sets ForecastCoupledContext.Investment from them) price the XP. No Ideology role holds a requirement here.
        var facts = ForecastPortfolioChoiceTests.Observed([], [], helper: true);
        var learning = CapabilityMaintenanceTests.Learning() with { Xp = 0d };
        var roster = ((WorkPawnsRead)facts.ForecastObserved.WorkPawns).Pawns.Select(p => p with { Skills = [new() { Name = "Melee", Level = 1, Learning = learning }] }).ToArray();
        var workers = roster.Select((p, i) => new PracticeWorker(p.Id.Value, "Melee", "target/" + i, new(i + 1, 1), "RimGovernor_Spar", "RimGovernor_Spar",
            0d, 100d, learning) { Company = [], CallbackIntervalTicks = 10d, RawXpPerCallback = 10d, PreparationTicks = 0, JoyGainPerTick = 0, Thoughts = [], Equipment = [], Findings = [] }).ToArray();
        facts = facts with { Hostiles = Known(0L), CriticalPatients = 0L, ForecastObserved = facts.ForecastObserved with
        {
            WorkPawns = new WorkPawnsRead(EquatableArray.From(roster)),
            Needs = facts.ForecastObserved.Needs! with { Pawns = EquatableArray.From(facts.ForecastObserved.Needs!.Pawns.Select(p => p with
            { Needs = EquatableArray.From(p.Needs.Select(n => n with { Level = 1d, FallPerTick = 0d })) })) },
            Coupled = facts.ForecastObserved.Coupled! with { Practice = new() { Workers = EquatableArray.From(workers) } }
        } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300)!;
        Assert.NotEmpty(generated.Context.PracticeTasks);
        Assert.Empty(generated.Context.MaintenanceRequirements);
        // A fresh colony's tier 2 prices no fighter's stock (no raid yet): the bout earns nothing and is not scheduled.
        var investment = priced ? roster.ToDictionary(p => ProjectGraph.SkillKey(p.Id.Value, "Melee"), _ => 1d, StringComparer.Ordinal) : new Dictionary<string, double>();
        generated = generated with { Context = (generated.Context with { Investment = investment }).Priced() };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 300d / GameTime.TicksPerDay,
            facts with { CoupledAlternatives = generated }, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request with { Coupled = generated.Context }, generated);
        Assert.Equal(priced, selected.Selection.Selected.Length > 0);
    }
}
