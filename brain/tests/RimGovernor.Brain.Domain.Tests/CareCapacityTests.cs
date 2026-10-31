using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 4.2 of redefine-habitat-requirements: care capacity is compared, not gated. A patient's alternatives (reuse a Ready
/// medical bed, convert its own bed, convert another free bed, be tended where it lies, defer for new capacity) are priced by
/// their care effects; a conversion that costs another sleeper its slot is excluded; the patient's converted bed holds it once;
/// a medical bed nobody is cared in is restored or kept each review; a refused order is a fact the next review reads; and the
/// planned medical beds are layout alternatives the shared comparator decides.
/// </summary>
public sealed class CareCapacityTests
{
    private const string Patient = "p";
    private static readonly PawnId P = new(Patient);
    private static readonly PawnId Q = new("q");
    private static readonly PawnId Partner = new("partner");

    private static SleepingPerson Person(PawnId id, string bed = "", bool seeks = false)
    {
        var pawn = SleepingNativeFixtures.Pawn(id, ownedBed: bed, comfortableMin: 10, comfortableMax: 30);
        return new SleepingPerson { Native = SleepingNativeFixtures.Person(id), Pawn = pawn with { Health = pawn.Health with { ShouldSeekMedicalRest = seeks } } };
    }

    private static SleepingBed Bed(string id, EquatableArray<PawnId> owners = default, bool medical = false, int slots = 1, Cell cell = default) => new()
    {
        Native = SleepingNativeFixtures.Bed(id, temperature: 21, owners: owners, accessibleTo: [P, Q, Partner], room: "r1", medical: medical, slots: slots),
        Building = SleepingNativeFixtures.Building(id, cell: cell, roofed: true),
        Humanlike = true,
        RestEffectiveness = 1,
    };

    private static SupportCareBed CareBed(string id, bool medical, double infection, int slots = 1) => new(id, new(2, 2), slots)
    {
        Medical = medical, Prisoners = false, Slaves = false, Humanlike = true, Occupants = [], UsableNow = true,
        Movement = new CareMovement.Walk(30), InfectionChanceFactor = infection, Temperature = CoupledFixtures.Comfortable, HealPerDay = 0, SurgerySuccessFactor = new CareSurgeryFactor.Read(1),
    };

    /// <summary>A doctor's tend in <paramref name="bed"/> once the patient lies there, its batch's infection check after the tend.</summary>
    private static SupportWorker Tend(string bed, double quality = .5) =>
        new("care", "doc", Patient, "TendPatient", "DoctorTend", new(2, 2), 10, 20) { CareBed = bed, Medicine = null, Treatment = CoupledFixtures.Tend(quality, new InfectionCheck.Pending(1000)) };

    /// <summary>The patient lying on the floor, seeking medical rest, with the beds the native census lists for it and a tend in each.</summary>
    private static CareCapacityRequest Request(SleepingObservation sleeping, params SupportCareBed[] beds) => new()
    {
        Support = CoupledFixtures.NoSupport with
        {
            Demands = [CoupledFixtures.Demand("care", "care", Patient, new(1, 1), 0, 0)],
            Workers = [.. beds.Select(b => Tend(b.Id))],
            Patients = [CoupledFixtures.CarePatient("care", Patient, BedOccupant.Colonist, new CarePosture.Lying(), CareConduct.Calm, beds)],
        },
        Sleeping = sleeping,
        Rooms = RoomFixtures.Census(RoomFixtures.State("r1", RoomRole.Bedroom, beds: [.. sleeping.Beds.Select(b => b.Id)])),
    };

    private static SleepingObservation Own(params SleepingBed[] more) => new()
    {
        Colonists = 1, People = [Person(P, "own", seeks: true)], Beds = [Bed("own", owners: [P]), .. more],
    };

    private static CareCapacityDecision Decision(CareCapacityPlan plan) => Assert.Single(plan.Decisions);

