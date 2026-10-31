using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 4.3 of redefine-habitat-requirements: a care patient reaches the bed the care capacity comparison chose through a typed
/// transport (a downed patient carried by an exact-bed Rescue, an orderable walking patient's own LayDown), and that patient's
/// transport holds the bed: no other patient, sleeper, bed order or assignment takes it until the census releases it. A walking
/// patient nobody can order is excluded naming its state; a refused transport excludes its bed at the next review; a release
/// touches only the patient that healed, died, left or stopped being the colony's responsibility.
/// </summary>
public sealed class CareTransportTests
{
    private static readonly PawnId P = new("p");
    private static readonly PawnId Q = new("q");

    private static SleepingPerson Person(PawnId id, string bed = "", bool seeks = false)
    {
        var pawn = SleepingNativeFixtures.Pawn(id, ownedBed: bed, comfortableMin: 10, comfortableMax: 30);
        return new SleepingPerson { Native = SleepingNativeFixtures.Person(id), Pawn = pawn with { Health = pawn.Health with { ShouldSeekMedicalRest = seeks } } };
    }

    private static SleepingBed Bed(string id, EquatableArray<PawnId> owners = default, bool medical = false) => new()
    {
        Native = SleepingNativeFixtures.Bed(id, temperature: 21, owners: owners, accessibleTo: [P, Q], room: "r1", medical: medical, slots: 1),
        Building = SleepingNativeFixtures.Building(id, cell: default, roofed: true),
        Humanlike = true,
        RestEffectiveness = 1,
    };

    private static SupportCareBed CareBed(string id, bool medical, double infection, CareMovement? movement = null) => new(id, new(2, 2), 1)
    {
        Medical = medical, Prisoners = false, Slaves = false, Humanlike = true, Occupants = [], UsableNow = true,
        Movement = movement ?? new CareMovement.Walk(30), InfectionChanceFactor = infection, Temperature = CoupledFixtures.Comfortable, HealPerDay = 0, SurgerySuccessFactor = new CareSurgeryFactor.Read(1),
    };

    private static SupportWorker Tend(string patient, string bed, string demand) =>
        new(demand, "doc", patient, "TendPatient", "DoctorTend", new(2, 2), 10, 20) { CareBed = bed, Medicine = null, Treatment = CoupledFixtures.Tend(.5, new InfectionCheck.Pending(1000)) };

    private static SupportCarePatient Patient(string demand, string pawn, BedOccupant who, params SupportCareBed[] beds) =>
        CoupledFixtures.CarePatient(demand, pawn, who, new CarePosture.Lying(), CareConduct.Calm, beds);

    private static SleepingObservation Census() => new()
    {
        Colonists = 2, People = [Person(P, "own", seeks: true), Person(Q, "qown", seeks: true)],
        Beds = [Bed("own", owners: [P]), Bed("qown", owners: [Q]), Bed("ward", medical: true)],
    };

    /// <summary>Patients lying on the floor, each with its own dirty bed and the clean ward the comparison reuses.</summary>
    private static CareCapacityRequest Request(params SupportCarePatient[] patients) => new()
    {
        Support = CoupledFixtures.NoSupport with
        {
            Demands = [.. patients.Select(p => CoupledFixtures.Demand(p.Demand, "care", p.Patient, new(1, 1), 0, 0))],
            Workers = [.. patients.SelectMany(p => p.Beds.Select(b => Tend(p.Patient, b.Id, p.Demand)))],
            Patients = [.. patients],
        },
        Sleeping = Census(),
        Rooms = RoomFixtures.Census(RoomFixtures.State("r1", RoomRole.Bedroom, beds: ["own", "qown", "ward"])),
    };

    private static SupportCarePatient Walking(string demand, string pawn, BedOccupant who = BedOccupant.Colonist) =>
        Patient(demand, pawn, who, CareBed(pawn == "p" ? "own" : "qown", false, 2), CareBed("ward", true, .5));

