using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 4.4 of redefine-habitat-requirements: field care. A medical sleeping spot placed where a downed patient lies, a
/// stabilizing self-tend before a later walk to the ward, and an immediate transport are compared by their real delay (a tend that
/// ends after the patient bleeds out is excluded) and exposure (hypothermia or heatstroke where it lies over a common horizon). The
/// spot has only its own stats. Its chain binds in steps: placed, made medical by the review that lists it, the patient carried
/// there; a refused placement excludes it in that world. A stabilizing tend moves nobody; the transfer is the later review's
/// choice once the census reads the patient recovering.
/// </summary>
public sealed class CareFieldTests
{
    private static readonly PawnId P = new("p");
    private static readonly Cell SpotCell = new(3, 3);
    private const string Site = "field_spot:3,3";

    private static readonly PawnId Q = new("q");

    private static SleepingBed Bed(string id, bool medical = false, Cell cell = default, EquatableArray<PawnId> owners = default) => new()
    {
        Native = SleepingNativeFixtures.Bed(id, temperature: 21, owners: owners, accessibleTo: [P, Q], room: "r1", medical: medical),
        Building = SleepingNativeFixtures.Building(id, cell: cell, roofed: true),
        Humanlike = true,
        RestEffectiveness = 1,
    };

    private static SupportCareBed CareBed(string id, bool medical, CareMovement movement, double infection = .5, double heal = 0, Cell? cell = null, CareTemperature? temperature = null) =>
        new(id, cell ?? new(9, 9), 1)
        {
            Medical = medical, Prisoners = false, Slaves = false, Humanlike = true, Occupants = [], UsableNow = true,
            Movement = movement, InfectionChanceFactor = infection, Temperature = temperature ?? CoupledFixtures.Comfortable, HealPerDay = heal, SurgerySuccessFactor = new CareSurgeryFactor.Read(1),
        };

    /// <summary>A tend given in <paramref name="bed"/> ("" where the patient lies), its batch's infection check well after it.</summary>
    private static SupportWorker Tend(string bed, double quality, string doctor = "doc", double approach = 10, double work = 20) =>
        new("care", doctor, "p", "TendPatient", "DoctorTend", new(2, 2), approach, work) { CareBed = bed, Medicine = null, Treatment = CoupledFixtures.Tend(quality, new InfectionCheck.Pending(50000)) };

    private static CareFieldSpot.Site Spot(CareMovement? movement = null, CareTemperature? temperature = null) =>
        new(Site, SpotCell, movement ?? new CareMovement.Carried("r", 40), 1, temperature ?? CoupledFixtures.Comfortable, 0);

    private static SupportCarePatient Downed(CareBleeding bleeding, CareFieldSpot spot, params SupportCareBed[] beds) =>
        CoupledFixtures.CarePatient("care", "p", BedOccupant.Colonist, new CarePosture.Lying(), CareConduct.Calm, beds) with { Bleeding = bleeding, FieldSpot = spot };

    private static CareCapacityRequest Request(SupportCarePatient patient, SleepingObservation sleeping, params SupportWorker[] workers) => new()
    {
        Support = CoupledFixtures.NoSupport with
        {
            Demands = patient.Tendable ? [CoupledFixtures.Demand("care", "care", "p", new(1, 1), 0, 0)] : [],
            Workers = [.. workers],
            Patients = [patient],
        },
        Sleeping = sleeping,
        Rooms = RoomFixtures.Census(RoomFixtures.State("r1", RoomRole.Hospital, beds: [.. sleeping.Beds.Select(b => b.Id)])),
    };

    private static SleepingObservation Ward(params SleepingBed[] more) => new() { Colonists = 1, Beds = [Bed("ward", medical: true), .. more] };

    private static CareCapacityDecision Decision(CareCapacityPlan plan) => Assert.Single(plan.Decisions);

