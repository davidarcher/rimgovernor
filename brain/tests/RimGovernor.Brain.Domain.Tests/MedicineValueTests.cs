using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3635: tier 2 prices the colony's raid harm at the tend quality its best doctor's projected Medicine level and the medicine on hand
// give, so the per-XP (one level more over its XP, #3344) and per-medicine marginals carry the harm a better doctor or more medicine avoids. One raid a day on
// Anna, who is also the only doctor (ForecastValueTests' harm case: 5 hits of 10 sharp damage).
public sealed class MedicineValueTests
{
    private const string Key = "skill/Anna/Medicine";

    private static readonly string Industrial = "MedicineIndustrial";

    // Vanilla's MedicalTendQuality shape, 0.2 + 0.1 a level: industrial medicine's cap of 1 is reached at level 8.
    private static SkillStatCurve Tend() => new(ColonyValue.TendQualityStat, "Medicine",
        EquatableArray.From(Enumerable.Range(0, 21).Select(l => new SkillStatLevel(l, 0.2 + (0.1 * l)))), "test");

    internal static ColonyModelInput Colony(int stored, long medicine, bool curve = true)
    {
        var doctor = SkillStockTests.WithMedicine(ProjectGraphTests.Pawn("Anna", null, null), SkillStockTests.Mechanics(stored, 0));
        doctor = curve ? doctor with { SkillStatCurves = EquatableArray.From(doctor.SkillStatCurves.Append(Tend())) } : doctor;
        var input = ProjectGraphTests.Colony();
        return input with
        {
            Pawns = EquatableArray.From(new[] { doctor }),
            Stocks = input.Stocks with { Medicine = EquatableArray.From(new[] { new Amount(new Resource(Industrial), medicine) }) },
        };
    }

    // Anna contracts every Core disease she is a victim of, and gains immunity at 1x.
    private static readonly NeedsForecastPawn Anna = ForecastValueTests.HarmPawn() with
    {
        Pawn = "Anna",
        Disease = NeedsFixtures.Disease with { Contracts = [new("Disease_Flu", 1), new("Disease_GutWorms", 1), new("Disease_OrganDecay", 1)] },
    };

    private static ColonyValue Value(ColonyModelInput input, HarmInputs? harm = null, double days = CalendarRules.YearDays)
    {
        var context = ForecastValueTests.Context(Anna, ForecastValueTests.HarmTables(),
            harm ?? ForecastValueTests.RaidDay(), new Dictionary<string, EquatableArray<GearOption>> { ["Anna"] = ForecastValueTests.Cloth });
        return new ColonyValue(context, [], days, input.Pawns, ItemFactsFixtureRules.CoreItemFacts());
    }

    // #3829: a flu at 0.2 rising 0.2 a day kills at 1 unless immunity (0.25 x u a day) comes first; a tend lowers it 0.15 x quality a
    // day, so a better tend saves more of the victims. Every day of it halves consciousness.
    private static readonly DiseaseCourse Flu = new()
    {
        Table = new()
        {
            Known = true, Hediff = "Flu", InitialSeverity = 0.2, Lethality = new Lethality.Lethal(1), Immunizable = new ImmunizableProps(0.25, 0.2, -0.5),
            Stages = [new PenaltyStage(0, 0, [new CapacityModifier("Consciousness", 0, new CapacityCeiling.Uncapped(), 0.5)])],
        },
        Tendable = true, Tend = new TendCourse(-0.15, 0.5, 0.75, -1),
    };

    private static HarmInputs Sick(double perDay) => ForecastValueTests.RaidDay() with
    {
        Illness = new() { Assessed = true, Incidents = [new IllnessIncident("Disease_Flu", [(0, perDay)], 1, Flu)] },
    };

    internal static ColonyModelPolicy Policy(double tends = 0) => new() { MedicineTendsPerDay = tends };

    private static HarmInputs RaidAt(double day)
    {
        var harm = ForecastValueTests.RaidDay();
        return harm with { ScheduledRaids = [new(0, harm.Raid, harm.Composition)
            { Timing = new IncidentTiming.Scheduled(new(0, CalendarRules.YearDays, [day]), 1) }] };
    }

