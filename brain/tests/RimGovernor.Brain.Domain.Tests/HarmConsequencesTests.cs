using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3544: the health consequences of one raid for one pawn. Each claim is one rule, over injected hits and responses.
public sealed class HarmConsequencesTests
{
    private const double Day = GameTime.TicksPerDay;

    private static NeedsForecastPawn Pawn() => NeedsFixtures.Pawn() with
    {
        Pawn = "pawn",
        Consciousness = 1d, Moving = 1d, Manipulation = 1d, Sight = 1d, Talking = 1d,
        PainShockThreshold = 0.8, HealthScale = 1d, IncomingDamageFactor = 1d, InjuryHealingFactor = 1d,
    };

    private static HediffPenaltyTable BloodLoss() => new()
    {
        Known = true, Hediff = "BloodLoss", Lethality = new Lethality.Lethal(1),
        Stages = [new PenaltyStage(0, 0, [new CapacityModifier("Consciousness", 0, new CapacityCeiling.Uncapped(), 0.9)])],
    };

    private static HediffPenaltyTable WoundInfection() => new()
    {
        Known = true, Hediff = "WoundInfection", Lethality = new Lethality.Lethal(1), InitialSeverity = 0.2,
        Immunizable = new ImmunizableProps(1, 0.4, -0.5),
        Stages = [new PenaltyStage(0, 0, [new CapacityModifier("Consciousness", 0, new CapacityCeiling.Uncapped(), 0.8)])],
    };

    private static CapacityPenaltyTables Tables(bool infection = true) => new()
    {
        BloodLoss = BloodLoss(), WoundInfection = infection ? WoundInfection() : new(), MovingMinForCapable = 0.15,
    };

    private static WeaponBin[] Bins(double damage) => [RaidPartLossTests.Gun(damage)];

    private static ArmorResponse[] Response(double damage, double bleed) => [new() { ExpectedDamage = damage, BleedChance = bleed }];

    private static ForecastSupportConsequences Care(double firstCareTicks) => new()
    {
        CareServices = "demand/tend", FirstCareTicks = firstCareTicks, TendQuality = 0.5, CareRoom = new CareRoomRead.Assessed(0.8),
    };

    private static HarmDistribution Of(HitRange hits, double damage = 2, double bleed = 0.8, NeedsForecastPawn? pawn = null,
        CapacityPenaltyTables? tables = null, ForecastSupportConsequences? support = null) =>
        HarmConsequences.Of(hits, Bins(damage), Response(damage, bleed), new RaidPartInputs { Catalog = RaidPartLossTests.Catalog }, pawn ?? Pawn(), tables ?? Tables(false), support, new ForecastValuePolicy(), 5 * Day);

    private static double Mean(HarmDistribution d) => d.Points.Sum(p => p.Loss * p.Probability);

    [Fact]
    public void ACareWithoutAnAssessedRoomIsPricedAtOneAndNamed()
    {
        var assessed = HarmConsequences.CareOf(Care(0));
        var unassessed = HarmConsequences.CareOf(Care(0) with { CareRoom = new CareRoomRead.Unassessed() });

        Assert.False(assessed.RoomUnobserved);
        Assert.Equal(0.8, assessed.RoomFactor, 9);
        Assert.True(unassessed.RoomUnobserved);
        Assert.Equal(1, unassessed.RoomFactor);
        Assert.True(unassessed.Tended);
    }

    [Fact]
    public void ZeroHitsLoseNothing()
    {
        var d = Of(new HitRange(0, 0, 0));

        Assert.True(d.Known);
        Assert.All(d.Points, p => Assert.Equal(0, p.Loss));
        Assert.Equal(1, d.Points.Sum(p => p.Probability), 9);
        Assert.Equal(0, d.DeathProbability);
        Assert.Equal(0, d.DownProbability);
    }

    [Fact]
    public void ExpectedDamageOverTheLethalThresholdAtTheHighEndKillsWithTheTerminalValueInTheTail()
    {
        // 10 damage a hit: 20 hits at the range's high is 200 severity, over 150 x 1.
        var d = Of(new HitRange(5, 3, 20), damage: 10, bleed: 0);

        Assert.True(d.DeathProbability > 0);
        Assert.True(d.DeathProbability < 1);
        Assert.Contains(d.Points, p => p.Loss >= new ForecastValuePolicy().DeathTerminalValue);
        Assert.Equal(1, d.Points.Sum(p => p.Probability), 9);
    }

    [Fact]
    public void AHalvedBleedChanceLowersTheBleedingLoss()
    {
        // 6 damage in all: below the pain threshold, so the loss is blood loss alone.
        var full = Mean(Of(new HitRange(3, 3, 3), bleed: 0.8));
        var half = Mean(Of(new HitRange(3, 3, 3), bleed: 0.4));

        Assert.True(full > 0);
        Assert.True(half > 0);
        Assert.True(half < 0.9 * full);
    }

    [Fact]
    public void FirstCareInAnHourLosesLessThanUntended()
    {
        var hits = new HitRange(3, 3, 3);

        var care = Mean(Of(hits, support: Care(Day / 24)));
        var untended = Mean(Of(hits, support: null));

        Assert.True(care < untended);
    }

    [Fact]
    public void UnknownInfectionLeavesThatPartUnassessedWithTheFindingAndPricesTheRest()
    {
        var hits = new HitRange(3, 3, 3);

        var without = Of(hits, tables: Tables(infection: false));
        var with = Of(hits, tables: Tables(infection: true), damage: 2);

        Assert.True(without.Known);
        Assert.Contains("capacity_penalty_table_unknown:WoundInfection", without.Findings);
        Assert.DoesNotContain("capacity_penalty_table_unknown:WoundInfection", with.Findings);
        Assert.True(Mean(without) > 0);
        Assert.True(Mean(with) > Mean(without));
    }

    [Fact]
    public void AdaptationFeedbackIsAlwaysReportedUnassessed()
    {
        Assert.Contains("adaptation_feedback_unassessed", Of(new HitRange(1, 1, 1)).Findings);
    }
}