    private static CareCapacityOption Option(CareCapacityPlan plan, CareCapacityKind kind, string bed) =>
        Assert.Single(Decision(plan).Options, o => o.Kind == kind && o.Bed == bed);

    private static CareEffect Effect(CareCapacityPlan plan, CareCapacityKind kind, string bed) =>
        Assert.IsType<CareAssessment.Assessed>(Option(plan, kind, bed).Assessment).Effect;

    [Fact]
    public void AFieldSpotBeatsACarryToTheWardThatTheBleedingDoesNotSurvive()
    {
        // The ward's tend is better, but it ends 2020 ticks out and the patient bleeds out at 1000: the carry is excluded, named.
        var ward = CareBed("ward", true, new CareMovement.Carried("r", 2000));
        var bleeding = Request(Downed(new CareBleeding.Bleeding(1, 1000), Spot(), ward), Ward(), Tend("ward", .9), Tend(Site, .5));
        var plan = CareCapacityRules.Plan(bleeding);
        Assert.Equal(new CareAssessment.Excluded(CareCapacityRules.BleedsOut), Option(plan, CareCapacityKind.Reuse, "ward").Assessment);
        // A humanlike lying out of bed is tended only in a bed (WorkGiver_Tend.GoodLayingStatusForTend): no tend where it lies.
        Assert.Equal(new CareAssessment.Excluded(CareCapacityRules.NoTend), Option(plan, CareCapacityKind.InPlace, "").Assessment);
        Assert.Equal((CareCapacityKind.FieldSpot, Site), (Decision(plan).Chosen.Kind, Decision(plan).Chosen.Bed));
        Assert.Equal([new CareFieldPlacement("p", SpotCell)], plan.Placements);
        Assert.Empty(plan.Orders);
        Assert.Empty(CareTransportRules.Of(plan, bleeding.Support));
        // Its effect is the spot's own: the tend quality given there and its room, the placement and the medical toggle.
        var spot = Effect(plan, CareCapacityKind.FieldSpot, Site);
        Assert.Equal((.5, 1d, 2, 60d), (spot.TendQuality, spot.RoomInfection, spot.Orders, spot.TendTicks));

        // Not bleeding, the ward's better tend wins: the spot carries no invented hospital benefit.
        var stable = CareCapacityRules.Plan(Request(Downed(new CareBleeding.Stable(), Spot(), ward), Ward(), Tend("ward", .9), Tend(Site, .5)));
        Assert.Equal((CareCapacityKind.Reuse, "ward"), (Decision(stable).Chosen.Kind, Decision(stable).Chosen.Bed));
        Assert.Empty(stable.Placements);

        // No tend reaches the patient before it bleeds out anywhere: Defer stays choosable, marked, and nothing is placed.
        var lost = CareCapacityRules.Plan(Request(Downed(new CareBleeding.Bleeding(1, 30), Spot(), ward), Ward(), Tend("ward", .9), Tend(Site, .5)));
        Assert.Equal(new CareAssessment.Excluded(CareCapacityRules.BleedsOut), Option(lost, CareCapacityKind.FieldSpot, Site).Assessment);
        Assert.Equal(CareCapacityKind.Defer, Decision(lost).Chosen.Kind);
        Assert.True(Effect(lost, CareCapacityKind.Defer, "").BleedsOut);
        Assert.Empty(lost.Placements);
    }