    [Fact]
    public void NativeRaidDatesConsumeTheSameTreatmentCountAtDifferentStockDates()
    {
        var input = Colony(6, 1000);
        ColonyTrajectory At(double day)
        {
            var value = Value(input, RaidAt(day));
            Assert.Equal(0, value.TreatmentsPerDay(input));
            var scheduled = value.ScheduledTreatments();
            var treatment = Assert.Single(scheduled);
            Assert.Equal(day, treatment.Day);
            return Run(input, Policy() with { Scheduled = scheduled });
        }

        var early = At(2);
        var late = At(20);
        Assert.Equal(early.Events[^1].Medicine.Sum(), late.Events[^1].Medicine.Sum(), 9);
        var between = early.Events.First(e => e.Day > 2 && e.Day < 20).Day;
        Assert.True(early.Events.First(e => e.Day == between).Medicine.Sum() < late.Events.First(e => e.Day == between).Medicine.Sum());
        // The event's debit is instantaneous, not spread over the interval leading up to it.
        var treatment = Assert.Single(late.Events, e => e.Kind == ModelEventKind.MedicineTreatment);
        Assert.Equal(1000, treatment.Medicine.Sum() + treatment.MedicineConsumed.Sum(), 9);
    }

    [Fact]
    public void MedicineArrivingAfterOneRaidGivesNoCreditUntilTheSameRaidOccursAfterArrival()
    {
        var input = Stocked(0, 0);
        double Gain(double day)
        {
            var value = Value(input, RaidAt(day));
            var policy = Policy(value.TreatmentsPerDay(input)) with { Scheduled = value.ScheduledTreatments() };
            var before = value.Price(Run(input, policy));
            var after = value.Price(Run(input, policy with { MedicineTrade = Trade(visits: (10, 2)) }));
            Assert.Empty(before.Unpriced);
            Assert.Empty(after.Unpriced);
            return after.Value - before.Value;
        }

        Assert.Equal(0, Gain(2), 9);
        Assert.True(Gain(20) > 0);
    }

    [Fact]
    public void NativeDiseaseDatesRetainTheCourseRetendDatesAndContinuousMtbRemainsARate()
    {
        var input = Colony(6, 1000);
        HarmInputs At(double day) => ForecastValueTests.RaidDay(raidsPerDay: 0) with
        {
            Illness = new() { Assessed = true, Incidents = [new IllnessIncident("Disease_Flu", [], 1, Flu)
                { Scheduled = [new IncidentTiming.Scheduled(new(0, CalendarRules.YearDays, [day]), 1)] }] },
        };
        var early = Value(input, At(2));
        var late = Value(input, At(20));
        var first = Run(input, Policy() with { Scheduled = early.ScheduledTreatments() }).Events.Where(e => e.MedicineConsumed.Sum() > 0).ToArray();
        var second = Run(input, Policy() with { Scheduled = late.ScheduledTreatments() }).Events.Where(e => e.MedicineConsumed.Sum() > 0).ToArray();
        Assert.Equal(0, early.TreatmentsPerDay(input));
        Assert.True(first.Length > 1);
        Assert.Equal(2, first[0].Day);
        Assert.Equal(2.5, first[1].Day);
        Assert.Equal(first.Select(e => e.Day + 18), second.Select(e => e.Day));
        Assert.Equal(first.Sum(e => e.MedicineConsumed.Sum()), second.Sum(e => e.MedicineConsumed.Sum()), 9);
        Assert.Empty(Value(input, Sick(0.1)).ScheduledTreatments());
        Assert.True(Value(input, Sick(0.1)).TreatmentsPerDay(input) > 0);
    }

    [Fact]
    public void TreatmentSchedulesRequireNativeCoverageThroughTheRequestedHorizon()
    {
        var input = Colony(6, 1000);
        var harm = RaidAt(2);
        harm = harm with { ScheduledRaids = [harm.ScheduledRaids[0] with
            { Timing = new IncidentTiming.Scheduled(new(0, 10, [2]), 1) }] };
        Assert.Equal("incident_cycle_window_unobserved", Assert.Throws<RequiredFactException>(() => Value(input, harm).ScheduledTreatments()).Fact);
    }