    private static CareCapacityOption Option(CareCapacityPlan plan, string bed) => Assert.Single(Decision(plan).Options, o => o.Bed == bed && o.Kind != CareCapacityKind.Stabilize);

    [Fact]
    public void ConvertingThePatientsOwnBedBeatsWaitingForANewBedAndHoldsOneSlot()
    {
        var sleeping = Own();
        var plan = CareCapacityRules.Plan(Request(sleeping, CareBed("own", false, .5)));

        // Tended in its own clean room before the check, its infection weight is under the deferral's (the new bed not built yet).
        Assert.Equal((CareCapacityKind.ConvertOwn, "own"), (Decision(plan).Chosen.Kind, Decision(plan).Chosen.Bed));
        Assert.Equal([new CareBedOrder("own", true, "care")], plan.Orders);

        // The converted bed is the patient's one slot while it seeks medical rest: no second sleeping slot is owed for it.
        var converted = CareCapacityRules.Converted(sleeping, "own", true);
        Assert.Equal((0, 0), SleepingRules.SlotShortfall(converted));
        Assert.Contains(P, SleepingRules.HeldInMedicalBeds(converted, converted.People, slave: false));
    }

    [Fact]
    public void ADirtyOwnBedRoomLosesToTheNewMedicalBedAndToAReadyWard()
    {
        // The own bed's room is dirty enough that a tend there weighs more than waiting for the planned medical bed.
        var dirty = CareCapacityRules.Plan(Request(Own(), CareBed("own", false, 2)));
        Assert.Equal(CareCapacityKind.Defer, Decision(dirty).Chosen.Kind);
        Assert.Empty(dirty.Orders);

        // A Ready medical bed in a clean room is reused: no order at all.
        var ward = CareCapacityRules.Plan(Request(Own(Bed("ward", medical: true)), CareBed("own", false, 2), CareBed("ward", true, .5)));
        Assert.Equal((CareCapacityKind.Reuse, "ward"), (Decision(ward).Chosen.Kind, Decision(ward).Chosen.Bed));
        Assert.Empty(ward.Orders);
    }

    [Fact]
    public void AnotherOccupantsBedIsConvertedOnlyWhenTheyKeepASlot()
    {
        // The patient's own bed lies beyond the census's nearest beds: its conversion names the unread fact.
        var sleeping = Own(Bed("qbed", owners: [Q])) with { People = [Person(P, "own", seeks: true), Person(Q, "qbed")] };
        var excluded = CareCapacityRules.Plan(Request(sleeping, CareBed("qbed", false, .5)));
        Assert.Equal(new CareAssessment.Excluded(CareCapacityRules.DisplacesSleeper), Option(excluded, "qbed").Assessment);
        Assert.Equal(new CareAssessment.Unassessed("care_bed:own"), Option(excluded, "own").Assessment);
        Assert.Equal(CareCapacityKind.Defer, Decision(excluded).Chosen.Kind);
        Assert.Empty(excluded.Orders);

        // A free bed is the evicted owner's alternative: the conversion costs nobody a slot.
        var spare = sleeping with { Beds = [.. sleeping.Beds, Bed("spare")] };
        var converted = CareCapacityRules.Plan(Request(spare, CareBed("qbed", false, .5)));
        Assert.Equal((CareCapacityKind.ConvertOther, "qbed"), (Decision(converted).Chosen.Kind, Decision(converted).Chosen.Bed));
        Assert.Equal([new CareBedOrder("qbed", true, "care")], converted.Orders);

        // A bedless patient still never takes an owner's bed from it, though the census's total would not grow.
        var bedless = new SleepingObservation { Colonists = 2, People = [Person(P, seeks: true), Person(Q, "qbed")], Beds = [Bed("qbed", owners: [Q])] };
        Assert.Equal(new CareAssessment.Excluded(CareCapacityRules.DisplacesSleeper), Option(CareCapacityRules.Plan(Request(bedless, CareBed("qbed", false, .5))), "qbed").Assessment);
    }

