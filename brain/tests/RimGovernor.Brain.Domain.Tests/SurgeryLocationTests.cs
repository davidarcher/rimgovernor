using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 4.5 of redefine-habitat-requirements: where a queued operation is done is compared, not gated on a hospital label. Each
/// legal bed the patient may be cared in is priced at the actual chance there (the surgeon's quality before the bed, times that
/// bed's SurgerySuccessChanceFactor read with its facilities and room, clamped) with the move to it; a transfer wins only when the
/// failures it saves outweigh the move. A prisoner's alternatives are its prison-legal beds, and an unread bed factor leaves its
/// alternative unassessed, naming the fact.
/// </summary>
public sealed class SurgeryLocationTests
{
    private const string Patient = "p";
    private const string BillDemand = "surgery:p:bill";

    private static SupportCareBed Bed(string id, bool medical, double factor, CareMovement movement, bool prisoners = false, CareSurgeryFactor? read = null) =>
        new(id, new(2, 2), 1)
        {
            Medical = medical, Prisoners = prisoners, Slaves = false, Humanlike = true,
            Occupants = movement is CareMovement.AlreadyHere ? [Patient] : [], UsableNow = true, Movement = movement,
            InfectionChanceFactor = 1, Temperature = CoupledFixtures.Comfortable, HealPerDay = 0,
            SurgerySuccessFactor = read ?? new CareSurgeryFactor.Read(factor),
        };

    /// <summary>A bill the bed and room comp applies to, clamped 0 to 0.98: half its failures kill, the rest deal 20 damage.</summary>
    private static SupportDemand Bill(string nativeBed) => CoupledFixtures.Demand(BillDemand, "surgery", Patient, new(1, 1), 0, 0) with
    {
        SurgeryBed = nativeBed, SurgeryEnvironment = true, SurgeryQualityMin = 0, SurgeryQualityMax = 0.98,
        SurgeryOutcomes = [new("success", 0, 0, 0), new("death", 0.5, 0, 0), new("failure", 1, 20, 6)],
    };

    private static SupportWorker Surgeon(double quality) => new(BillDemand, "doc", Patient, "DoBill", "DoBillsMedicalHumanOperation", new(2, 2), 100, 2000)
    {
        Medicine = null, Treatment = CoupledFixtures.Surgery(new PawnActivityRecipe("bill", "InstallBionicEye", []), quality: quality),
    };

    /// <summary>A patient with nothing to tend, waiting on its operation.</summary>
    private static SupportCarePatient Waiting(BedOccupant who, CarePosture posture, params SupportCareBed[] beds) =>
        CoupledFixtures.CarePatient("care:p", Patient, who, posture, CareConduct.Calm, beds) with { Tendable = false, FieldSpot = new CareFieldSpot.None("surgery") };

    private static CareCapacityRequest Request(SupportCarePatient patient, string nativeBed, double quality = 0.97) => new()
    {
        Support = CoupledFixtures.NoSupport with { Demands = [Bill(nativeBed)], Workers = [Surgeon(quality)], Patients = [patient] },
        Rooms = RoomFixtures.Census(Array.Empty<RoomState>()),
    };

    private static CareCapacityDecision Decision(CareCapacityPlan plan) => Assert.Single(plan.Decisions);

    private static CareCapacityOption Option(CareCapacityPlan plan, CareCapacityKind kind, string bed) => Assert.Single(Decision(plan).Options, o => o.Kind == kind && o.Bed == bed);

    private static double Loss(CareCapacityOption option) => ((CareAssessment.Assessed)option.Assessment).Effect.SurgeryLoss;

    // The expected loss of one failure: half die (60 colonist-days), half lie healing 20 damage at 12 a day.
    private const double FailureDays = 0.5 * 60 + 0.5 * 20 / 12d;

