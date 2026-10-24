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

    private static ColonyValue Value(ColonyModelInput input, HarmInputs? harm = null)
    {
        var context = ForecastValueTests.Context(ForecastValueTests.HarmPawn() with { Pawn = "Anna" }, ForecastValueTests.HarmTables(),
            harm ?? ForecastValueTests.RaidDay(), new Dictionary<string, EquatableArray<GearOption>> { ["Anna"] = ForecastValueTests.Cloth });
        return new ColonyValue(context, [], CalendarRules.YearDays, input.Pawns, ItemFactsFixtureRules.CoreItemFacts());
    }

    private static ColonyModelPolicy Policy(double tends = 0) => new() { MedicineTendsPerDay = Known(tends) };

    private static double Priced(ColonyModelInput input, double tends = 0)
    {
        var trajectory = ColonyModel.Run(input, [.. input.Pawns.Value.Select(p => PawnCycle.Year(input, p).ToArray())], Policy(tends));
        var result = Value(input).Price(trajectory);
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