    [Fact]
    public void APartnerSharingThePatientsBedIsProtected()
    {
        var shared = new SleepingObservation
        {
            Colonists = 2, People = [Person(P, "dbl", seeks: true), Person(Partner, "dbl")], Beds = [Bed("dbl", owners: [P, Partner], slots: 2)],
        };
        var plan = CareCapacityRules.Plan(Request(shared, CareBed("dbl", false, .5, slots: 2)));
        Assert.Equal(new CareAssessment.Excluded(CareCapacityRules.DisplacesSleeper), Option(plan, "dbl").Assessment);
        Assert.Empty(plan.Orders);

        // With a free bed for the partner the double bed is converted; the patient is held by it once and the partner sleeps.
        var spare = shared with { Beds = [.. shared.Beds, Bed("spare")] };
        var converted = CareCapacityRules.Plan(Request(spare, CareBed("dbl", false, .5, slots: 2)));
        Assert.Equal([new CareBedOrder("dbl", true, "care")], converted.Orders);
        Assert.Equal((0, 0), SleepingRules.SlotShortfall(CareCapacityRules.Converted(spare, "dbl", true)));
    }

    [Fact]
    public void AfterCareTheBedIsRestoredWhenItsSleeperNeedsItAndKeptOtherwise()
    {
        // Care ended: the converted bed stands medical with its owner evicted (Building_Bed.Medical's setter) and nobody in it.
        var after = new SleepingObservation { Colonists = 1, People = [Person(P)], Beds = [Bed("own", medical: true)] };
        var request = Request(after) with { Support = CoupledFixtures.NoSupport };
        Assert.Equal([new CareBedOrder("own", false, CareCapacityRules.RestoreReason)], CareCapacityRules.Plan(request).Orders);

        // Still seeking medical rest, the patient is held by it: kept.
        Assert.Empty(CareCapacityRules.Plan(request with { Sleeping = after with { People = [Person(P, seeks: true)] } }).Orders);
        // Restoring it would drop the Hospital goal's floor: kept.
        Assert.Empty(CareCapacityRules.Plan(request with { MinBeds = 1 }).Orders);
        // A planned medical bed stays medical.
        var planned = after with { Beds = [Bed("own", medical: true, cell: new(7, 7))] };
        Assert.Empty(CareCapacityRules.Plan(request with { Sleeping = planned, PlannedMedical = new HashSet<Cell> { new(7, 7) } }).Orders);
        // A refused restore is not issued again in this world.
        Assert.Empty(CareCapacityRules.Plan(request with { Refused = [new("own", false, "forbidden")] }).Orders);
    }

    [Fact]
    public void ARefusedConversionIsAFactTheNextReviewChoosesWithout()
    {
        var plan = CareCapacityRules.Plan(Request(Own(), CareBed("own", false, .5)) with { Refused = [new("own", true, "bed_forbidden")] });
        Assert.Equal(new CareAssessment.Excluded("refused:bed_forbidden"), Option(plan, "own").Assessment);
        Assert.Equal(CareCapacityKind.Defer, Decision(plan).Chosen.Kind);
        Assert.Empty(plan.Orders);
    }

    [Fact]
    public void ABedTheSleepingCensusDoesNotDescribeIsUnassessedByName()
    {
        var plan = CareCapacityRules.Plan(Request(Own(), CareBed("unknown", false, .5)));
        Assert.Equal(new CareAssessment.Unassessed("sleeping_bed:unknown"), Option(plan, "unknown").Assessment);
        Assert.Equal(new CareAssessment.Unassessed("care_bed:own"), Option(plan, "own").Assessment);
    }