    [Fact]
    public void AnOperationProceedsInTheNonMedicalBedThePatientLiesInWhenNoBedGivesABetterChance()
    {
        // No hospital and no medical label is asked for: its own bed (factor 1) rates 0.97, and the ward is no better a bed.
        var own = Bed("own", false, 1, new CareMovement.AlreadyHere());
        var ward = Bed("ward", true, 1, new CareMovement.Walk(600));
        var plan = CareCapacityRules.Plan(Request(Waiting(BedOccupant.Colonist, new CarePosture.InBed("own"), own, ward), "own"));

        Assert.Equal((CareCapacityKind.InPlace, "own"), (Decision(plan).Chosen.Kind, Decision(plan).Chosen.Bed));
        Assert.Empty(plan.Orders);
        Assert.Equal(0.03 * FailureDays, Loss(Decision(plan).Chosen), 9);
        Assert.Equal(0.03 * FailureDays + 600d / GameTime.TicksPerDay, Loss(Option(plan, CareCapacityKind.Reuse, "ward")), 9);
    }

    [Fact]
    public void AHospitalTransferWinsOnlyWhenItsChanceGapOutweighsTheMove()
    {
        // A sleeping spot's 0.7 against a hospital bed's 1.1: 0.68 against the clamp's 0.98 saves far more than a 600-tick walk.
        var spot = Bed("spot", false, 0.7, new CareMovement.AlreadyHere());
        var ward = Bed("ward", true, 1.1, new CareMovement.Walk(600));
        var gain = CareCapacityRules.Plan(Request(Waiting(BedOccupant.Colonist, new CarePosture.InBed("spot"), spot, ward), "spot"));
        Assert.Equal((CareCapacityKind.Reuse, "ward"), (Decision(gain).Chosen.Kind, Decision(gain).Chosen.Bed));
        Assert.Equal(0.02 * FailureDays + 0.01, Loss(Decision(gain).Chosen), 9);
        var support = CoupledFixtures.NoSupport with { Patients = [Waiting(BedOccupant.Colonist, new CarePosture.InBed("spot"), spot, ward)] };
        Assert.Equal(new CareTransport[] { new CareTransport.Walk(Patient, "ward") }, CareTransportRules.Of(gain, support));

        // A bed barely better a day's walk away: the failures it saves (0.03 x 0.001) are worth less than the walk, so it stays.
        var bed = Bed("bed", false, 1, new CareMovement.AlreadyHere());
        var far = Bed("far", true, 1.001, new CareMovement.Walk(GameTime.TicksPerDay));
        var loss = CareCapacityRules.Plan(Request(Waiting(BedOccupant.Colonist, new CarePosture.InBed("bed"), bed, far), "bed"));
        Assert.Equal((CareCapacityKind.InPlace, "bed"), (Decision(loss).Chosen.Kind, Decision(loss).Chosen.Bed));
        Assert.True(Loss(Option(loss, CareCapacityKind.Reuse, "far")) > Loss(Decision(loss).Chosen));
    }

    [Fact]
    public void APrisonersOperationIsComparedOverItsPrisonLegalBeds()
    {
        // A downed prisoner carried in: the colony's hospital bed is illegal for it (LegalBed), the prison cell's medical bed is its place.
        var ward = Bed("ward", true, 1.1, new CareMovement.Carried("doc", 600));
        var cell = Bed("cell", true, 1, new CareMovement.Carried("doc", 900), prisoners: true);
        var plan = CareCapacityRules.Plan(Request(Waiting(BedOccupant.Prisoner, new CarePosture.Lying(), ward, cell), "cell"));

        Assert.Equal(new CareAssessment.Excluded(Wire.Name(CareBedState.IllegalBed)), Option(plan, CareCapacityKind.Reuse, "ward").Assessment);
        Assert.Equal((CareCapacityKind.Reuse, "cell"), (Decision(plan).Chosen.Kind, Decision(plan).Chosen.Bed));
        // Carried: the rescuer's ticks count as well as the patient's.
        Assert.Equal(0.03 * FailureDays + 2 * 900d / GameTime.TicksPerDay, Loss(Decision(plan).Chosen), 9);
    }

