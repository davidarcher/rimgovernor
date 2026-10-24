using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3804: a served planned hospital is credited, per colonist, the wound infection its cleaner room prevents, the illness its bed's
/// immunity relieves and the sleep mood of each patient-day it hosts against the bed the colonist owns now, and once the failures of
/// the queued surgeries it hosts.
/// </summary>
public sealed class HospitalCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;
    private const string Surgery = "surgery:pawn:bill";

    private static readonly FilthModel Filth = new()
    {
        InfectionChance = [new(-5, 1.5), new(0, 1), new(1, 0.5)], InfectionChanceRoomless = 1.3,
        SurgeryCleanliness = [new(-5, 0.6), new(0, 1), new(1, 1.05)], SurgeryCleanlinessRoomless = 0.8,
    };

    private static readonly FurnitureBed Bed = new() { Def = "HospitalBed", ImmunityGainSpeedFactor = 1.25, SurgerySuccessChanceFactor = 1.1 };

    private static NeedThoughtStage Row(string role, int stage, double offset) => new(role, role, stage, offset, NeedsFixtures.Situational);

    /// <summary>A colonist in a dirty bedroom (InfectionChanceFactor 1.4, its sleep thought -2), who would gain +3 in the hospital at stage 2.</summary>
    private static NeedsForecastPawn Patient(double gain = 1) => ForecastValueTests.HarmPawn() with
    {
        OwnedBed = "bed",
        OwnedBedRoom = NeedsFixtures.BedRoom("Bedroom", 0, false) with { InfectionChanceFactor = 1.4, SurgerySuccessFactor = 0.8, ImmunityGainSpeedFactor = 1 },
        ThoughtStages = [Row("slept_in_bedroom", 0, -2), Row("slept_outside", 0, -5), Row("slept_on_ground", 0, -4),
            Row(ForecastHospital.PatientRole, 0, 0), Row(ForecastHospital.PatientRole, 1, 1), Row(ForecastHospital.PatientRole, 2, 3)],
        Disease = NeedsFixtures.Disease with { ImmunityGainSpeed = gain, Contracts = [new DiseaseContract("Disease_Flu", 1)] },
    };

    private static CapacityPenaltyTables Tables() => ForecastValueTests.HarmTables() with
    {
        WoundInfection = new()
        {
            Known = true, Hediff = "WoundInfection", LethalSeverity = 1, InitialSeverity = 0.2, Immunizable = new ImmunizableProps(1, 0.4, -0.5),
            Stages = [new PenaltyStage(0, 0, [new CapacityModifier("Consciousness", 0, null, 0.8)])],
        },
    };

    // The illness forecast's flu (IllnessForecastTests): it kills half its victims at 1x immunity, none at 1.25x.
    private static IllnessSchedule Flu(double perDay) => new()
    {
        Assessed = true, Comps = [(0, perDay)],
        Incidents = perDay > 0 ? [new IllnessIncident("Disease_Flu", 1, 1, new DiseaseCourse
        {
            Table = new() { Known = true, Hediff = "Flu", InitialSeverity = 0.2, LethalSeverity = 1, Immunizable = new ImmunizableProps(0.25, 0.2, -0.5), Stages = [new PenaltyStage(0, 0, [])] },
            Tendable = true, Tend = new TendCourse(0, 0.5, 0.75, -1),
        })] : [],
    };

    private static PlannedRoomPrediction Prediction(string role = ForecastHospital.HospitalRole, double cleanliness = 1) =>
        PlannedRoomPredictionTests.Closed("1", "WoodLog", role, 40, 2, 12, 0, false) with { Cleanliness = cleanliness };

    private static ForecastCoupledContext Context(double raidsPerDay, double illnessPerDay = 0, NeedsForecastPawn? pawn = null)
    {
        var harm = ForecastValueTests.RaidDay(raidsPerDay: raidsPerDay) with { Illness = Flu(illnessPerDay) };
        return ForecastValueTests.Context(pawn ?? Patient(), Tables(), harm, new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = ForecastValueTests.Cloth }) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Hospital },
            Closures = [new(1, "WoodLog", [new(1, 1)], [], [], Prediction())],
            LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = new() { Id = 1, Role = PlannedRole.Hospital } },
            InvestmentHorizonDays = Horizon,
            Filth = Filth,
            Tending = new ForecastTending(true, 0.6),
            HospitalBed = Bed,
        };
    }

    /// <summary>A day in the mildest break tier unshifted, and none once shifted by <paramref name="shift"/>, the hospital serving from tick <paramref name="servedAt"/>.</summary>
    private static ForecastCoupledOutcome Served(double shift, double servedAt = 0, ForecastActivityAllocation[]? allocations = null) =>
        new(new(allocations ?? [], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>
        {
            ["pawn"] = new([], default, default, new(new("pawn", "", default, default), []), [])
            {
                Mood = new([], default, default, default, default, default, [])
                {
                    PressureTicks = Known(0d), BreakRisk = Known(0d), BreakTierTicks = [Day, 0, 0],
                    ShiftedBreakTierTicks = [new(shift, [0, 0, 0])],
                },
            },
        }, [])
        { ServedRooms = [new(1, "WoodLog", servedAt)] };

    private static ForecastInvestmentContribution[] Credits(ForecastValueResult ledger) =>
        [.. ledger.InvestmentContributions.Where(r => r.Key.StartsWith("hospital/", StringComparison.Ordinal))];

    private static HarmConsequences.Care Tended(double room) => new(true, 0.6, 0, room);

    [Fact]
    public void HalfAPatientDayADayFromADirtyBedroomIsCreditedTheInfectionAndMoodTheCleanHospitalGives()
    {
        // One raid's tended days to healed in the hospital set the raids a day that give 0.5 patient-days a day.
        var probe = Context(1);
        var horizon = Horizon * Day;
        var pawn = Patient();
        var oneRaid = HarmLedger.RaidOn(probe, pawn, Tended(0.5), horizon);
        Assert.True(oneRaid.PatientDays > 0);
        var raids = 0.5 / oneRaid.PatientDays;
        var context = Context(raids);

        // Infection: the bedroom's stored factor 1.4 against the hospital's 0.5 at Cleanliness 1, the doctor's tend quality alike (Δtend 0).
        var infection = raids * (HarmLedger.RaidOn(context, pawn, Tended(1.4), horizon).Risk - HarmLedger.RaidOn(context, pawn, Tended(0.5), horizon).Risk);
        Assert.True(infection > 0);
        // Mood: +3 at the hospital's stage 2 for the -2 bedroom thought the patient-day loses.
        Assert.Equal(5, ForecastHospital.MoodDelta(pawn, Prediction()));
        Assert.Contains(0.05, PlannedRoomCredits.BreakShifts(context)["pawn"]);
        var outcome = Served(0.05);
        var mood = ForecastHousing.GainPerDay(5, outcome.Needs["pawn"].Mood, context.MoodBreaks, context.Value.WellbeingWeight, 1)!.Value;

        var ledger = ForecastValue.Of(outcome, context, Day);
        var row = Assert.Single(Credits(ledger));
        Assert.Equal((PlannedRoomCredits.Key(PlannedRole.Hospital, 1, "pawn"), "pawn", Horizon), (row.Key, row.Pawn, row.Units));
        Assert.Equal(-(infection + 0 + (0.5 * mood)) * Horizon, row.Value, 9);
        Assert.DoesNotContain("hospital_raid_surgery_unmodelled", ledger.Findings);
    }

    [Fact]
    public void NoExpectedPatientEarnsNothing()
    {
        // No raid, no disease and no queued surgery: no patient-day, so nothing to credit.
        var ledger = ForecastValue.Of(Served(0), Context(0), Day);
        Assert.Empty(Credits(ledger));
    }

    [Fact]
    public void AModdedStorytellersUnknownCompIsNamedAndTheHospitalIsCreditedTheRaidsItsVanillaCompsSend()
    {
        // #3859 the production way (#3822): a storyteller of a ThreatBig on-off cycle (1 threat per 10 days) and a mod's comp
        // class, decoded by the catalog and read through the review inputs. The schedule is the cycle's alone, the mod's comp
        // named, and the hospital is credited without throwing.
        var catalog = Game.ThreatScheduleTests.Catalog(Game.ThreatScheduleTests.Teller("Modded", Game.ThreatScheduleTests.Cycle(0, 5, 5, 1, 1),
            new Game.ThreatScheduleTests.StorytellerCompProperties_Test()));
        var schedule = HarmInputs.Of(new RoundsFacts
        {
            ThreatTable = catalog.ThreatSchedule(), Storyteller = Known("Modded"), DaysPassed = Known(30.0), RaidPoints = Known(500.0),
        }).Schedule;
        var finding = "threat_comp_unmodelled:" + nameof(Game.ThreatScheduleTests.StorytellerCompProperties_Test);
        Assert.True(schedule.Assessed);
        Assert.Equal([finding], schedule.Findings);
        var context = Context(0) with { Harm = Context(0).Harm with { Schedule = schedule } };
        Assert.Equal(0.1 * ThreatSchedule.RaidFractionAfterForced, HarmLedger.RaidsPerDay(context.Harm)!.Value, 12);

        var ledger = ForecastValue.Of(Served(0.05), context, Day);

        Assert.True(Assert.Single(Credits(ledger)).Value < 0);
        Assert.Contains(finding, ledger.Findings);
    }

    [Fact]
    public void AnUnreadTendingOrOwnedRoomThrows()
    {
        Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Served(0.05), Context(1) with { Tending = null }, Day));
        Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Served(0.05), Context(1, pawn: Patient() with { OwnedBedRoom = null }), Day));
    }

    [Fact]
    public void TheHospitalBedsImmunityIsPricedAgainstTheOwnedBedsInTheIllnessRace()
    {
        var pawn = Patient();
        var schedule = Flu(1 / 30.0);
        var policy = new ForecastValuePolicy();
        // The bed's ImmunityGainSpeedFactor multiplies the pawn's own immunity gain: 1.25x in the hospital bed, as a 1.25x pawn.
        var hospital = IllnessForecast.Of(schedule, pawn, Tables(), Tended(1), Bed.ImmunityGainSpeedFactor, policy, Horizon * Day);
        var faster = IllnessForecast.Of(schedule, Patient(gain: 1.25), Tables(), Tended(1), 1, policy, Horizon * Day);
        Assert.Equal(faster.DeathProbability, hospital.DeathProbability, 12);
        Assert.Equal(0, hospital.DeathProbability, 9);
        var owned = IllnessForecast.Of(schedule, pawn, Tables(), Tended(1), 1, policy, Horizon * Day);
        Assert.Equal(0.5, owned.DeathProbability, 9);
        Assert.DoesNotContain("illness_bed_immunity_unassessed", owned.Findings);

        static double Risk(HarmDistribution d, ForecastValuePolicy p)
        {
            var (mean, tail) = HarmLedger.Moments(d, p.HarmRiskAlpha);
            return HarmLedger.Risk(mean, tail, p.HarmRiskWeight);
        }

        var context = Context(0, illnessPerDay: 1 / 30.0);
        var illness = (1 / 30.0) * (Risk(owned, policy) - Risk(hospital, policy));
        var patientDays = (1 / 30.0) * hospital.PatientDays;
        var outcome = Served(0.05);
        var mood = ForecastHousing.GainPerDay(5, outcome.Needs["pawn"].Mood, context.MoodBreaks, context.Value.WellbeingWeight, 1)!.Value;
        var row = Assert.Single(Credits(ForecastValue.Of(outcome, context, Day)));
        Assert.Equal(-(illness + (patientDays * mood)) * Horizon, row.Value, 9);
    }

    private static ForecastCoupledContext SurgeryContext(params SurgeryOutcomeRead[] outcomes) => Context(0) with
    {
        Support = CoupledFixtures.NoSupport with {
            Demands = [CoupledFixtures.Demand(Surgery, "surgery", "pawn", new(1, 1), 0, default) with
            {
                SurgeryOutcomes = EquatableArray.From(outcomes), SurgeryEnvironment = true, SurgeryQualityMin = 0, SurgeryQualityMax = 0.98,
            }],
            Workers = [new(Surgery, "doctor", "pawn", "DoBill", "DoBillsMedicalHumanOperation", new(1, 1), 0d, 100d) { Medicine = null, Treatment = CoupledFixtures.Surgery(new("bill", "operation", []), quality: 0.9) }],
        },
    };

    [Fact]
    public void AQueuedSurgeryIsCreditedOnceTheDeathsTheHospitalBedPrevents()
    {
        // 0.9 x the bedroom bed's 0.8 succeeds 0.72 of the time; 0.9 x 1.1 x the clean room's 1.05 clamps at 0.98. Half the failures die.
        var context = SurgeryContext(new("success", 0, 0, 0), new("death", 0.5, 0, 0));
        var death = Horizon + context.Value.DeathTerminalValue;
        var once = (((1 - 0.72) * 0.5) - ((1 - 0.98) * 0.5)) * death;
        var row = Assert.Single(Credits(ForecastValue.Of(Served(0), context, Day)));
        Assert.Equal((PlannedRoomCredits.Key(PlannedRole.Hospital, 1, "surgery:" + Surgery), "pawn", Horizon), (row.Key, row.Pawn, row.Units));
        Assert.Equal(-once, row.Value, 9);
    }

    [Fact]
    public void AnIllnessOrganLossBooksItsReplacementSurgeriesADayAtTheRateTheyCome()
    {
        // #3830: organ decay on the lungs alone, lost on day 4.995 of the 10-day horizon; a stocked natural lung is queued on every loss, so its surgeries
        // come at (1/30) a day, each saving what the 0.72-to-0.98 success gain saves once. An unstocked one queues none.
        const string decay = "Disease_OrganDecay";
        var course = new DiseaseCourse
        {
            Table = new() { Known = true, Hediff = "OrganDecay", InitialSeverity = 0.001, LethalSeverity = 1, Stages = [new PenaltyStage(0, 0, [])] },
            PerDayMin = 0.2, PerDayMax = 0.2, MaxSeverity = 1, DestroysOrgan = true,
            Parts = [new() { Part = "Lung", Tag = "BreathingSource", Capacity = "Breathing", Lethal = true, Recipes = new Dictionary<string, double> { ["InstallNaturalLung"] = 1 } }],
        };
        var schedule = new IllnessSchedule { Assessed = true, Comps = [(0, 1 / 30.0)], Incidents = [new IllnessIncident(decay, 1, 1, course)] };
        NeedsForecastPawn Decaying(bool stocked) => Patient() with
        {
            Disease = new PawnDiseaseFacts
            {
                ImmunityGainSpeed = 1, Contracts = [new DiseaseContract(decay, 1)], BloodPumping = 1, Breathing = 1, BloodFiltration = 1,
                Parts = [new("Lung", 0.025, 1) { Incidents = [decay], Hediffs = [] }, new("Lung", 0.025, 1) { Incidents = [decay], Hediffs = [] }],
                Replacements = [new("Lung", "InstallNaturalLung")
                {
                    IngredientsOnMap = stocked, EligibleDoctors = 1, SuccessChance = 0.9, DoctorSuccessChance = 0.9,
                    Outcomes = [new("success", 0, 0, 0), new("death", 0.5, 0, 0)], Environment = true, QualityMax = 0.98, Quality = 0.9,
                }],
            },
        };
        double Credit(bool stocked)
        {
            var context = Context(0, pawn: Decaying(stocked)) with { Harm = Context(0).Harm with { Illness = schedule } };
            return Credits(ForecastValue.Of(Served(0), context, Day)).Sum(r => r.Value);
        }

        var stockedContext = Context(0);
        var once = (((1 - 0.72) * 0.5) - ((1 - 0.98) * 0.5)) * (Horizon + stockedContext.Value.DeathTerminalValue);
        Assert.Equal(-(1 / 30.0) * once * Horizon, Credit(true) - Credit(false), 9);
    }

    [Fact]
    public void ARaidsArmLossBooksItsReplacementSurgeriesADayAtTheRaidRate()
    {
        // #3847: the raid's 10-damage rifle takes the 5-health arm with the per-raid chance the hospital-tended raid gives it; a stocked
        // natural arm is queued on every loss, so a raid a day books that many surgeries a day, each saving the 0.72-to-0.98 success gain.
        // The same loss with a bill outside the hospital's environment books none, its harm alike.
        NeedsForecastPawn Armed(bool environment) => Patient() with { PartLoss = RaidPartLossTests.ArmLoss(environment) };
        double Credit(bool environment)
        {
            // The patient-days' mood shift is the same in both: the bill's environment moves no harm.
            var context = Context(1, pawn: Armed(environment));
            return Credits(ForecastValue.Of(Served(PlannedRoomCredits.BreakShifts(context)["pawn"].Single()), context, Day)).Sum(r => r.Value);
        }

        var context = Context(1, pawn: Armed(true));
        var perRaid = Assert.Single(HarmLedger.RaidOn(context, Armed(true), Tended(0.5), Horizon * Day).Replacements).Probability;
        Assert.True(perRaid > 0);
        var once = (((1 - 0.72) * 0.5) - ((1 - 0.98) * 0.5)) * (Horizon + context.Value.DeathTerminalValue);
        Assert.Equal(-perRaid * once * Horizon, Credit(true) - Credit(false), 9);
    }

    [Fact]
    public void ASurgeryFailuresWoundsArePricedThroughTheHarmModel()
    {
        var context = SurgeryContext(new("success", 0, 0, 0), new("failure", 1, 20, 6));
        var pawn = Patient();
        var horizon = Horizon * Day;
        double Cost(double room)
        {
            var wounds = HarmConsequences.Wounds(20, 6, ForecastHospital.SurgeryBleedShare, pawn, Tables(), Tended(room), context.Value, horizon);
            Assert.True(wounds.Known);
            var (mean, tail) = HarmLedger.Moments(wounds, context.Value.HarmRiskAlpha);
            return HarmLedger.Risk(mean, tail, context.Value.HarmRiskWeight);
        }

        var once = ((1 - 0.72) * Cost(1.4)) - ((1 - 0.98) * Cost(0.5));
        Assert.True(once > 0);
        var row = Assert.Single(Credits(ForecastValue.Of(Served(0), context, Day)));
        Assert.Equal(-once, row.Value, 9);
    }

    [Fact]
    public void ASurgeryTheScheduleFinishesBeforeTheHospitalServesEarnsNothing()
    {
        var context = SurgeryContext(new("success", 0, 0, 0), new("death", 0.5, 0, 0)) with
        {
            Bindings = new Dictionary<(string, string), ActivityTaskCandidate>
            {
                [("operate", "doctor")] = new("operate", "doctor", [], PawnActivityKind.Work, "pawn", "DoBillsMedicalHumanOperation", "DoBill", 1, 0, new(1, 1)) { SupportDemand = Surgery },
            },
        };
        var outcome = Served(0, servedAt: 1000, allocations: [new("operate", "doctor", 0, 0, 0, 500, 0, 0, 500, 0, 0)]);
        Assert.Empty(Credits(ForecastValue.Of(outcome, context, Day)));
    }

    [Fact]
    public void AFreshColonysProductionContextCarriesTheHospitalBedWithoutThrowing()
    {
        // The production path (#3822): RoundsRead furnishes the planned hospital with the catalog's first hospital bed (a bedroll
        // never), and GenerateCoupledActivities carries it into the context; tier 2 adds the tending (ColonyValue.TendingNow).
        var furniture = new RoomFurniture
        {
            Beds = [new() { Def = SleepingRules.SleepingBedrollDefinition, Slots = 1 }, new() { Def = "Bed", Slots = 1, ImmunityGainSpeedFactor = 1.07, SurgerySuccessChanceFactor = 1.1 }],
        };
        Assert.Equal("Bed", furniture.HospitalBed().Def);
        var facts = ForecastCoupledTests.ConstructionReview() with
        {
            PlannedShelters = [new() { Id = 2, Role = PlannedRole.Hospital, Region = RegionRules.RectRegion(new() { X = 10, Z = 0, Width = 5, Height = 5 }) }],
            HospitalBed = furniture.HospitalBed(),
        };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context;
        Assert.Equal(PlannedRole.Hospital, context.PlannedRoomRoles[2]);
        Assert.True(context.RoomCredits.ContainsKey(PlannedRole.Hospital));
        Assert.Equal(1.07, context.HospitalBed.ImmunityGainSpeedFactor);
        Assert.Empty(ForecastHospital.Shifts(context));
    }

    [Fact]
    public void AColonistWithoutABedLosesTheGroundThoughtInTheHospital()
    {
        var pawn = Patient() with { OwnedBed = "", OwnedBedRoom = null };
        Assert.Equal(3 + 4, ForecastHospital.MoodDelta(pawn, Prediction()));
        // A room the prediction does not make a Hospital gives no patient thought.
        Assert.Equal(4, ForecastHospital.MoodDelta(pawn, Prediction(role: "Bedroom")));
    }
}
