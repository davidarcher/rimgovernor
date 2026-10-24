using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

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

    private static ColonyModelInput Colony(int stored, long medicine, bool curve = true)
    {
        var doctor = SkillStockTests.WithMedicine(ProjectGraphTests.Pawn("Anna", null, null), SkillStockTests.Mechanics(stored, 0));
        doctor = curve ? doctor with { SkillStatCurves = EquatableArray.From(doctor.SkillStatCurves.Append(Tend())) } : doctor;
        var input = ProjectGraphTests.Colony();
        return input with
        {
            Pawns = Known(EquatableArray.From(new[] { doctor })),
            Stocks = input.Stocks with { Medicine = Known(EquatableArray.From(new[] { new Amount(new Resource(Industrial), medicine) })) },
        };
    }

    // Anna contracts every Core disease she is a victim of, and gains immunity at 1x.
    private static readonly NeedsForecastPawn Anna = ForecastValueTests.HarmPawn() with
    {
        Pawn = "Anna",
        Disease = NeedsFixtures.Disease with { Contracts = [new("Disease_Flu", 1), new("Disease_GutWorms", 1), new("Disease_OrganDecay", 1)] },
    };

    private static ColonyValue Value(ColonyModelInput input, HarmInputs? harm = null)
    {
        var context = ForecastValueTests.Context(Anna, ForecastValueTests.HarmTables(),
            harm ?? ForecastValueTests.RaidDay(), new Dictionary<string, EquatableArray<GearOption>> { ["Anna"] = ForecastValueTests.Cloth });
        return new ColonyValue(context, [], CalendarRules.YearDays, input.Pawns, ItemFactsFixtureRules.CoreItemFacts());
    }

    // #3829: a flu at 0.2 rising 0.2 a day kills at 1 unless immunity (0.25 x u a day) comes first; a tend lowers it 0.15 x quality a
    // day, so a better tend saves more of the victims. Every day of it halves consciousness.
    private static readonly DiseaseCourse Flu = new()
    {
        Table = new()
        {
            Known = true, Hediff = "Flu", InitialSeverity = 0.2, LethalSeverity = 1, Immunizable = new ImmunizableProps(0.25, 0.2, -0.5),
            Stages = [new PenaltyStage(0, 0, [new CapacityModifier("Consciousness", 0, null, 0.5)])],
        },
        Tendable = true, Tend = new TendCourse(-0.15, 0.5, 0.75, -1),
    };

    private static HarmInputs Sick(double perDay) => ForecastValueTests.RaidDay() with
    {
        Illness = new() { Assessed = true, Comps = [(0, perDay)], Incidents = [new IllnessIncident("Disease_Flu", 1, 1, Flu)] },
    };

    private static ColonyModelPolicy Policy(double tends = 0) => new() { MedicineTendsPerDay = Known(tends) };

    private static double Priced(ColonyModelInput input, HarmInputs? harm = null, double tends = 0)
    {
        var trajectory = ColonyModel.Run(input, [.. input.Pawns.Value.Select(p => PawnCycle.Year(input, p).ToArray())], Policy(tends));
        var result = Value(input, harm).Price(trajectory);
        Assert.Empty(result.Unpriced);
        return result.Value.Value;
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
    public void TheTendingNowIsTheBestDoctorsDayZeroQualityWithTheMedicineHeld()
    {
        // #3804: the hospital credit's tending. Anna at level 6 tends at 0.8 with the industrial medicine on hand.
        var input = Colony(6, 1000);
        var trajectory = ColonyModel.Run(input, [.. input.Pawns.Value.Select(p => PawnCycle.Year(input, p).ToArray())], Policy());
        var tending = Value(input).TendingNow(trajectory, new SortedSet<string>(StringComparer.Ordinal));
        Assert.NotNull(tending);
        Assert.True(tending.Tended);
        Assert.Equal(0.8, tending.Quality, 9);
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
    public void TheMedicineTendedADayAddsTheIllnessTermsTendsAtTheTendingNow()
    {
        var input = Colony(6, 1000);
        var raids = Value(input, Sick(0)).TreatmentsPerDay(input);
        var sick = Value(input, Sick(0.1)).TreatmentsPerDay(input);

        // Anna at level 6 tends at 0.8 with the industrial medicine on hand (TendingNow); a flu incident's tends at that quality.
        var flu = IllnessForecast.Of(Sick(0.1).Illness, Anna, ForecastValueTests.HarmTables(), new HarmConsequences.Care(true, 0.8, 0, 1), 1,
            new ForecastValuePolicy(), CalendarRules.YearDays * GameTime.TicksPerDay);
        Assert.True(flu.Treatments > 0);
        Assert.Equal(raids.Value + (0.1 * flu.Treatments), sick.Value, 9);
    }

    [Fact]
    public void AnUnassessedIllnessThrowsRatherThanPricingTheRaidsAlone()
    {
        var input = Colony(6, 1000);
        var trajectory = ColonyModel.Run(input, [.. input.Pawns.Value.Select(p => PawnCycle.Year(input, p).ToArray())], Policy());
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
        var illness = HarmInputs.Of(new RoundsFacts { DiseaseTable = catalog.DiseaseTable(), Disease = IllnessForecastTests.Facts(0) }).Illness;
        Assert.True(illness.Assessed);
        var input = Colony(3, 100_000);

        var sick = Marginals(input, ForecastValueTests.RaidDay() with { Illness = illness });

        Assert.True(sick.PerUnit[Key] > Marginals(input).PerUnit[Key], string.Join(',', sick.Findings));
        Assert.True(Value(input, ForecastValueTests.RaidDay() with { Illness = illness }).TreatmentsPerDay(input).Value > Value(input).TreatmentsPerDay(input).Value);
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
        var uncalibrated = new ExposureTable(RaidExposure.Schema, [new ExposureCell("1-2", ExposureRoles.Noncombatant, 5, 5, 3, 20, 0.5, 0.4, 0.6)]);
        var cell = Marginals(Colony(3, 100_000), ForecastValueTests.RaidDay(uncalibrated));

        Assert.Contains($"{RaidExposure.CellUncalibrated}:1-2/{ExposureRoles.Noncombatant}", cell.Findings);
        Assert.DoesNotContain(Key, cell.PerUnit.Keys);
        Assert.DoesNotContain(ProjectGraph.MedicineKey(new Resource(Industrial)), cell.PerUnit.Keys);
        Assert.True(cell.Value.IsKnown);

        var unqualified = Marginals(Colony(3, 100_000, curve: false));

        Assert.Contains($"{ColonyValue.MedicineSkillValueUnassessed}:Anna", unqualified.Findings);
        Assert.DoesNotContain(Key, unqualified.PerUnit.Keys);
    }
}