    [Fact]
    public void AVitalsMonitorRaisesTheChanceThroughTheBedsStat()
    {
        // The native read is the bed's SurgerySuccessChanceFactor with the monitor's CompAffectedByFacilities offset in it.
        var request = Request(Waiting(BedOccupant.Colonist, new CarePosture.Lying()), "");
        var plain = CareCapacityRules.SurgeryLossOf(request, request.Support.Demands, Bed("b", true, 0.85, new CareMovement.AlreadyHere()));
        var monitored = CareCapacityRules.SurgeryLossOf(request, request.Support.Demands, Bed("b", true, 0.85 + 0.05, new CareMovement.AlreadyHere()));
        Assert.Equal((1 - 0.97 * 0.85) * FailureDays, plain.Loss, 9);
        Assert.Equal((1 - 0.97 * 0.90) * FailureDays, monitored.Loss, 9);

        // The planned hospital bed gains the monitor the plan places touching it, and only then.
        var monitor = new FacilityLink { Def = "VitalsMonitor", Adjacent = true, ImmunityGainSpeedOffset = 0.02, SurgerySuccessChanceOffset = 0.05 };
        FurnitureBed[] candidates = [new() { Def = "HospitalBed", Slots = 1, ImmunityGainSpeedFactor = 1.1, SurgerySuccessChanceFactor = 1.1 }];
        LayoutPlan With(int x) => PlannedRoomCreditTests.WithMedicalBed(null, 1, "HospitalBed") is var plan
            ? plan with { Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [1] = [.. plan.Pieces[1], new InteriorPiece { Slot = "monitor", Def = "VitalsMonitor", Size = new(1, 1), Rect = new RimGovernor.Brain.Policy.Rectangle { X = x, Z = 0, Width = 1, Height = 1 } }] } }
            : throw new InvalidOperationException();
        var beside = LayoutHospitalBedRules.PlannedBed(With(1), candidates, monitor);
        Assert.Equal(1.15, beside.SurgerySuccessChanceFactor, 9);
        Assert.Equal(1.12, beside.ImmunityGainSpeedFactor, 9);
        Assert.Equal(1.1, LayoutHospitalBedRules.PlannedBed(With(4), candidates, monitor).SurgerySuccessChanceFactor);
    }

    [Fact]
    public void AnUnreadBedFactorLeavesThatAlternativeUnassessedNamingTheFact()
    {
        var ward = Bed("ward", true, 0, new CareMovement.Walk(600), read: new CareSurgeryFactor.Unread("surgery_success_factor:ward"));
        var own = Bed("own", false, 1, new CareMovement.AlreadyHere());
        var plan = CareCapacityRules.Plan(Request(Waiting(BedOccupant.Colonist, new CarePosture.InBed("own"), own, ward), "own"));
        Assert.Equal(new CareAssessment.Unassessed("surgery_success_factor:ward"), Option(plan, CareCapacityKind.Reuse, "ward").Assessment);
        Assert.Equal((CareCapacityKind.InPlace, "own"), (Decision(plan).Chosen.Kind, Decision(plan).Chosen.Bed));

        // The bed the native would operate in is not among the listed beds: its alternatives are unassessed, Defer stays choosable last.
        var lost = CareCapacityRules.Plan(Request(Waiting(BedOccupant.Colonist, new CarePosture.Lying(), ward), "gone"));
        Assert.Equal(CareCapacityKind.Defer, Decision(lost).Chosen.Kind);
        Assert.Equal(double.PositiveInfinity, Loss(Decision(lost).Chosen));
        Assert.Equal(new CareAssessment.Excluded(CareCapacityRules.RestNeedsBed), Option(lost, CareCapacityKind.InPlace, "").Assessment);
    }
}