    [Fact]
    public void TheGoalFloorConvertsAPlannedMedicalBedFirst()
    {
        var sleeping = new SleepingObservation { Beds = [Bed("a"), Bed("b", cell: new(5, 5))] };
        var request = Request(sleeping) with { Support = CoupledFixtures.NoSupport, MinBeds = 1, PlannedMedical = new HashSet<Cell> { new(5, 5) } };
        Assert.Equal([new CareBedOrder("b", true, CareCapacityRules.FloorReason)], CareCapacityRules.Plan(request).Orders);
    }

    private static ForecastConsequenceVector Vector(double investment)
    {
        var ledger = new ForecastValueResult(new LedgerTerm.Priced(investment), [])
        {
            Hunger = new LedgerTerm.Priced(0), Rest = new LedgerTerm.Priced(0), FoodPoisoning = new LedgerTerm.Priced(0), Thermal = new LedgerTerm.Priced(0),
            MoodBreaks = new LedgerTerm.Priced(0), Wellbeing = new LedgerTerm.Priced(0), Harm = new LedgerTerm.Priced(0), Investment = investment,
        };
        var value = new ForecastValueExplanation(ledger, new()) { UrgentZero = new(true, 0), BreakRisk = new(true, 0), Death = new(true, 0) };
        return new(EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 5).ToArray()), EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 6).ToArray()),
            new ForecastTerm.Assessed(0d), new ForecastTerm.Assessed(0d), 0, []) { Value = value };
    }

    [Fact]
    public async Task NewMedicalBedsAreLayoutAlternativesTheSharedComparatorDecides()
    {
        var prepared = LayoutDeriveRules.FirstPlanFacts(FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild").Survey, FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild"), []);
        var sick = CareFixtures.Patient("c1") with { Observed = CareFixtures.NativePatient("c1") with { Health = PawnFixtures.Healthy with { ShouldSeekMedicalRest = true } } };
        var facts = prepared with { Patients = [sick] };
        var baseline = LayoutHospitalBedRules.Baseline(facts)!;
        Assert.Equal(1, baseline.Count);
        Assert.NotEqual("", baseline.Def);

        // The patient cared in a converted bed instead: the deviation plans only the goal's floor, no duplicate bed.
        var inputs = LayoutStationPlanRules.HostedRegroupInputsOf(facts);
        inputs = inputs with { Alternatives = [.. inputs.Alternatives.Select(h => h with { Input = h.Input with { Price = new() { Work = new(8000, LayoutRegroupRules.CellWalkDays(60)) } } })] };
        var none = baseline with { Count = 0 };
        var floor = Assert.Single(inputs.Alternatives, a => a.HospitalBeds == none);
        Assert.Equal(LayoutHospitalBedRules.IdOf(none), floor.Host.Id);
        Assert.All(LayoutHospitalBedRules.DeviationsOf(facts).Deviations, c => Assert.Single(inputs.Alternatives, a => a.HospitalBeds == c));

        // Each planned medical bed costs 10 to build and nothing else differs: the floor-only alternative is applied.
        static int Medical(Regrouping layout) => layout.Clustering.Groups.Sum(g => g.Stations.Where(s => s.Purpose == StationPurpose.SleepMed).Sum(s => s.Count));
        var result = await LayoutRegroupRules.RegroupAsync(inputs, (candidates, _) => Task.FromResult<IReadOnlyList<RegroupValuation>>([.. candidates.Select(candidate =>
            new RegroupValuation.Evaluated(candidate.Id, Vector(Medical(candidate.Layout) * 10), []))]), TestContext.Current.CancellationToken);
        var decision = Assert.Single(result.Decisions, d => d.Kind == LayoutHospitalBedRules.HospitalBedMove && d.B == LayoutHospitalBedRules.NameOf(none));
        Assert.Equal(LayoutHospitalBedRules.MedicalBeds, decision.A);
        Assert.Equal(LayoutRegroupRules.RegroupApplied, decision.Verdict);
        Assert.Equal(0, Medical(result));
    }
}