    private static CareCapacityOption Option(CareCapacityPlan plan, string demand, string bed) =>
        Assert.Single(Assert.Single(plan.Decisions, d => d.Demand == demand).Options, o => o.Bed == bed && o.Kind != CareCapacityKind.Stabilize);

    [Fact]
    public void ADownedPatientIsCarriedToItsExactBedAndAWalkingColonistLiesDownInIt()
    {
        var walk = Request(Walking("care", "p"));
        var walked = CareCapacityRules.Plan(walk);
        Assert.Equal(new CareTransport[] { new CareTransport.Walk("p", "ward") }, CareTransportRules.Of(walked, walk.Support));

        var downed = Request(Patient("care", "p", BedOccupant.Colonist, CareBed("own", false, 2), CareBed("ward", true, .5, new CareMovement.Carried("r", 40))));
        var carried = CareCapacityRules.Plan(downed);
        Assert.Equal(new CareTransport[] { new CareTransport.Carried("p", "ward") }, CareTransportRules.Of(carried, downed.Support));

        // A slave the player controls is ordered like a colonist; an in-place or deferred choice moves nobody.
        Assert.True(CareTransportRules.Orderable(BedOccupant.Slave));
        var inPlace = Request(Patient("care", "p", BedOccupant.Colonist));
        Assert.Empty(CareTransportRules.Of(CareCapacityRules.Plan(inPlace), inPlace.Support));
    }

    [Theory]
    [InlineData(BedOccupant.Guest)]
    [InlineData(BedOccupant.Prisoner)]
    [InlineData(BedOccupant.Animal)]
    public void AWalkingPatientNobodyCanOrderIsExcludedNamingItsState(BedOccupant who)
    {
        var request = Request(Walking("care", "p", who));
        var plan = CareCapacityRules.Plan(request);
        var ward = Option(plan, "care", "ward").Assessment;
        // A prisoner or animal is refused the colony ward by legality first; a guest, legal there, by the missing order.
        Assert.IsType<CareAssessment.Excluded>(ward);
        if (who == BedOccupant.Guest) Assert.Equal(new CareAssessment.Excluded(CareTransportRules.NotOrderable + ":" + Wire.Name(who)), ward);
        Assert.Empty(CareTransportRules.Of(plan, request.Support));
    }

    [Fact]
    public void AHeldBedIsNoSecondPatientsAndNoSleepersFreeSlot()
    {
        // Q's lay-down holds the ward: P is not sent there, and the census counts the ward's slot as Q's.
        var request = Request(Walking("care", "p")) with { Holds = [new("q", "ward")] };
        var plan = CareCapacityRules.Plan(request);
        Assert.Equal(new CareAssessment.Excluded(CareTransportRules.HeldByPatient), Option(plan, "care", "ward").Assessment);
        Assert.DoesNotContain(plan.Decisions, d => d.Chosen.Bed == "ward");
        Assert.DoesNotContain(plan.Orders, o => o.Bed == "ward");

        var bedless = new SleepingObservation { Colonists = 2, People = [Person(P, seeks: true), Person(Q, seeks: true)], Beds = [Bed("ward", medical: true)] };
        // Unheld, the first seeker by id takes the ward's free slot; held by Q, the slot is Q's and P is owed one.
        Assert.Equal(new[] { P }, SleepingRules.HeldInMedicalBeds(bedless, bedless.People, slave: false));
        var held = CareCapacityRules.Holding(bedless, [new("q", "ward")]);
        Assert.Equal(new[] { Q }, SleepingRules.HeldInMedicalBeds(held, held.People, slave: false));
        Assert.False(SleepingRules.CompatibleSlot(held.Beds.Single(), P, slave: false));
    }

    [Fact]
    public void ARefusedTransportExcludesItsBedAtTheNextReviewAndCreditsNothing()
    {
        var request = Request(Walking("care", "p")) with { RefusedTransports = [new("p", "ward", "native_refused")] };
        var plan = CareCapacityRules.Plan(request);
        Assert.Equal(new CareAssessment.Excluded("refused:native_refused"), Option(plan, "care", "ward").Assessment);
        Assert.DoesNotContain(CareTransportRules.Of(plan, request.Support), t => t.Bed == "ward");
        // Another patient's refusal is not P's fact.
        var other = CareCapacityRules.Plan(Request(Walking("care", "p")) with { RefusedTransports = [new("q", "ward", "native_refused")] });
        Assert.IsType<CareAssessment.Assessed>(Option(other, "care", "ward").Assessment);
    }