    [Fact]
    public void ExposureWhereThePatientLiesIsPricedOverACommonHorizon()
    {
        // The patient lies in the cold (-20 against a 16..26 comfort range); the spot is as cold, the ward warm. The tends are equal.
        var cold = new CareTemperature.Read(-20);
        var ward = CareBed("ward", true, new CareMovement.Carried("r", 2000), infection: 1);
        var patient = Downed(new CareBleeding.Stable(), Spot(temperature: cold), ward) with { Temperature = cold };
        var plan = CareCapacityRules.Plan(Request(patient, Ward(), Tend("ward", .5), Tend(Site, .5)));
        var gain = ForecastValue.ThermalGainPerTick(true, -20, 16, 26);
        Assert.True(gain > 0);
        // The horizon is the latest settling or tend end (the ward's tend, 2020) plus a day's rest; the carry is counted in the cold.
        var horizon = 2020 + CareCapacityRules.RestHorizonTicks;
        Assert.Equal(horizon * gain, Effect(plan, CareCapacityKind.FieldSpot, Site).Exposure, 9);
        Assert.Equal(2000 * gain, Effect(plan, CareCapacityKind.Reuse, "ward").Exposure, 9);
        Assert.Equal((CareCapacityKind.Reuse, "ward"), (Decision(plan).Chosen.Kind, Decision(plan).Chosen.Bed));

        // Warm everywhere, the same tends: the spot's earlier tend wins.
        var warm = CareCapacityRules.Plan(Request(Downed(new CareBleeding.Stable(), Spot(), ward), Ward(), Tend("ward", .5), Tend(Site, .5)));
        Assert.Equal(CareCapacityKind.FieldSpot, Decision(warm).Chosen.Kind);
        Assert.Equal(0, Effect(warm, CareCapacityKind.Reuse, "ward").Exposure);

        // A heatwave prices the hot side the same way.
        var hot = new CareTemperature.Read(55);
        var heat = CareCapacityRules.Plan(Request(Downed(new CareBleeding.Stable(), Spot(temperature: hot), ward) with { Temperature = hot }, Ward(), Tend("ward", .5), Tend(Site, .5)));
        Assert.True(Effect(heat, CareCapacityKind.FieldSpot, Site).Exposure > Effect(heat, CareCapacityKind.Reuse, "ward").Exposure);
        Assert.Equal(CareCapacityKind.Reuse, Decision(heat).Chosen.Kind);
    }

    [Fact]
    public void AnUnreadTemperatureLeavesItsAlternativeUnassessedNamingTheFact()
    {
        var ward = CareBed("ward", true, new CareMovement.Carried("r", 2000));
        var unread = new CareTemperature.Unread("cell_temperature:3,3");
        var plan = CareCapacityRules.Plan(Request(Downed(new CareBleeding.Stable(), Spot(temperature: unread), ward), Ward(), Tend("ward", .5), Tend(Site, .5)));
        Assert.Equal(new CareAssessment.Unassessed(CareCapacityRules.TemperatureFact + "cell_temperature:3,3"), Option(plan, CareCapacityKind.FieldSpot, Site).Assessment);
        Assert.Equal(CareCapacityKind.Reuse, Decision(plan).Chosen.Kind);

        // Where the patient lies now unread: every alternative is unassessed but Defer, which stays choosable last.
        var nowhere = CareCapacityRules.Plan(Request(Downed(new CareBleeding.Stable(), Spot(), ward) with { Temperature = unread }, Ward(), Tend("ward", .5), Tend(Site, .5)));
        Assert.IsType<CareAssessment.Unassessed>(Option(nowhere, CareCapacityKind.Reuse, "ward").Assessment);
        Assert.Equal(CareCapacityKind.Defer, Decision(nowhere).Chosen.Kind);
        Assert.Empty(nowhere.Placements);
    }

