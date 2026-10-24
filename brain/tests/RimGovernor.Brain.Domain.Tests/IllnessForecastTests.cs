using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain.Tests.Game;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3824: the illness forecast. A known incident rate and course give the patient-days, tends and death chance worked out by hand
// from the course's own numbers; the schedule's rate, shares and victim chance follow the storyteller comp and the incident worker.
public sealed class IllnessForecastTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 30 * Day;

    private static NeedsForecastPawn Pawn(double gain = 1, params DiseaseContract[] contracts) => new()
    {
        Pawn = "pawn",
        Consciousness = Known(1d), Moving = Known(1d), Manipulation = Known(1d), Sight = Known(1d), Talking = Known(1d),
        Disease = new PawnDiseaseFacts { ImmunityGainSpeed = gain, Contracts = EquatableArray.From(contracts) },
    };

    private static readonly CapacityPenaltyTables Tables = new() { MovingMinForCapable = 0.15 };

    private static ForecastSupportConsequences Care(double quality = 0.5) => new()
    {
        CareServices = "demand/tend", FirstCareTicks = 0, TendQuality = Known(quality), CareRoomFactor = Known(0.8),
    };

    private static HediffPenaltyTable Table(string hediff, double initial, double? lethal = null, ImmunizableProps? immunizable = null,
        (double, double)? gone = null) => new()
    {
        Known = true, Hediff = hediff, InitialSeverity = initial, LethalSeverity = lethal, Immunizable = immunizable, DisappearsAfterTicks = gone,
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
        Assessed = true, Comps = [(0, perDay)], Incidents = [new IllnessIncident(incident, 1, 1, course)],
    };

    private static HarmDistribution Of(DiseaseCourse course, string incident, ForecastSupportConsequences? support, double gain = 1) =>
        IllnessForecast.Of(Schedule(course, incident), Pawn(gain, new DiseaseContract(incident, 1)), Tables, HarmConsequences.CareOf(support), 1, new ForecastValuePolicy(), Horizon);

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

    [Fact]
    public void ALethalPartShareKillsOnlyOnThatShare()
    {
        var decay = new DiseaseCourse
        {
            Table = Table("OrganDecay", 0.001, 1), PerDayMin = 0.05, PerDayMax = 0.05, MaxSeverity = 1, DestroysOrgan = true, LethalPartShare = 0.25,
        };

        var d = Of(decay, "Disease_OrganDecay", null);

        Assert.Equal(0.25, d.DeathProbability, 9);
        Assert.Contains("disease_organ_loss_unpriced:OrganDecay", d.Findings);
    }

    [Fact]
    public void TheContractChanceAndTheVictimChanceScaleTheSickShare()
    {
        var schedule = Schedule(Flu()) with { Incidents = [new IllnessIncident("Disease_Flu", 1, 0.5, Flu())] };

        var d = IllnessForecast.Of(schedule, Pawn(1, new DiseaseContract("Disease_Flu", 0.5)), Tables, HarmConsequences.CareOf(Care()), 1, new ForecastValuePolicy(), Horizon);
        var missing = IllnessForecast.Of(schedule, Pawn(1), Tables, HarmConsequences.CareOf(Care()), 1, new ForecastValuePolicy(), Horizon);

        Assert.Equal(0.25 * 0.5, d.DeathProbability, 9);
        Assert.False(missing.Known);
        Assert.Contains("disease_contract_unobserved:pawn:Disease_Flu", missing.Findings);
    }

    [Fact]
    public void APawnWithoutItsIllnessReadIsUnknownWithAFinding()
    {
        var d = IllnessForecast.Of(Schedule(Flu()), Pawn() with { Disease = null }, Tables, HarmConsequences.CareOf(Care()), 1, new ForecastValuePolicy(), Horizon);

        Assert.False(d.Known);
        Assert.Equal(["illness_unobserved:pawn"], d.Findings);
    }

    // ----- the schedule

    private static DiseaseFacts Facts(double settled = 5, params DiseaseIncidentRead[] incidents) => new()
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

        var schedule = IllnessSchedule.Of(table, Facts());

        Assert.True(schedule.Assessed);
        Assert.Empty(schedule.Findings);
        // Cassandra's comp starts on day 9, four days from now: over 30 days it runs 26.
        Assert.Equal(1 / 60.0 * 26 / 30, schedule.IncidentsPerDay(30), 12);
        Assert.Equal(1 / 120.0, IllnessSchedule.Of(table, Facts(20) with { IntervalFactor = 2 }).IncidentsPerDay(30), 12);
        Assert.Equal(0, IllnessSchedule.Of(table, Facts() with { IntervalFactor = 100 }).IncidentsPerDay(30));
        Assert.Equal(0, IllnessSchedule.Of(table, Facts() with { RandomDiseases = false }).IncidentsPerDay(30));
    }

    [Fact]
    public void TheSharesAreTheCommonalitiesOfTheUsableIncidents()
    {
        var table = DiseaseTableTests.Table();

        var all = IllnessSchedule.Of(table, Facts());
        var blocked = IllnessSchedule.Of(table, Facts(5, new DiseaseIncidentRead("Disease_Flu", 3) { Blocked = ["scenario"] },
            new DiseaseIncidentRead("Disease_GutWorms", 3), new DiseaseIncidentRead("Disease_OrganDecay", 3)));

        // Organ decay has no commonality in the biome; flu 100, gut worms 30.
        Assert.Equal(["Disease_Flu", "Disease_GutWorms"], all.Incidents.Select(i => i.Incident));
        Assert.Equal(100 / 130.0, all.Incidents[0].Share, 12);
        Assert.Equal(30 / 130.0, all.Incidents[1].Share, 12);
        Assert.Equal(["Disease_GutWorms"], blocked.Incidents.Select(i => i.Incident));
        Assert.Equal(1, blocked.Incidents[0].Share);
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
    public void AnAbsentFactLeavesTheScheduleUnassessedNamingIt()
    {
        var table = DiseaseTableTests.Table();

        Assert.Equal(["disease_facts_unobserved"], IllnessSchedule.Of(table, null).Findings);
        Assert.Equal(["disease_storyteller_unknown:Ghost"], IllnessSchedule.Of(table, Facts() with { Storyteller = "Ghost" }).Findings);
        Assert.Equal(["disease_biome_unknown:Moon"], IllnessSchedule.Of(table, Facts() with { Biome = "Moon" }).Findings);
        Assert.Equal(["disease_victims_unobserved:Disease_OrganDecay"],
            IllnessSchedule.Of(table, Facts(5, new DiseaseIncidentRead("Disease_Flu", 3), new DiseaseIncidentRead("Disease_GutWorms", 3))).Findings);
        Assert.False(IllnessSchedule.Of(new DiseaseTable(), Facts()).Assessed);
    }

    // ----- the production way: the catalog's tables and the wire facts as native fills them, through the census threat section

    [Fact]
    public void TheCatalogAndTheNativeReadGiveAnAssessedIllnessThroughTheReviewInputs()
    {
        var catalog = DiseaseTableTests.Catalog(DiseaseTableTests.CoreDefs());
        var census = ColonyFactsCoreTests.Snapshot() with { Threat = new ThreatSection { Observed = ColonyFactsCoreTests.QuietThreat with { Storyteller = "Cassandra", DaysPassed = 20, Disease = Facts(20) } } };

        var inputs = HarmInputs.Of(new RoundsFacts { DiseaseTable = catalog.DiseaseTable(), Disease = census.Threat.Observed.Disease });
        var unread = HarmInputs.Of(new RoundsFacts
        {
            DiseaseTable = catalog.DiseaseTable(),
            Disease = ColonyFactsCoreTests.Snapshot().Threat.Observed.Disease,
        });

        Assert.True(inputs.Illness.Assessed);
        Assert.Equal(1 / 60.0, inputs.Illness.IncidentsPerDay(30), 12);
        var flu = IllnessForecast.Of(inputs.Illness, Pawn(1, new DiseaseContract("Disease_Flu", 1), new DiseaseContract("Disease_GutWorms", 1),
            new DiseaseContract("Disease_OrganDecay", 1)), Tables, HarmConsequences.CareOf(Care()), 1, new ForecastValuePolicy(), Horizon);
        Assert.True(flu.Known);
        Assert.True(flu.PatientDays > 0);
        Assert.True(flu.Treatments > 0);
        Assert.True(flu.DeathProbability is > 0 and < 1);

        Assert.False(unread.Illness.Assessed);
        Assert.Equal(["disease_facts_unobserved"], unread.Illness.Findings);
    }
}