    [Fact]
    public void TreatmentSchedulesReadTheRequestedHorizonBeyondOneYear()
    {
        var input = Colony(6, 1000);
        var harm = RaidAt(70);
        harm = harm with { ScheduledRaids = [harm.ScheduledRaids[0] with
            { Timing = new IncidentTiming.Scheduled(new(0, 90, [70]), 1) }] };
        var value = Value(input, harm, 90);
        Assert.Equal(0, value.TreatmentsPerDay(input));
        Assert.Equal(70, Assert.Single(value.ScheduledTreatments()).Day);
    }

    [Fact]
    public void ActualRetendQualityChangesRecoveryAndTheMedicineDebitedByTheProductionRun()
    {
        var chronic = Flu with
        {
            Table = Flu.Table with { Immunizable = null, Lethality = new Lethality.Harmless() },
            Tend = new TendCourse(0, 0.5, 0.75, 1.5),
        };
        var harm = ForecastValueTests.RaidDay(raidsPerDay: 0) with
        {
            Illness = new() { Assessed = true, Incidents = [new IllnessIncident("Disease_Flu", [], 1, chronic)
                { Scheduled = [new IncidentTiming.Scheduled(new(0, CalendarRules.YearDays, [2]), 1)] }] },
        };
        ColonyTrajectory Treat(ColonyModelInput input) => Run(input, Policy() with { Scheduled = Value(input, harm).ScheduledTreatments() });
        var industrial = Treat(Stocked(100, 0));
        var switches = Treat(Stocked(1, 100));
        // Two industrial tends at .8 cure it. After the first industrial unit, herbal tends at .48 take two further tends.
        Assert.Equal(2, industrial.Events.Sum(e => e.MedicineConsumed.Sum()), 9);
        Assert.Equal(3, switches.Events.Sum(e => e.MedicineConsumed.Sum()), 9);
        Assert.Equal(98, industrial.Events[^1].Medicine.Sum(), 9);
        Assert.Equal(98, switches.Events[^1].Medicine.Sum(), 9);

        var input = Stocked(1, 100);
        var policy = Policy() with { HorizonDays = 4, Scheduled = Value(input, harm).ScheduledTreatments() };
        var whole = Run(input, policy);
        var cut = whole.Events.Single(e => e.Day == 2.5 && e.Kind == ModelEventKind.MedicineTreatment);
        var carried = ColonyModelState.Observed(input, policy) with
        {
            Day = cut.Day, Nutrition = cut.Levels[(int)ModelStock.Nutrition],
            Medicine = EquatableArray.From(whole.MedicineKinds.Select((kind, k) => new ModelResourceStock(kind, cut.Medicine[k]))),
            TreatmentHistory = whole with { Events = EquatableArray.From(whole.Events.Where(e => e.Day <= cut.Day)) },
        };
        var continued = ColonyModel.Run(input, carried, [.. input.Pawns.Select(p => PawnCycle.Year(input, p).ToArray())], policy);
        Assert.Equal(whole.Events[^1].Medicine, continued.Events[^1].Medicine);
        Assert.Equal(whole.Events.Where(e => e.Day > cut.Day).Sum(e => e.MedicineConsumed.Sum()), continued.Events.Sum(e => e.MedicineConsumed.Sum()), 9);
    }

    private static double Priced(ColonyModelInput input, HarmInputs? harm = null, double tends = 0)
    {
        var trajectory = ColonyModel.Run(input, [.. input.Pawns.Select(p => PawnCycle.Year(input, p).ToArray())], Policy(tends));
        var result = Value(input, harm).Price(trajectory);
        Assert.Empty(result.Unpriced);
        return result.Value;
    }

    private static ProjectMarginals Marginals(ColonyModelInput input, HarmInputs? harm = null, double tends = 0) =>
        ProjectMarginalValue.Of(input, Policy(tends), Value(input, harm));

    [Fact]
    public void AHigherProjectedMedicineLevelLowersTheTrajectorysHarm()
    {
        // Level 3 tends at 0.5, level 6 at 0.8: the wounds heal faster, so the run is worth more.
        Assert.True(Priced(Colony(6, 1000)) > Priced(Colony(3, 1000)));
    }

