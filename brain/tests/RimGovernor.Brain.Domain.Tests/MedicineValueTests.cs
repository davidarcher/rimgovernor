using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3635: tier 2 prices the colony's raid harm at the tend quality its best doctor's projected Medicine level and the medicine on hand
// give, so the per-XP and per-medicine marginals of #3634 carry the harm a better doctor or more medicine avoids. One raid a day on
// Anna, who is also the only doctor (ForecastValueTests' harm case: 5 hits of 10 sharp damage).
public sealed class MedicineValueTests
{
    private const string Key = "skill/Anna/Medicine";

    private static readonly string Industrial = "MedicineIndustrial";

    // Vanilla's MedicalTendQuality shape, 0.2 + 0.1 a level: industrial medicine's cap of 1 is reached at level 8.
    private static SkillStatCurve Tend() => new(ColonyValue.TendQualityStat, "Medicine",
        EquatableArray.From(Enumerable.Range(0, 21).Select(l => new SkillStatLevel(l, 0.2 + (0.1 * l)))), "test");

    private static ColonyModelInput Colony(int stored, long medicine, bool practice = false, bool curve = true)
    {
        var doctor = SkillStockTests.Practitioner(ProjectGraphTests.Pawn("Anna", null, null), SkillStockTests.Mechanics(stored, 0));
        doctor = curve ? doctor with { SkillStatCurves = EquatableArray.From(doctor.SkillStatCurves.Append(Tend())) } : doctor;
        var input = ProjectGraphTests.Colony();
        return input with
        {
            Pawns = Known(EquatableArray.From(new[] { doctor })),
            Practice = Known(practice ? EquatableArray.From(new[] { SkillStockTests.Slot() }) : EquatableArray.From(Array.Empty<ModelPracticeSlot>())),
            Stocks = input.Stocks with { Medicine = Known(EquatableArray.From(new[] { new Amount(new Resource(Industrial), medicine) })) },
        };
    }

    private static ColonyValue Value(ColonyModelInput input, HarmInputs? harm = null)
    {
        var context = ForecastValueTests.Context(ForecastValueTests.HarmPawn() with { Pawn = "Anna" }, ForecastValueTests.HarmTables(),
            harm ?? ForecastValueTests.RaidDay(), new Dictionary<string, EquatableArray<GearOption>> { ["Anna"] = ForecastValueTests.Cloth });
        return new ColonyValue(context, [], CalendarRules.YearDays, input.Pawns, ItemFactsFixtureRules.CoreItemFacts());
    }

    private static ColonyModelPolicy Policy(ColonyModelInput input, bool practice, double tends = 0) =>
        new() { MedicineTendsPerDay = Known(tends), Projects = practice ? ProjectGraph.Practice(input) : new ProjectGraph() };

    private static double Priced(ColonyModelInput input, double tends = 0)
    {
        var trajectory = ColonyModel.Run(input, [.. input.Pawns.Value.Select(p => PawnCycle.Year(input, p).ToArray())], Policy(input, false, tends));
        var result = Value(input).Price(trajectory);
        Assert.Empty(result.Unpriced);
        return result.Value.Value;
    }

    private static ProjectMarginals Marginals(ColonyModelInput input, HarmInputs? harm = null, bool practice = true, double tends = 0) =>
        ProjectMarginalValue.Of(input, Policy(input, practice, tends), Value(input, harm));

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
        Assert.True(Marginals(Colony(6, 30), practice: false, tends: 1).PerUnit[ProjectGraph.MedicineKey(new Resource(Industrial))] > 0);
    }

    [Fact]
    public void AnXpIsWorthMoreToALowLevelDoctorThanToOneAtTheMedicinesCap()
    {
        var low = Marginals(Colony(3, 100_000, practice: true));
        var high = Marginals(Colony(12, 100_000, practice: true));

        Assert.True(low.PerUnit[Key] > 0);
        Assert.True(low.PerUnit[Key] > high.PerUnit[Key]);
        // Industrial medicine already caps a level-12 doctor's tend at 1: more skill avoids nothing.
        Assert.Equal(0, high.PerUnit[Key]);
    }

    [Fact]
    public void NoThreatGivesAZeroMarginal()
    {
        var marginals = Marginals(Colony(3, 100_000, practice: true), ForecastValueTests.RaidDay(raidsPerDay: 0));

        Assert.Equal(0, marginals.PerUnit[Key]);
        Assert.Equal(0, marginals.PerUnit[ProjectGraph.MedicineKey(new Resource(Industrial))]);
    }

    [Fact]
    public void AnUncalibratedCellOrAMissingCurveGivesAFindingNotANumber()
    {
        var uncalibrated = new ExposureTable(RaidExposure.Schema, [new ExposureCell("1-2", ExposureRoles.Noncombatant, 5, 5, 3, 20, 0.5, 0.4, 0.6)]);
        var cell = Marginals(Colony(3, 100_000, practice: true), ForecastValueTests.RaidDay(uncalibrated));

        Assert.Contains($"{RaidExposure.CellUncalibrated}:1-2/{ExposureRoles.Noncombatant}", cell.Findings);
        Assert.DoesNotContain(Key, cell.PerUnit.Keys);
        Assert.DoesNotContain(ProjectGraph.MedicineKey(new Resource(Industrial)), cell.PerUnit.Keys);
        Assert.True(cell.Value.IsKnown);

        var unqualified = Marginals(Colony(3, 100_000, practice: true, curve: false));

        Assert.Contains($"{ColonyValue.MedicineSkillValueUnassessed}:Anna", unqualified.Findings);
        Assert.DoesNotContain(Key, unqualified.PerUnit.Keys);
    }
}