    [Fact]
    public void APlacedSpotIsMadeMedicalForItsPatientThenCarriedToAndARefusedPlacementIsExcluded()
    {
        // The spot stands, not yet medical, and the native has already assigned a bedless sleeper to it: placed for the patient,
        // it is the patient's own (the sleeper had no slot before it), so it is made medical regardless.
        var bedless = new SleepingObservation { Colonists = 2, People = [SleepingFixturesPerson("p", ""), SleepingFixturesPerson("q", "spot")], Beds = [Bed("spot", cell: SpotCell, owners: [Q])] };
        var standing = CareBed("spot", false, new CareMovement.Carried("r", 40), infection: 1, cell: SpotCell);
        var placed = Request(Downed(new CareBleeding.Bleeding(1, 1000), Spot(), standing), bedless, Tend("spot", .5))
            with { FieldSpots = [new("p", SpotCell, true)] };
        var toggled = CareCapacityRules.Plan(placed);
        Assert.Equal((CareCapacityKind.FieldSpot, "spot"), (Decision(toggled).Chosen.Kind, Decision(toggled).Chosen.Bed));
        Assert.Equal([new CareBedOrder("spot", true, "care")], toggled.Orders);
        Assert.Empty(toggled.Placements);
        Assert.Empty(CareTransportRules.Of(toggled, placed.Support));
        // The same spot placed by nobody for it is another sleeper's slot: converting it is excluded.
        var unplaced = CareCapacityRules.Plan(placed with { FieldSpots = [] });
        Assert.Equal(new CareAssessment.Excluded(CareCapacityRules.DisplacesSleeper), Option(unplaced, CareCapacityKind.ConvertOther, "spot").Assessment);

        // Once the census reads it medical, the downed patient is carried there (a rescue to the exact spot).
        var medical = placed with { Support = placed.Support with { Patients = [placed.Support.Patients[0] with { Beds = [standing with { Medical = true }] }] },
            Sleeping = bedless with { Beds = [Bed("spot", medical: true, cell: SpotCell, owners: [Q])] } };
        var moved = CareCapacityRules.Plan(medical);
        Assert.Empty(moved.Orders);
        Assert.Equal(new CareTransport[] { new CareTransport.Carried("p", "spot") }, CareTransportRules.Of(moved, medical.Support));

        // Placed but not listed by the census: its spot is not assessed, and no second spot is placed.
        var missing = CareCapacityRules.Plan(Request(Downed(new CareBleeding.Stable(), Spot(), CareBed("ward", true, new CareMovement.Carried("r", 2000))), Ward(), Tend("ward", .5), Tend(Site, .5))
            with { FieldSpots = [new("p", new(7, 7), true)] });
        Assert.Equal(new CareAssessment.Unassessed(CareCapacityRules.FieldSpotBedFact + "7,7"), Option(missing, CareCapacityKind.FieldSpot, Site).Assessment);
        Assert.Empty(missing.Placements);

        // In flight: the spot is chosen again (the step waits on the open placement).
        var flight = CareCapacityRules.Plan(Request(Downed(new CareBleeding.Bleeding(1, 1000), Spot(), CareBed("ward", true, new CareMovement.Carried("r", 2000))), Ward(), Tend("ward", .5), Tend(Site, .5))
            with { FieldSpots = [new("p", SpotCell, false)] });
        Assert.Equal([new CareFieldPlacement("p", SpotCell)], flight.Placements);

        // Refused in this world: the new spot is excluded naming the refusal, and nothing is placed.
        var refused = CareCapacityRules.Plan(Request(Downed(new CareBleeding.Bleeding(1, 1000), Spot(), CareBed("ward", true, new CareMovement.Carried("r", 2000))), Ward(), Tend("ward", .5), Tend(Site, .5))
            with { RefusedFieldSpots = [new("p", "native_refused")] });
        Assert.Equal(new CareAssessment.Excluded("refused:native_refused"), Option(refused, CareCapacityKind.FieldSpot, Site).Assessment);
        Assert.Empty(refused.Placements);
        Assert.Equal(CareCapacityKind.Defer, Decision(refused).Chosen.Kind);
    }