    [Fact]
    public void RunningOutOfMedicineRaisesTheHarm()
    {
        var stocked = Priced(Colony(6, 1000), tends: 1);
        var runsOut = Priced(Colony(6, 30), tends: 1);
        var none = Priced(Colony(6, 0), tends: 1);

        // Without medicine the tend is at the native 0.3 potency: 0.24 instead of 0.8, from the day the 30 units run out.
        Assert.True(stocked > runsOut);
        Assert.True(runsOut > none);
        // A unit more puts the run-out off by a day of better tending.
        Assert.True(Marginals(Colony(6, 30), tends: 1).PerUnit[ProjectGraph.MedicineKey(new Resource(Industrial))] > 0);
    }

    [Fact]
    public void AnXpIsWorthMoreToALowLevelDoctorThanToOneAtTheMedicinesCap()
    {
        var low = Marginals(Colony(3, 100_000));
        var high = Marginals(Colony(12, 100_000));

        // The level-4 doctor's lower raid harm over the 100 000 XP level 3 takes (#3344).
        Assert.True(low.PerUnit[Key] > 0);
        Assert.Equal((Priced(Colony(4, 100_000)) - Priced(Colony(3, 100_000))) / 100_000, low.PerUnit[Key], 12);
        Assert.True(low.PerUnit[Key] > high.PerUnit[Key]);
        // Industrial medicine already caps a level-12 doctor's tend at 1: more skill avoids nothing.
        Assert.Equal(0, high.PerUnit[Key]);
    }

    [Fact]
    public void TheStockTargetBillCraftsMedicineUpToTheTargetAtTheCraftersRateUntilTheIngredientsRunOut()
    {
        // #3868: 30 industrial medicine held, tended at 1 a day, crafted at 2 a day toward a target of 3.
        double Held(ColonyModelInput input, ColonyModelPolicy policy, double day)
        {
            var trajectory = ColonyModel.Run(input, [.. input.Pawns.Select(p => PawnCycle.Year(input, p).ToArray())], policy);
            // The stock moves at a constant rate between events: interpolate at the day.
            var (from, at) = (0.0, trajectory.StartMedicine.Sum());
            foreach (var e in trajectory.Events)
            {
                if (e.Day >= day)
                {
                    return e.Day > from ? at + ((e.Medicine.Sum() - at) * (day - from) / (e.Day - from)) : e.Medicine.Sum();
                }

                (from, at) = (e.Day, e.Medicine.Sum());
            }

            return at;
        }

        MedicineCraft Craft(double units) => new(new Resource(Industrial), 2, units, 3, []);

        // Above the target nothing is crafted: the stock falls a unit a day to the target on day 27, then holds there.
        Assert.Equal(30 - 10, Held(Colony(6, 30), Policy(1) with { MedicineCraft = Craft(1000) }, 10), 6);
        Assert.Equal(3, Held(Colony(6, 30), Policy(1) with { MedicineCraft = Craft(1000) }, 200), 6);
        // Untended, the stock above the target stays put.
        Assert.Equal(30, Held(Colony(6, 30), Policy(0) with { MedicineCraft = Craft(1000) }, 200), 6);
        // From an empty stock it is crafted at the full rate, not the tends a day, until the target.
        Assert.Equal(3, Held(Colony(6, 0), Policy(1) with { MedicineCraft = Craft(1000) }, 100), 6);
        // Five units of ingredients make five: the stock then runs out.
        Assert.Equal(0, Held(Colony(6, 30), Policy(1) with { MedicineCraft = Craft(5) }, 200), 6);
    }

    // #3907: the routine trade buys the cheapest kind (herbal) on the medical reserve's latch, counted without the best kind (industrial
    // here): 1 colonist starts it below 1 and stops it at 3.
    private static readonly Resource Herbal = new("MedicineHerbal");

    private static MedicineTrade Trade(double units = 1000, double claimed = 0, bool active = false, params (double From, double PerDay)[] visits) =>
        new(EquatableArray.From((visits.Length == 0 ? [(0.0, 2.0)] : visits).Select(v => new MedicineTradeSource(Herbal, v.From, v.PerDay, v.PerDay))), units, claimed,
            new Resource(Industrial), 1, MedicalReservePolicy.Default(), active, [new Resource(Industrial), Herbal]);