    [Fact]
    public void AHoldIsReleasedOnlyForThePatientTheCensusNoLongerCaresFor()
    {
        var both = Request(Walking("care-p", "p"), Walking("care-q", "q")).Support;
        IssuedCareTransport[] issued = [new("p", "ward", true), new("q", "qward", true)];
        Assert.Equal([new CareBedHold("p", "ward"), new CareBedHold("q", "qward")], CareTransportRules.Holds(issued, both));

        // Q healed, died, left the map or stopped being the colony's: only Q's bed is released.
        var onlyP = both with { Patients = [both.Patients[0]] };
        Assert.Equal([new CareBedHold("p", "ward")], CareTransportRules.Holds(issued, onlyP));

        // P lying in another bed releases P's hold; lying in the held bed keeps it.
        var elsewhere = both with { Patients = [both.Patients[0] with { Posture = new CarePosture.InBed("other") }, both.Patients[1]] };
        Assert.Equal([new CareBedHold("q", "qward")], CareTransportRules.Holds(issued, elsewhere));
        var arrived = both with { Patients = [both.Patients[0] with { Posture = new CarePosture.InBed("ward") }, both.Patients[1]] };
        Assert.Contains(new CareBedHold("p", "ward"), CareTransportRules.Holds(issued, arrived));

        // The latest attempt of each patient is read: a refused or superseded attempt holds nothing.
        Assert.Equal([new CareBedHold("q", "qward")], CareTransportRules.Holds([.. issued, new("p", "ward", false)], both));
    }

    private static WorkAction Row(DomainAction a, Stage stage = Stage.Pending) => new(new PlanId("plan"), a, new ProgressView { Action = a.Id, Stage = stage });

    private static MethodClaims Claims(params WorkAction[] rows) => MethodClaimsRules.MethodClaimsOf(new WorkAdmissionEvidence(), rows);

    [Fact]
    public void AnOpenTransportClaimsItsBedAgainstEveryOtherBedOrderAndTransport()
    {
        var rescue = Claims(Row(DomainAction.NewRescueAction(new("r-0"), new Rescue(new("rescuer"), P, "Bed_ward"))));
        var rest = Claims(Row(DomainAction.NewBedRestAction(new("b-0"), new BedRest(Q, "Bed_ward"))));
        Assert.Contains("Bed_ward", rescue.Targets);
        Assert.Contains("care/p", rescue.Targets);
        Assert.Equal("Bed_ward", MethodClaimsRules.MethodClaimOverlap(rest, rescue));
        var medical = Claims(Row(DomainAction.NewBedUseAction(new("u-0"), BedUse.NewBedMedical("Bed_ward", false))));
        Assert.Equal("Bed_ward", MethodClaimsRules.MethodClaimOverlap(medical, rest));
        // Another bed is free of it; a settled transport claims nothing (the native reservation, then the patient, holds the bed).
        Assert.Null(MethodClaimsRules.MethodClaimOverlap(Claims(Row(DomainAction.NewBedRestAction(new("c-0"), new BedRest(new("c"), "Bed_other")))), rescue));
        Assert.Empty(Claims(Row(DomainAction.NewBedRestAction(new("d-0"), new BedRest(Q, "Bed_ward")), Stage.Completed)).Targets);
    }

    [Fact]
    public void TransportPayloadsRequireTheirExactBed()
    {
        Assert.Throws<DomainException>(() => new Rescue(new("rescuer"), P, ""));
        Assert.Throws<DomainException>(() => new BedRest(P, "p"));
        Assert.Equal("Bed_ward", new BedRest(P, "Bed_ward").Bed);
    }
}