    [Fact]
    public void AStabilizingSelfTendMovesNobodyUntilTheCensusReadsThePatientRecovering()
    {
        // A walking colonist bleeding out before its walk to the ward ends, able to tend itself where it stands now.
        var ward = CareBed("ward", true, new CareMovement.Walk(1000), heal: 10);
        var walking = CoupledFixtures.CarePatient("care", "p", BedOccupant.Colonist, new CarePosture.Standing(), CareConduct.Calm, ward)
            with { Bleeding = new CareBleeding.Bleeding(1, 300) };
        var request = Request(walking, Ward(), Tend("ward", .8), Tend("", .35, doctor: "p", approach: 0, work: 100));
        var plan = CareCapacityRules.Plan(request);
        Assert.Equal(new CareAssessment.Excluded(CareCapacityRules.BleedsOut), Option(plan, CareCapacityKind.Reuse, "ward").Assessment);
        // Stabilized then walked: it heals in the ward bed (12 lying + its 10) where staying put heals 8 standing.
        Assert.Equal((CareCapacityKind.Stabilize, "ward"), (Decision(plan).Chosen.Kind, Decision(plan).Chosen.Bed));
        Assert.Equal(22, Effect(plan, CareCapacityKind.Stabilize, "ward").HealPerDay);
        Assert.Equal(8, Effect(plan, CareCapacityKind.InPlace, "").HealPerDay);
        // The transfer is not bound before the stabilization is observed.
        Assert.Empty(CareTransportRules.Of(plan, request.Support));
        Assert.Empty(plan.Orders);

        // The next census reads it recovering (no longer tendable, no longer bleeding): its walk to the ward is that review's choice.
        var recovering = Request(walking with { Tendable = false, Bleeding = new CareBleeding.Stable() }, Ward());
        var later = CareCapacityRules.Plan(recovering);
        Assert.Equal(new CareAssessment.Excluded(CareCapacityRules.RestNeedsBed), Option(later, CareCapacityKind.InPlace, "").Assessment);
        Assert.Equal((CareCapacityKind.Reuse, "ward"), (Decision(later).Chosen.Kind, Decision(later).Chosen.Bed));
        Assert.Equal(new CareTransport[] { new CareTransport.Walk("p", "ward") }, CareTransportRules.Of(later, recovering.Support));
    }

    [Fact]
    public void ARecoveringPatientInItsFieldSpotIsWalkedToTheWardButNoBedIsConvertedForIt()
    {
        // Up again in the spot (no longer downed, still seeking medical rest): the ward heals faster than the spot.
        var spot = CareBed("spot", true, new CareMovement.AlreadyHere(), infection: 1, cell: SpotCell);
        var ward = CareBed("ward", true, new CareMovement.Walk(300), heal: 10);
        var own = CareBed("own", false, new CareMovement.Walk(100), heal: 4);
        var patient = CoupledFixtures.CarePatient("care", "p", BedOccupant.Colonist, new CarePosture.InBed("spot"), CareConduct.Calm, spot, ward, own)
            with { Tendable = false };
        var sleeping = Ward(Bed("spot", medical: true, cell: SpotCell), Bed("own"));
        var request = Request(patient, sleeping) with { FieldSpots = [new("p", SpotCell, true)] };
        var plan = CareCapacityRules.Plan(request);
        Assert.Equal((CareCapacityKind.Reuse, "ward"), (Decision(plan).Chosen.Kind, Decision(plan).Chosen.Bed));
        Assert.Equal(new CareAssessment.Excluded(CareCapacityRules.RecoveringReadyBedsOnly), Option(plan, CareCapacityKind.ConvertOther, "own").Assessment);
        Assert.Equal(new CareTransport[] { new CareTransport.Walk("p", "ward") }, CareTransportRules.Of(plan, request.Support));
        Assert.Empty(plan.Orders);
    }

    private static SleepingPerson SleepingFixturesPerson(string id, string bed)
    {
        var pawn = SleepingNativeFixtures.Pawn(new PawnId(id), ownedBed: bed, comfortableMin: 10, comfortableMax: 30);
        return new SleepingPerson { Native = SleepingNativeFixtures.Person(new PawnId(id)), Pawn = pawn };
    }
}