    private static ColonyModelInput Stocked(double industrial, double herbal)
    {
        var input = Colony(6, 0);
        return input with { Stocks = input.Stocks with { Medicine = EquatableArray.From(new[] { new Amount(new Resource(Industrial), (long)industrial), new Amount(Herbal, (long)herbal) }) } };
    }

    private static ColonyTrajectory Run(ColonyModelInput input, ColonyModelPolicy policy) =>
        ColonyModel.Run(input, [.. input.Pawns.Select(p => PawnCycle.Year(input, p).ToArray())], policy);

    private static (double Day, double Industrial, double Herbal) At(ColonyTrajectory run, string id)
    {
        var e = run.Events.First(e => e.Kind == ModelEventKind.MedicineTrade && e.Id == id);
        var kinds = run.MedicineKinds.ToList();
        return (Math.Round(e.Day, 9), Math.Round(e.Medicine[kinds.IndexOf(new Resource(Industrial))], 9), Math.Round(e.Medicine[kinds.IndexOf(Herbal)], 9));
    }

    [Fact]
    public void TheRoutineTradeBuysTheCheapestKindBelowTheEntryUntilTheTargetCountingWithoutTheBestKind()
    {
        // No herbal held, the 30 industrial not counted: below the entry of 1, so the trade buys 2 a day while tending takes the industrial
        // at 1 a day. It stops at 3 herbal on day 1.5, the industrial then 28.5.
        var run = Run(Stocked(30, 0), Policy(1) with { MedicineTrade = Trade() });
        Assert.Equal((1.5, 28.5, 3.0), At(run, "stop"));
        // The industrial runs out on day 30; tending then takes the herbal 3 → 1 by day 32, where the trade starts again and gains a net
        // unit a day to the target on day 34.
        Assert.Equal(30, At(run, "best").Day);
        Assert.Equal((32.0, 0.0, 1.0), At(run, "start"));
        Assert.Contains(run.Events, e => e.Kind == ModelEventKind.MedicineTrade && e.Id == "stop" && Math.Abs(e.Day - 34) < 1e-9);
        Assert.Contains(MedicineTrade.LaterBuysFinding, run.Findings);
    }

    [Fact]
    public void TheLatchStartsBelowTheEntryAndHoldsBetweenTheEntryAndTheTarget()
    {
        // 2 herbal, between the entry (1) and the target (3): an inactive latch buys nothing and starts on day 31 when tending has taken
        // the herbal below 1 (the industrial lasts 30 days) …
        var idle = Run(Stocked(30, 2), Policy(1) with { MedicineTrade = Trade(active: false) });
        Assert.DoesNotContain(idle.Events, e => e.Kind == ModelEventKind.MedicineTrade && e.Day < 29);
        Assert.Equal(31, At(idle, "start").Day);
        // … an active one keeps buying to the target, 2 → 3 at 2 a day.
        var held = Run(Stocked(30, 2), Policy(1) with { MedicineTrade = Trade(active: true) });
        Assert.Equal((0.5, 29.5, 3.0), At(held, "stop"));
    }

    [Fact]
    public void TheTradeBuysNothingBeforeItsVisitsStartAndNoMoreThanTheSilverPays()
    {
        // The visits start on day 5, so the stock is bought from then: 3 herbal at 2 a day by day 6.5.
        var late = Run(Stocked(30, 0), Policy(1) with { MedicineTrade = Trade(visits: (5.0, 2.0)) });
        Assert.Equal(5, At(late, "visits").Day);
        Assert.Equal((6.5, 23.5, 3.0), At(late, "stop"));
        // Two units of silver buy two: spent on day 1; the latch stays on below the target.
        var spent = Run(Stocked(30, 0), Policy(1) with { MedicineTrade = Trade(units: 2) });
        Assert.Equal((1.0, 29.0, 2.0), At(spent, "silver"));
        Assert.DoesNotContain(spent.Events, e => e.Kind == ModelEventKind.MedicineTrade && e.Id == "stop");
    }

    [Fact]
    public void ABuyBeyondTodaysGapIsNamedAndOneWithinItIsNot()
    {
        // The gap of 3 is claimed: 3 bought is within it; 3 bought against a claim of 1 is not.
        var within = Run(Stocked(30, 0), Policy(1) with { MedicineTrade = Trade(units: 3, claimed: 3) });
        var beyond = Run(Stocked(30, 0), Policy(1) with { MedicineTrade = Trade(units: 3, claimed: 1) });
        Assert.DoesNotContain(MedicineTrade.LaterBuysFinding, within.Findings);
        Assert.Contains(MedicineTrade.LaterBuysFinding, beyond.Findings);
    }

