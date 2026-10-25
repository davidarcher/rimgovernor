using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain.Tests.Game;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3824: the illness forecast. A known incident rate and course give the patient-days, tends and death chance worked out by hand
// from the course's own numbers; the schedule's rate, shares and victim chance follow the storyteller comp and the incident worker.
public sealed class IllnessForecastTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 30 * Day;

    private static NeedsForecastPawn Pawn(double gain = 1, params DiseaseContract[] contracts) => NeedsFixtures.Pawn() with
    {
        Pawn = "pawn",
        Consciousness = 1d, Moving = 1d, Manipulation = 1d, Sight = 1d, Talking = 1d,
        Disease = NeedsFixtures.Disease with { ImmunityGainSpeed = gain, Contracts = EquatableArray.From(contracts) },
    };

    private static readonly CapacityPenaltyTables Tables = new() { MovingMinForCapable = 0.15 };

    private static ForecastSupportConsequences Care(double quality = 0.5) => new()
    {
        CareServices = "demand/tend", FirstCareTicks = 0, TendQuality = quality, CareRoom = new CareRoomRead.Assessed(0.8),
    };

    private static HediffPenaltyTable Table(string hediff, double initial, double? lethal = null, ImmunizableProps? immunizable = null,
        (double, double)? gone = null) => new()
    {
        Known = true, Hediff = hediff, InitialSeverity = initial, Lethality = lethal is { } top ? new Lethality.Lethal(top) : new Lethality.Harmless(), Immunizable = immunizable, Lifespan = gone is { } span ? new Lifespan.EndsAfter(span.Item1, span.Item2) : new Lifespan.Lasting(),
        Stages = [new PenaltyStage(0, 0, [])],
    };

    // Severity 0.2 rising 0.2 a day kills at day 4; immunity at 0.25 x u a day comes on day 4 / u, u uniform on [0.8, 1.2], so the
    // pawn dies for u <= 1: a death chance of one half. A survivor's severity 0.2 + 0.8 / u falls at 0.5 a day after immunity.
    private static DiseaseCourse Flu() => new()
    {
        Table = Table("Flu", 0.2, 1, new ImmunizableProps(0.25, 0.2, -0.5)), Tendable = true, Tend = new TendCourse(0, 0.5, 0.75, -1),
    };

    private static IllnessSchedule Schedule(DiseaseCourse course, string incident = "Disease_Flu", double perDay = 1 / 30.0) => new()
    {
        Assessed = true, Incidents = [new IllnessIncident(incident, [(0, perDay)], 1, course)],
    };

    private static HarmDistribution Of(DiseaseCourse course, string incident, ForecastSupportConsequences? support, double gain = 1) =>
        IllnessForecast.Of(Schedule(course, incident), Pawn(gain, new DiseaseContract(incident, 1)), Tables, HarmConsequences.CareOf(support), 1, new ForecastValuePolicy(), Horizon, new StockArrival());

    // The tend times: first care, then the first retend, then every retend.
    private static int TendsBefore(double end, double first, double every)
    {
        var count = 0;
        for (var t = 0.0; t < end; t = count == 1 ? first : t + every)
        {
            count++;
        }

        return count;
    }

    internal static IllnessSchedule Schedule(DiseaseTable table, DiseaseFacts facts) =>
        IllnessSchedule.Of(table, facts is null ? null! : ColonyFactsCoreTests.QuietThreat with { Disease = facts });

    [Fact]
    public void AKnownCourseGivesTheExpectedDeathChancePatientDaysAndTends()
    {
        var d = Of(Flu(), "Disease_Flu", Care());

        Assert.True(d.Known);
        Assert.Equal(0.5, d.DeathProbability, 9);

        // The survivors' immunity factor on the midpoint grid of (1, 1.2]; each ends 0.4 + 5.6 / u days after first care.
        double[] u = [1.025, 1.075, 1.125, 1.175];
        var survivorDays = u.Average(x => 0.4 + 5.6 / x);
        Assert.Equal(0.5 * 4 + 0.5 * survivorDays, d.PatientDays, 6);
        Assert.Equal(0.5 * TendsBefore(4, 0.5, 0.75) + 0.5 * u.Average(x => TendsBefore(0.4 + 5.6 / x, 0.5, 0.75)), d.Treatments, 9);
        Assert.Equal(7.125, d.Treatments, 9);

        // The dead lose the horizon's rest and the terminal value; the survivors' stage costs nothing.
        var policy = new ForecastValuePolicy();
        Assert.Equal(0.5 * (30 - 4 + policy.DeathTerminalValue), d.Points.Sum(p => p.Loss * p.Probability), 6);
        Assert.Equal(1, d.Points.Sum(p => p.Probability), 9);
    }

    [Fact]
    public void AFasterImmunityLowersTheDeathChanceAndAnUnreachableOneKillsEveryCase()
    {
        // At 1.25x the threshold is u <= 0.8: no death. At 0.5x immunity comes by day 10 at the earliest, after the death on day 4.
        Assert.Equal(0, Of(Flu(), "Disease_Flu", Care(), gain: 1.25).DeathProbability, 9);
        Assert.Equal(1, Of(Flu(), "Disease_Flu", Care(), gain: 0.5).DeathProbability, 9);
    }

    [Fact]
    public void AnIllnessTheTendQualityCuresEndsAtTheTendThatReachesIt()
    {
        // Tends at 0, 2, 4.5, 7, 9.5 and 12 at quality 0.5: the sixth reaches a total of 3.
        var worms = new DiseaseCourse { Table = Table("GutWorms", 0.5), Tendable = true, Tend = new TendCourse(0, 2, 2.5, 3) };

        var tended = Of(worms, "Disease_GutWorms", Care());
        var untended = Of(worms, "Disease_GutWorms", null);

        Assert.Equal(12, tended.PatientDays, 9);
        Assert.Equal(6, tended.Treatments, 9);
        Assert.Equal(0, tended.DeathProbability);
        Assert.Equal(0, untended.PatientDays);
        Assert.Equal(0, untended.Treatments);
        Assert.Contains("illness_untended", untended.Findings);
    }

    [Fact]
    public void MechanitesHeldAtTheirMinimumLastUntilTheyDisappear()
    {
        // Tending at -1 x 0.5 outruns the 0.25 a day rise, so the severity holds at the 0.001 minimum until the Disappears comp's
        // time, uniform on 15 to 30 days: 22.5 days on average.
        var mechanites = new DiseaseCourse
        {
            Table = Table("Mechanites", 0.5, immunizable: new ImmunizableProps(0, 0.25, 0), gone: (900000, 1800000)),
            MinSeverity = 0.001, Tendable = true, Tend = new TendCourse(-1, 2, 2.5, -1),
        };

        var d = Of(mechanites, "Disease_Mechanites", Care());

        double[] gone = [16.875, 20.625, 24.375, 28.125];
        Assert.Equal(22.5, d.PatientDays, 9);
        Assert.Equal(gone.Average(g => TendsBefore(g, 2, 2.5)), d.Treatments, 9);
        Assert.Equal(0, d.DeathProbability);
    }

    // ----- organ loss (#3830)

    private const string Decay = "Disease_OrganDecay";

    private static DiseasePart Organ(string part, string capacity, bool threatened = false, params string[] recipes) => new()
    {
        Part = part, Threatened = threatened, Tag = part + "Tag", Capacity = capacity, Lethal = true,
        Recipes = recipes.ToDictionary(r => r, _ => 1.0),
    };

    // Severity 0.001 rising 0.05 a day reaches the max severity 1 on day 19.98: the heart's death, a lung's or a kidney's loss.
    private static DiseaseCourse DecayCourse(double offset = 0, params DiseasePart[] parts) => new()
    {
        Table = Table("OrganDecay", 0.001, 1) with { Stages = [new PenaltyStage(0, 0, []) { PartEfficiencyOffset = offset }] },
        PerDayMin = 0.05, PerDayMax = 0.05, MaxSeverity = 1, DestroysOrgan = true,
        Parts = parts.Length > 0 ? parts : [Organ("Heart", "BloodPumping", true), Organ("Lung", "Breathing", recipes: "InstallNaturalLung"), Organ("Kidney", "BloodFiltration")],
    };

    private const double LostOn = 0.999 / 0.05;

    // The Human torso's heart 0.02, lungs 0.025 and kidneys 0.017, as the native read lists them.
    private static DiseasePartRead[] Body(params DiseasePartRead[] rows) => rows.Length > 0 ? rows :
    [
        Part("Heart", 0.02), Part("Lung", 0.025), Part("Lung", 0.025), Part("Kidney", 0.017), Part("Kidney", 0.017),
    ];

    private static DiseasePartRead Part(string part, double coverage, double efficiency = 1, bool open = true) =>
        new(part, coverage, efficiency) { Incidents = open ? [Decay] : [], Hediffs = [] };

    private static OrganReplacementRead Replacement(string part, string recipe, bool stocked = true, int doctors = 1, double chance = 0.9) =>
        new(part, recipe)
        {
            PartThing = part, PartOnMap = stocked, MedicineOnMap = stocked, EligibleDoctors = doctors, SuccessChance = chance, DoctorSuccessChance = chance,
            Outcomes = [new("success", 0, 0, 0), new("death", 0.5, 0, 0)], Environment = true, QualityMax = 0.98, Quality = 0.9,
        };

    private static NeedsForecastPawn Decaying(DiseasePartRead[] rows, params OrganReplacementRead[] replacements) => Pawn(1, new DiseaseContract(Decay, 1)) with
    {
        Disease = new PawnDiseaseFacts
        {
            ImmunityGainSpeed = 1, Contracts = [new DiseaseContract(Decay, 1)], BloodPumping = 1, Breathing = 1, BloodFiltration = 1,
            Parts = EquatableArray.From(rows), Replacements = EquatableArray.From(replacements),
        },
    };

    private static IllnessPawn Organs(DiseaseCourse course, NeedsForecastPawn pawn, double horizon = Horizon)
    {
        var schedule = Schedule(course, Decay);
        return new(pawn.Pawn, IllnessForecast.Of(schedule, pawn, Tables, HarmConsequences.CareOf(null), 1, new ForecastValuePolicy(), horizon, new StockArrival()), schedule.IncidentsPerDay(horizon / Day));
    }

    // The replacement surgeries a day the hospital credit is fed (ForecastHospital.Surgeries).
    private static double SurgeriesPerDay(IllnessPawn p) => ForecastHospital.Surgeries(p.Pawn, p.Distribution.Replacements, p.IncidentsPerDay).Sum(r => r.PerDay);

    private static double Mean(HarmDistribution d) => d.Points.Sum(p => p.Loss * p.Probability);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedRequestsHaveTheirOwnStockAndLaterInvestmentWithNoHarmCacheLeak(bool selectedFirst)
    {
        var lung = new Resource("Lung");
        var option = Assert.Single(ForecastTradeRequests.Options(ForecastTradeRequestTests.Catalog(lung),
            new TradeVisitFacts { Requests = [ForecastTradeRequestTests.Request()] }, 100));
        var pawn = Decaying(Body(Part("Lung", .1), Part("Lung", .1)),
            Replacement("Lung", "InstallNaturalLung", stocked: false, chance: 1) with { MedicineOnMap = true }) with
        {
            Needs = ForecastValueTests.Pawn().Needs, Mood = ForecastValueTests.Pawn().Mood,
        };
        // An already advanced disease loses the lung within the horizon; its replacement waits for the selected visit.
        var course = DecayCourse(0, Organ("Lung", "Breathing", recipes: "InstallNaturalLung")) with { PerDayMin = 1, PerDayMax = 1 };
        var context = ForecastValueTests.Context(pawn, harm: new HarmInputs
        {
            Schedule = new() { Assessed = true, MajorThreatsPerDay = 0, RaidFraction = 1 },
            Illness = Schedule(course, Decay, perDay: .1),
        }) with { Stock = new StockArrival { Requests = [option] }, InvestmentHorizonDays = 10 };
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), true,
            new Dictionary<string, ForecastNeedsResult> { [pawn.Pawn] = ForecastValueTests.Timeline(pawn) }, []);
        ForecastValueResult Value(bool selected) => ForecastValue.Of(outcome, context, Day, outcome, selected ? [option.Commitment()] : []);
        var first = Value(selectedFirst);
        var second = Value(!selectedFirst);
        var selected = selectedFirst ? first : second;
        var unselected = selectedFirst ? second : first;
        Assert.Equal(unselected.Harm, selected.Harm); // day two cannot change the one-day immediate term
        Assert.Empty(unselected.InvestmentContributions);
        Assert.True(selected.Investment < 0);
        Assert.Equal(option.Id, Assert.Single(selected.InvestmentContributions).Key);
        Assert.Equal(selected.Investment, Value(true).Investment);
        Assert.Equal(unselected.Harm, Value(false).Harm);
        var longContext = context with { InvestmentHorizonDays = 0 };
        ForecastValueResult Long(bool take) => ForecastValue.Of(outcome, longContext, 10 * Day, outcome, take ? [option.Commitment()] : []);
        var longFirst = Long(selectedFirst);
        var longSecond = Long(!selectedFirst);
        var longSelected = selectedFirst ? longFirst : longSecond;
        var longBaseline = selectedFirst ? longSecond : longFirst;
        Assert.True(Assert.IsType<LedgerTerm.Priced>(longSelected.Harm).Loss < Assert.IsType<LedgerTerm.Priced>(longBaseline.Harm).Loss);
        Assert.Equal(longSelected.Harm, Long(true).Harm);
        Assert.Equal(longBaseline.Harm, Long(false).Harm);
        var retained = Assert.IsType<HarmDetail>(ForecastValue.ReferenceHarm(outcome, longContext, 10 * Day, [option.Commitment()]));
        Assert.Equal(Assert.IsType<LedgerTerm.Priced>(longSelected.Harm).Loss, retained.Illness!.Loss);
        Assert.Empty(context.Stock.Trade);
        var derived = ForecastTradeRequests.Context(outcome, [option.Commitment()], context, Day);
        Assert.Same(context.Needs, derived.Needs);
        Assert.Same(context.Stock, derived.StockBeforeRequests);
        Assert.Single(derived.Stock.Trade);
        Assert.Equal(0, ForecastValue.Of(outcome, context with { InvestmentHorizonDays = 2 }, Day, outcome, [option.Commitment()]).Investment);

        // Through the real portfolio search: two roots schedule the same empty work, but only a selected request has stock.
        var empty = option with { Native = option.Native with { TraderKind = "empty" }, Sources = [] };
        var observed = ForecastCoupledSelectionTests.Review(level: 1, fall: 0, sourceCount: 0, horizon: Day)
            with { StockArrival = context.Stock with { Requests = [empty, option] } };
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(observed));
        generated = generated with { Context = generated.Context with
        {
            Needs = generated.Context.Needs with { Pawns = [pawn] }, Harm = context.Harm,
            CapacityPenalties = context.CapacityPenalties, InvestmentHorizonDays = 10,
        } };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 1, observed with { CoupledAlternatives = generated },
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(observed) }, []);
        var selection = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        Assert.Contains(option.Id, selection.Selection.Selected);
        Assert.DoesNotContain(empty.Id, selection.Selection.Selected);
        Assert.True(selection.SelectedVector.Value.Ledger.Investment < 0);
        Assert.Equal(selection.BaselineVector.Value.Ledger.Harm, selection.SelectedVector.Value.Ledger.Harm);
        Assert.Equal(1, selection.SearchPhases.Sum(p => p.ScheduleSearches));
    }

    [Fact]
    public void ADestroyedOrganTheAutopilotCannotReplaceCostsItsCapacityToTheHorizonAndBooksNoSurgery()
    {
        var unstocked = Organs(DecayCourse(), Decaying(Body(), Replacement("Lung", "InstallNaturalLung", stocked: false)));
        var d = unstocked.Distribution;

        // The heart kills on its coverage share; a lung or a kidney is lost on day 19.98, by coverage.
        double heart = 0.02 / 0.104, lung = 0.05 / 0.104, kidney = 0.034 / 0.104;
        Assert.True(d.Known);
        Assert.Equal(heart, d.DeathProbability, 9);
        // One lung of two halves Breathing: consciousness x (1 - 0.2 x 0.5), moving x (1 + 0.2 x (0.5 - 1)) x that, manipulation and
        // talking x that. One kidney halves BloodFiltration: consciousness x (1 - 0.1 x 0.5), moving x that.
        var lungFactor = 0.9 * (0.9 * 0.9) * 0.9 * 0.9;
        var kidneyFactor = 0.95 * 0.95 * 0.95 * 0.95;
        var rest = 30 - LostOn;
        var policy = new ForecastValuePolicy();
        Assert.Equal((heart * (rest + policy.DeathTerminalValue)) + (lung * rest * (1 - lungFactor)) + (kidney * rest * (1 - kidneyFactor)), Mean(d), 6);
        Assert.Equal(0, SurgeriesPerDay(unstocked));
        Assert.Equal([("Kidney", SurgeryWantReason.PartShort, kidney), ("Lung", SurgeryWantReason.PartShort, lung)],
            d.Replacements.Select(r => (r.Part, r.Reason, Math.Round(r.Probability, 9))).Select(r => (r.Part, r.Reason, r.Item3)).ToArray(),
            new ReplacementComparer());
        Assert.All(d.Replacements, r => Assert.Null(r.Recipe));
        Assert.DoesNotContain(d.Findings, f => f.StartsWith("disease_organ_loss_unpriced", StringComparison.Ordinal));
    }

    private sealed class ReplacementComparer : IEqualityComparer<(string, SurgeryWantReason, double)>
    {
        public bool Equals((string, SurgeryWantReason, double) a, (string, SurgeryWantReason, double) b) =>
            a.Item1 == b.Item1 && a.Item2 == b.Item2 && Math.Abs(a.Item3 - b.Item3) < 1e-6;

        public int GetHashCode((string, SurgeryWantReason, double) value) => HashCode.Combine(value.Item1, value.Item2);
    }

    [Fact]
    public void AStockedReplacementBooksItsSurgeriesADayAndEndsTheLoss()
    {
        var stocked = Organs(DecayCourse(), Decaying(Body(), Replacement("Lung", "InstallNaturalLung")));
        var d = stocked.Distribution;

        // The lung's share books the stocked natural lung; the kidney has no recipe and stays lost.
        double heart = 0.02 / 0.104, lung = 0.05 / 0.104, kidney = 0.034 / 0.104;
        var lost = d.Replacements.Single(r => r.Recipe is not null);
        Assert.Equal(("Lung", "InstallNaturalLung"), (lost.Part, lost.Recipe!.Recipe));
        Assert.Equal(lung, lost.Probability, 9);
        Assert.Equal(1 / 30.0 * lung, SurgeriesPerDay(stocked), 12);
        var rest = 30 - LostOn;
        var policy = new ForecastValuePolicy();
        Assert.Equal((heart * (rest + policy.DeathTerminalValue)) + (kidney * rest * (1 - (0.95 * 0.95 * 0.95 * 0.95))), Mean(d), 6);
        Assert.DoesNotContain(TradeArrivalRules.SilverIncomeUnmodelled, d.Findings);

        // A doctor beyond the 0.20 failure cap queues nothing: the loss stays, named NoDoctor.
        var refused = Organs(DecayCourse(), Decaying(Body(), Replacement("Lung", "InstallNaturalLung", chance: 0.7))).Distribution;
        Assert.Equal(SurgeryWantReason.NoDoctor, refused.Replacements.Single(r => r.Part == "Lung").Reason);
        Assert.True(Mean(refused) > Mean(d));
    }

    [Fact]
    public void ACourseWithNoPartLossBooksNoSurgery()
    {
        // The flu destroys nothing; organ decay over a 10-day horizon has not reached its max severity.
        var flu = new IllnessPawn("pawn", Of(Flu(), "Disease_Flu", Care()), 1 / 30.0);
        var early = Organs(DecayCourse(), Decaying(Body(), Replacement("Lung", "InstallNaturalLung")), horizon: 10 * Day);

        Assert.Empty(flu.Distribution.Replacements);
        Assert.Equal(0, SurgeriesPerDay(flu));
        Assert.Empty(early.Distribution.Replacements);
        Assert.Equal(0, SurgeriesPerDay(early));
        Assert.Equal(0, early.Distribution.DeathProbability);
    }

    [Fact]
    public void AStagesPartEfficiencyOffsetCostsTheOrgansCapacityWhileItLasts()
    {
        // A lung at -0.5 leaves Breathing (1 + 0.5) / 2 = 0.75: consciousness x 0.95, moving x 0.95 x 0.95, manipulation and talking x 0.95.
        var course = DecayCourse(-0.5, Organ("Lung", "Breathing"));
        var d = Organs(course, Decaying([Part("Lung", 0.025), Part("Lung", 0.025)]), horizon: 10 * Day).Distribution;

        Assert.Equal(10 * (1 - (0.95 * (0.95 * 0.95) * 0.95 * 0.95)), Mean(d), 6);
    }

    [Fact]
    public void LosingTheLastKidneyKillsAndAPawnWithNoOpenPartIsSpared()
    {
        var course = DecayCourse(0, Organ("Kidney", "BloodFiltration"));
        var last = Organs(course, Decaying([Part("Kidney", 0.017, efficiency: 0, open: false), Part("Kidney", 0.017)])).Distribution;
        var none = Organs(course, Decaying([Part("Kidney", 0.017, efficiency: 0, open: false), Part("Kidney", 0.017, open: false)])).Distribution;

        Assert.Equal(1, last.DeathProbability, 9);
        Assert.Empty(last.Replacements);
        Assert.True(none.Known);
        Assert.Equal(0, none.DeathProbability);
        Assert.Equal(0, Mean(none));
        Assert.Throws<RequiredFactException>(() => Organs(course, Decaying([])));
    }

    [Fact]
    public void TheContractChanceAndTheVictimChanceScaleTheSickShare()
    {
        var schedule = Schedule(Flu()) with { Incidents = [new IllnessIncident("Disease_Flu", [(0, 1 / 30.0)], 0.5, Flu())] };

        var d = IllnessForecast.Of(schedule, Pawn(1, new DiseaseContract("Disease_Flu", 0.5)), Tables, HarmConsequences.CareOf(Care()), 1, new ForecastValuePolicy(), Horizon, new StockArrival());
        var missing = IllnessForecast.Of(schedule, Pawn(1), Tables, HarmConsequences.CareOf(Care()), 1, new ForecastValuePolicy(), Horizon, new StockArrival());

        Assert.Equal(0.25 * 0.5, d.DeathProbability, 9);
        Assert.False(missing.Known);
        Assert.Contains("disease_contract_unobserved:pawn:Disease_Flu", missing.Findings);
    }

    // ----- the schedule

    internal static DiseaseFacts Facts(double settled = 5, params DiseaseIncidentRead[] incidents) => new()
    {
        Storyteller = "Cassandra", Biome = "TemperateForest", RandomDiseases = true, IntervalFactor = 1, DaysSinceSettle = settled,
        Candidates = 3, FreeColonists = 3,
        Incidents = EquatableArray.From(incidents.Length > 0 ? incidents
            : [new DiseaseIncidentRead("Disease_Flu", 3), new DiseaseIncidentRead("Disease_GutWorms", 3), new DiseaseIncidentRead("Disease_OrganDecay", 3)]),
    };

    [Fact]
    public void TheRateIsOneOverTheBiomeMtbTimesTheIntervalFactorFromTheCompsMinDaysPassed()
    {
        var table = DiseaseTableTests.Table();

        var schedule = Schedule(table, Facts());

        Assert.True(schedule.Assessed);
        Assert.Empty(schedule.Findings);
        // Cassandra's comp starts on day 9, four days from now: over 30 days it runs 26.
        Assert.Equal(1 / 60.0 * 26 / 30, schedule.IncidentsPerDay(30), 12);
        Assert.Equal(1 / 120.0, Schedule(table, Facts(20) with { IntervalFactor = 2 }).IncidentsPerDay(30), 12);
        Assert.Equal(0, Schedule(table, Facts() with { IntervalFactor = 100 }).IncidentsPerDay(30));
        Assert.Equal(0, Schedule(table, Facts() with { RandomDiseases = false }).IncidentsPerDay(30));
    }

    [Fact]
    public void TheSharesAreTheCommonalitiesOfTheUsableIncidents()
    {
        var table = DiseaseTableTests.Table();

        var all = Schedule(table, Facts());
        var blocked = Schedule(table, Facts(5, new DiseaseIncidentRead("Disease_Flu", 3) { Blocked = ["scenario"] },
            new DiseaseIncidentRead("Disease_GutWorms", 3), new DiseaseIncidentRead("Disease_OrganDecay", 3)));

        // Organ decay has no commonality in the biome; flu 100, gut worms 30.
        Assert.Equal(["Disease_Flu", "Disease_GutWorms"], all.Incidents.Select(i => i.Incident));
        Assert.Equal(100 / 130.0, all.Share(all.Incidents[0], 30), 12);
        Assert.Equal(30 / 130.0, all.Share(all.Incidents[1], 30), 12);
        Assert.Equal(["Disease_GutWorms"], blocked.Incidents.Select(i => i.Incident));
        Assert.Equal(1, blocked.Share(blocked.Incidents[0], 30));
    }

    [Fact]
    public void TheVictimChanceIsTheMeanDrawnCountOverThePotentialVictims()
    {
        // 3 candidates at 0.2 to 0.5: RoundToInt gives 1 to 2 (1.5 rounds to even), one and a half of three.
        Assert.Equal(0.5, IllnessSchedule.VictimChance(3, 3, 0.2, 0.5, 99999), 12);
        // At most one victim of two potential ones, whatever the drawn count.
        Assert.Equal(0.5, IllnessSchedule.VictimChance(10, 2, 0.2, 0.5, 1), 12);
        // A zero count is clamped to one.
        Assert.Equal(1, IllnessSchedule.VictimChance(1, 1, 0.2, 0.4, 99999), 12);
        Assert.Equal(0, IllnessSchedule.VictimChance(3, 0, 0.2, 0.5, 99999));
    }

    [Fact]
    public void MissingFactsRefuseAndUnmodelledFactsNameTheUnassessedSchedule()
    {
        var table = DiseaseTableTests.Table();

        Assert.Throws<RequiredFactException>(() => Schedule(table, null!));
        Assert.Equal(["disease_storyteller_unknown:Ghost"], Schedule(table, Facts() with { Storyteller = "Ghost" }).Findings);
        Assert.Equal(["disease_biome_unknown:Moon"], Schedule(table, Facts() with { Biome = "Moon" }).Findings);
        Assert.Equal(["disease_victims_unobserved:Disease_OrganDecay"],
            Schedule(table, Facts(5, new DiseaseIncidentRead("Disease_Flu", 3), new DiseaseIncidentRead("Disease_GutWorms", 3))).Findings);
        Assert.False(Schedule(new DiseaseTable(), Facts()).Assessed);
    }

    // ----- the production way: the catalog's tables and the wire facts as native fills them, through the census threat section

    [Fact]
    public void TheCatalogAndTheNativeReadGiveAnAssessedIllnessThroughTheReviewInputs()
    {
        var catalog = DiseaseTableTests.Catalog(DiseaseTableTests.CoreDefs());
        var census = ColonyFactsCoreTests.Snapshot() with { Threat = new ThreatSection { Observed = ColonyFactsCoreTests.QuietThreat with { Storyteller = "Cassandra", DaysPassed = 20, Disease = Facts(20) } } };

        var inputs = HarmInputs.Of(new ReviewFacts { DiseaseTable = catalog.DiseaseTable(), Census = ColonyFactsCoreTests.Snapshot() with { Threat = new() { Observed = census.Threat.Observed } } });
        Assert.Throws<RequiredFactException>(() => HarmInputs.Of(new ReviewFacts
        {
            DiseaseTable = catalog.DiseaseTable(),
            Census = ColonyFactsCoreTests.Snapshot() with { Threat = new() { Observed = census.Threat.Observed with { Disease = null! } } },
        }));

        Assert.True(inputs.Illness.Assessed);
        Assert.Equal(1 / 60.0, inputs.Illness.IncidentsPerDay(30), 12);
        var flu = IllnessForecast.Of(inputs.Illness, Pawn(1, new DiseaseContract("Disease_Flu", 1), new DiseaseContract("Disease_GutWorms", 1),
            new DiseaseContract("Disease_OrganDecay", 1)), Tables, HarmConsequences.CareOf(Care()), 1, new ForecastValuePolicy(), Horizon, new StockArrival());
        Assert.True(flu.Known);
        Assert.True(flu.PatientDays > 0);
        Assert.True(flu.Treatments > 0);
        Assert.True(flu.DeathProbability is > 0 and < 1);
    }

    [Fact]
    public void AModdedStorytellersUnknownCompIsNamedAndItsDiseaseCompIsStillTheRate()
    {
        // #3859 the production way (#3822): a storyteller of Cassandra's disease comp and a mod's comp class, decoded by the
        // catalog and read through the review inputs. The unknown comp is named and sends nothing; the review does not throw.
        var modded = new RimWorld.StorytellerDef
        {
            defName = "Modded",
            comps = [new RimWorld.StorytellerCompProperties_Disease { category = DiseaseTableTests.DiseaseHuman, minDaysPassed = 9 }, new ThreatScheduleTests.StorytellerCompProperties_Test()],
        };
        var catalog = DiseaseTableTests.Catalog([.. DiseaseTableTests.CoreDefs(), modded]);

        var illness = HarmInputs.Of(new ReviewFacts { DiseaseTable = catalog.DiseaseTable(), Census = ColonyFactsCoreTests.Snapshot() with { Threat = new() { Observed = ColonyFactsCoreTests.QuietThreat with { Storyteller = "Modded", Disease = Facts(20) with { Storyteller = "Modded" } } } } }).Illness;

        Assert.True(illness.Assessed);
        Assert.Equal(["disease_comp_unmodelled:" + nameof(ThreatScheduleTests.StorytellerCompProperties_Test)], illness.Findings);
        Assert.Equal(1 / 60.0, illness.IncidentsPerDay(30), 12);
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var term = IllnessForecast.Term(illness, ["pawn"], new Dictionary<string, NeedsForecastPawn> { ["pawn"] = Pawn(1, new DiseaseContract("Disease_Flu", 1),
            new DiseaseContract("Disease_GutWorms", 1), new DiseaseContract("Disease_OrganDecay", 1)) }, Tables, Care(), new ForecastValuePolicy(), Horizon, new StockArrival(), null, findings);
        Assert.NotNull(term);
        Assert.Contains(illness.Findings[0], findings);
    }

    [Fact]
    public void AFreshColonyInABiomeThatSendsOrganDecayPricesItsOrgansWithoutThrowing()
    {
        // #3830 the production way (#3822): the catalog's organ decay with its heart, lungs and kidneys, a colony settled today, and a
        // pawn as the native read fills it (the torso's organs open, a natural lung on the map).
        var catalog = DiseaseTableTests.Catalog(DiseaseTableTests.CoreDefs());
        var illness = HarmInputs.Of(new ReviewFacts { DiseaseTable = catalog.DiseaseTable(), Census = ColonyFactsCoreTests.Snapshot() with { Threat = new() { Observed = ColonyFactsCoreTests.QuietThreat with { Disease = Facts(0) with { Biome = "AridShrubland" } } } } }).Illness;
        Assert.True(illness.Assessed);
        var decay = Assert.Single(illness.Incidents, i => i.Incident == Decay);
        Assert.Equal(["Heart", "Lung", "Kidney"], decay.Course.Parts.Select(p => p.Part));

        var pawn = Decaying(Body(), Replacement("Lung", "InstallNaturalLung")) with
        {
            Disease = Decaying(Body(), Replacement("Lung", "InstallNaturalLung")).Disease! with
            {
                Contracts = [new DiseaseContract("Disease_Flu", 1), new DiseaseContract(Decay, 1)],
            },
        };
        var d = IllnessForecast.Of(illness, pawn, Tables, HarmConsequences.CareOf(Care()), 1, new ForecastValuePolicy(), Horizon, new StockArrival());
        Assert.True(d.Known);
        Assert.DoesNotContain(d.Findings, f => f.StartsWith("disease_organ_loss_unpriced", StringComparison.Ordinal));
    }
}
