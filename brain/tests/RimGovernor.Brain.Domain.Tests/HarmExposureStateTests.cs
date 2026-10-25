using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HarmExposureStateTests
{
    [Theory]
    [InlineData("eligible", true, ExposureRoles.Unarmed)]
    [InlineData("drafted", true, ExposureRoles.Unarmed)]
    [InlineData("eligible", false, ExposureRoles.Noncombatant)]
    [InlineData("downed", true, ExposureRoles.Downed)]
    [InlineData("downed", false, ExposureRoles.Downed)]
    public void LiveAndSweepRolesUseTheSameFactsAndEachLivingStateNeedsItsOwnCell(string state, bool violence, string role)
    {
        var pawn = ForecastValueTests.HarmPawn() with { State = state, ViolenceCapable = violence };
        Assert.Equal(role, HarmLedger.RoleOf(pawn, ForecastValueTests.Cloth));
        var staged = new StagedFixture { Fixture = new LabFixture { Colonists = 1, Pawns = [new() { Side = CombatFixtures.Colonist }] } };
        staged.Pawns.Add(new Fx.StagedPawn { Id = pawn.Pawn, Kind = "test", Primary = Fx.StagedPrimary.None,
            Downed = state == "downed", ViolenceCapable = violence });
        Assert.Equal(role, Assert.Single(CombatLabBundle.Roles(staged)).Value);

        // Injected policy data exercises the price plumbing; it makes no claim about native calibration.
        var table = new ExposureTable(RaidExposure.Schema, [new("1-2", role, 100, 5, 3, 20, .5, .4, .6, 100, 100)]);
        var context = ForecastValueTests.Context(pawn, ForecastValueTests.HarmTables(), ForecastValueTests.RaidDay(table),
            new Dictionary<string, EquatableArray<GearOption>> { [pawn.Pawn] = ForecastValueTests.Cloth });
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), true,
            new Dictionary<string, ForecastNeedsResult> { [pawn.Pawn] = ForecastValueTests.Timeline(pawn) }, []);
        var rows = new Dictionary<string, NeedsForecastPawn> { [pawn.Pawn] = pawn };
        Assert.True(HarmLedger.FightLoss(1000, [pawn.Pawn], outcome, context, GameTime.TicksPerDay, new(), rows, null, new()).Loss > 0);
        Assert.True(Assert.IsType<ForecastTerm.Assessed>(HarmLedger.TreatmentsPerDay(context, rows, 1)).Value > 0);
        Assert.True(HarmLedger.RaidOn(context, pawn, new HarmConsequences.Care(true, 1, 0, 1), GameTime.TicksPerDay).Risk > 0);
        var missing = context with { Harm = context.Harm with { Exposure = new(RaidExposure.Schema, []) } };
        Assert.Contains($"{RaidExposure.CellUncalibrated}:1-2/{role}", Assert.Throws<RequiredFactException>(() =>
            HarmLedger.FightLoss(1000, [pawn.Pawn], outcome, missing, GameTime.TicksPerDay, new(), rows, null, new())).Fact);
    }

    [Fact]
    public void IncapabilityAndDownedStatusTakePrecedenceOverAWeaponAndOnlyDeathProvesNoFurtherHits()
    {
        var pawn = ForecastValueTests.HarmPawn() with { ViolenceCapable = false };
        Assert.Equal(ExposureRoles.Noncombatant, HarmLedger.RoleOf(pawn, [CreepJoinerHarmTests.Rifle]));
        Assert.Equal(ExposureRoles.Downed, HarmLedger.RoleOf(pawn with { State = "downed" }, [CreepJoinerHarmTests.Rifle]));
        var dead = HarmLedger.ExposureOf(new(RaidExposure.Schema, []), 1, pawn with { State = "dead" }, []);
        Assert.Equal(new HitRange(0, 0, 0), Assert.IsType<ExposureReading.Calibrated>(dead).Range);
        Assert.Contains("exposure_no_hits:dead:pawn", dead.Findings);
    }

    [Theory]
    [InlineData("eligible", true)]
    [InlineData("eligible", false)]
    [InlineData("downed", true)]
    public void EmbeddedCivilianEvidenceFlowsThroughFightCareAndRaidValuation(string state, bool violence)
    {
        var pawn = ForecastValueTests.HarmPawn() with { State = state, ViolenceCapable = violence };
        var context = ForecastValueTests.Context(pawn, ForecastValueTests.HarmTables(), ForecastValueTests.RaidDay(RaidExposure.Load()),
            new Dictionary<string, EquatableArray<GearOption>> { [pawn.Pawn] = ForecastValueTests.Cloth });
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), true,
            new Dictionary<string, ForecastNeedsResult> { [pawn.Pawn] = ForecastValueTests.Timeline(pawn) }, []);
        var rows = new Dictionary<string, NeedsForecastPawn> { [pawn.Pawn] = pawn };

        var findings = new SortedSet<string>(StringComparer.Ordinal);
        Assert.True(double.IsFinite(HarmLedger.FightLoss(1000, [pawn.Pawn], outcome, context, GameTime.TicksPerDay, new(), rows, null, findings).Loss));
        Assert.Contains(RaidExposure.EmpiricalEstimate, findings);
        Assert.Contains(RaidExposure.StabilityUnconfirmed, findings);
        if (state == "downed") Assert.Contains("exposure_sparse_hit_sample:1-2/downed:2/40", findings);
        Assert.True(double.IsFinite(Assert.IsType<ForecastTerm.Assessed>(HarmLedger.TreatmentsPerDay(context, rows, 1)).Value));
        Assert.True(double.IsFinite(HarmLedger.RaidOn(context, pawn, new HarmConsequences.Care(true, 1, 0, 1), GameTime.TicksPerDay).Risk));
        var reading = Assert.IsType<ExposureReading.Calibrated>(HarmLedger.ExposureOf(context.Harm.Exposure, 1, pawn, ForecastValueTests.Cloth));
        Assert.Contains(RaidExposure.EmpiricalEstimate, reading.Findings);
        Assert.Contains(RaidExposure.StabilityUnconfirmed, reading.Findings);
    }

    [Theory]
    [InlineData("lab-civilians-small", "0-0.5", 1)]
    [InlineData("lab-civilians-light", "0.5-1", 3)]
    [InlineData("lab-civilians-even", "1-2", 6)]
    [InlineData("lab-civilians-heavy", "2-4", 12)]
    [InlineData("lab-civilians-overrun", "4+", 24)]
    public void CalibrationFixturesCoverEachBinWithEachMissingNativeState(string name, string bin, int hostiles)
    {
        var fixture = CombatFixtures.Build(name, 80, 80);
        Assert.Equal(bin, fixture.ExposureBin);
        Assert.Equal(7, fixture.Colonists);
        Assert.Equal(hostiles, fixture.Pawns.Count(p => p.Side == CombatFixtures.Hostile));
        var civilians = fixture.Pawns.Where(p => p.Side == CombatFixtures.Colonist && p.Weapon.Length == 0).ToArray();
        Assert.Equal(3, civilians.Length);
        Assert.Single(civilians, p => p.Downed);
        Assert.Single(civilians, p => p.IncapableOfViolence);
        Assert.Single(civilians, p => !p.Downed && !p.IncapableOfViolence);
        Assert.True(civilians.Single(p => p.IncapableOfViolence).ToSpec("civilian").IncapableOfViolence);
    }
}