    [Fact]
    public void TheTendingOverTheHorizonAveragesTheQualityAsTheMedicineRunsOutOrIsCrafted()
    {
        // #3804, #3868: the hospital credit's tending. Anna at level 6 tends at 0.8 with the industrial medicine on hand and at
        // 0.8 × 0.3 without; the horizon's average falls when the stock runs out, and crafting toward the target keeps it up.
        ForecastTending Tending(ColonyModelInput input, ColonyModelPolicy policy)
        {
            var trajectory = ColonyModel.Run(input, [.. input.Pawns.Select(p => PawnCycle.Year(input, p).ToArray())], policy);
            var tending = Value(input).TendingOverHorizon(trajectory, new SortedSet<string>(StringComparer.Ordinal));
            Assert.NotNull(tending);
            Assert.True(tending.Tended);
            return tending;
        }

        var stocked = Tending(Colony(6, 1000), Policy(1)).Quality;
        var runsOut = Tending(Colony(6, 30), Policy(1)).Quality;
        var crafted = Tending(Colony(6, 30), Policy(1) with { MedicineCraft = new MedicineCraft(new Resource(Industrial), 2, 1000, 3, []) }).Quality;
        Assert.True(stocked >= 0.8 - 1e-9);
        Assert.True(runsOut < stocked);
        Assert.True(runsOut > 0.8 * 0.3);
        Assert.Equal(stocked, crafted, 9);
    }

    [Fact]
    public void ADiseaseRateRaisesTheWorthOfTheDoctorsSkillAndOfTheMedicine()
    {
        var medicine = ProjectGraph.MedicineKey(new Resource(Industrial));
        var sick = Marginals(Colony(3, 100_000), Sick(0.1));
        var well = Marginals(Colony(3, 100_000), Sick(0));

        // The level-4 doctor's better tend saves flu victims as well as raid wounds.
        Assert.True(sick.PerUnit[Key] > well.PerUnit[Key], string.Join(',', sick.Findings));
        Assert.Equal((Priced(Colony(4, 100_000), Sick(0.1)) - Priced(Colony(3, 100_000), Sick(0.1))) / 100_000, sick.PerUnit[Key], 12);

        // A unit more puts the run-out off by a day of the medicine's better tend, on the flu too.
        Assert.True(Marginals(Colony(6, 30), Sick(0.1), tends: 1).PerUnit[medicine] > Marginals(Colony(6, 30), Sick(0), tends: 1).PerUnit[medicine]);
    }

    [Fact]
    public void RaidTreatmentDemandDistinguishesNoRaidFromMissingRaidEvidence()
    {
        var input = Colony(6, 1000);
        var pawn = ForecastValueTests.HarmPawn();
        var context = ForecastValueTests.Context(pawn, ForecastValueTests.HarmTables(), ForecastValueTests.RaidDay());
        var pawns = new Dictionary<string, NeedsForecastPawn> { [pawn.Pawn] = pawn };
        Assert.IsType<ForecastTerm.Assessed>(HarmLedger.TreatmentsPerDay(context with { Harm = ForecastValueTests.RaidDay(raidsPerDay: 0) }, pawns, 1));
        Assert.Equal(0d, ((ForecastTerm.Assessed)HarmLedger.TreatmentsPerDay(context with { Harm = ForecastValueTests.RaidDay(raidsPerDay: 0) }, pawns, 1)).Value);
        var missing = context with { Harm = context.Harm with { Schedule = new() } };
        Assert.Equal(HarmLedger.ThreatScheduleUnknown, Assert.IsType<ForecastTerm.Unassessed>(HarmLedger.TreatmentsPerDay(missing, pawns, 1)).Reason);
        Assert.Equal(HarmLedger.ThreatScheduleUnknown, Assert.Throws<RequiredFactException>(() => Value(input, missing.Harm).TreatmentsPerDay(input)).Fact);
    }
    [Fact]
    public void TheMedicineTendedADayAddsTheIllnessTermsTendsAtTheDayZeroTending()
    {
        var input = Colony(6, 1000);
        var raids = Value(input, Sick(0)).TreatmentsPerDay(input);
        var sick = Value(input, Sick(0.1)).TreatmentsPerDay(input);

        // Anna at level 6 tends at 0.8 with the industrial medicine on hand (its day-0 tending); a flu incident's tends at that quality.
        var flu = IllnessForecast.Of(Sick(0.1).Illness, Anna, ForecastValueTests.HarmTables(), new HarmConsequences.Care(true, 0.8, 0, 1), 1,
            new ForecastValuePolicy(), CalendarRules.YearDays * GameTime.TicksPerDay, new StockArrival());
        Assert.True(flu.Treatments > 0);
        Assert.Equal(raids + (0.1 * flu.Treatments), sick, 9);
    }

    [Fact]
    public void AnUnassessedIllnessThrowsRatherThanPricingTheRaidsAlone()
    {
        var input = Colony(6, 1000);
        var trajectory = ColonyModel.Run(input, [.. input.Pawns.Select(p => PawnCycle.Year(input, p).ToArray())], Policy());
        var unread = Value(input, ForecastValueTests.RaidDay() with { Illness = new() { Findings = ["disease_facts_unobserved"] } });

        Assert.Equal("illness_schedule", Assert.Throws<RequiredFactException>(() => unread.Price(trajectory)).Fact);
        Assert.Throws<RequiredFactException>(() => unread.TreatmentsPerDay(input));
    }

    [Fact]
    public void AFreshColonysProductionIllnessPricesTheDoctorWithoutThrowing()
    {
        // The production way (#3822): the Core disease defs through the catalog, and the native disease read of a colony settled today
        // (Cassandra's comp starts on day 9), through the review's harm inputs.
        var catalog = Game.DiseaseTableTests.Catalog(Game.DiseaseTableTests.CoreDefs());
        var illness = HarmInputs.Of(new ReviewFacts { DiseaseTable = catalog.DiseaseTable(), Census = ColonyFactsCoreTests.Snapshot() with { Threat = new() { Observed = ColonyFactsCoreTests.QuietThreat with { Disease = IllnessForecastTests.Facts(0) } } } }).Illness;
        Assert.True(illness.Assessed);
        var input = Colony(3, 100_000);

        var sick = Marginals(input, ForecastValueTests.RaidDay() with { Illness = illness });

        Assert.True(sick.PerUnit[Key] > Marginals(input).PerUnit[Key], string.Join(',', sick.Findings));
        Assert.True(Value(input, ForecastValueTests.RaidDay() with { Illness = illness }).TreatmentsPerDay(input) > Value(input).TreatmentsPerDay(input));
    }

    [Fact]
    public void NoThreatGivesAZeroMarginal()
    {
        var marginals = Marginals(Colony(3, 100_000), ForecastValueTests.RaidDay(raidsPerDay: 0));

        Assert.Equal(0, marginals.PerUnit[Key]);
        Assert.Equal(0, marginals.PerUnit[ProjectGraph.MedicineKey(new Resource(Industrial))]);
    }

    [Fact]
    public void AnUncalibratedCellOrAMissingCurveGivesAFindingNotANumber()
    {
        var uncalibrated = new ExposureTable(RaidExposure.Schema, [new ExposureCell("1-2", ExposureRoles.Unarmed, 5, 5, 3, 20, 0.5, 0.4, 0.6)]);
        var cell = Marginals(Colony(3, 100_000), ForecastValueTests.RaidDay(uncalibrated));

        Assert.Contains($"{RaidExposure.CellUncalibrated}:1-2/{ExposureRoles.Unarmed}", cell.Findings);
        Assert.DoesNotContain(Key, cell.PerUnit.Keys);
        Assert.DoesNotContain(ProjectGraph.MedicineKey(new Resource(Industrial)), cell.PerUnit.Keys);
        Assert.True(double.IsFinite(cell.Value));

        var unqualified = Marginals(Colony(3, 100_000, curve: false));

        Assert.Contains($"{ColonyValue.MedicineSkillValueUnassessed}:Anna", unqualified.Findings);
        Assert.DoesNotContain(Key, unqualified.PerUnit.Keys);
    }
}
