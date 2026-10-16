using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The medical family (#3258): one critical Need per patient (SeriousHarm, the immediate pass's), care, reserves and
/// surgery as plain thresholds, the care pairing shared by every patient's Need, and care orders admitted under the
/// patient's Need side by side, held only by their claims.
/// </summary>
public sealed class MedicalFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly PawnId P1 = new("p1");
    private static readonly PawnId P2 = new("p2");

    private static IReadOnlyList<Need> Derive(MedicalFacts facts, bool critical = false, bool care = false, bool reserves = false, bool surgery = false) =>
        new MedicalFamily(critical, care, reserves, surgery).Derive(facts);

    [Fact]
    public void ACriticalNeedStandsPerPatientAndAnUnreadCensusIsOneUnknownNeed()
    {
        var unknown = Assert.Single(Derive(new MedicalFacts(), critical: true));
        Assert.Equal((CriticalCensusNeed, NeedTier.SeriousHarm, Satisfaction.Unknown), (unknown.Id, unknown.Tier, unknown.Satisfaction));

        var patients = Derive(new MedicalFacts { Critical = Known(EquatableArray.From([P1, P2])) }, critical: true);
        Assert.Equal([CriticalNeed(P1), CriticalNeed(P2)], patients.Select(n => n.Id));
        Assert.All(patients, n => Assert.Equal((NeedTier.SeriousHarm, Satisfaction.Unmet), (n.Tier, n.Satisfaction)));
        Assert.True(IsCriticalNeed(patients[0].Id, out var who) && who == P1);
        Assert.False(IsCriticalNeed(MedicalCareNeed, out _));

        // Met: nobody is a patient, so no critical Need stands; the census row reads it as met.
        Assert.Empty(Derive(new MedicalFacts { Critical = Known(EquatableArray.From(Array.Empty<PawnId>())) }, critical: true));
        Assert.Equal(Satisfaction.Met, CriticalCensusNeedOf(Known(0L)).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, CriticalCensusNeedOf(Known(2L)).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, CriticalCensusNeedOf(Unknown<long>()).Satisfaction);
    }

    [Theory]
    [InlineData(true, Satisfaction.Met)]
    [InlineData(false, Satisfaction.Unmet)]
    [InlineData(null, Satisfaction.Unknown)]
    public void CareReservesAndSurgeryArePlainThresholds(bool? value, Satisfaction expected)
    {
        var fact = value is { } v ? Known(v) : Unknown<bool>();
        var facts = new MedicalFacts { CareRecovered = fact, ReservesCovered = fact, SurgeryRecovered = fact, MedicineStock = Known(2L), MedicineTarget = Known(8L) };
        var needs = Derive(facts, care: true, reserves: true, surgery: true);
        Assert.Equal([MedicalCareNeed, MedicalReservesNeed, SurgeryNeed], needs.Select(n => n.Id));
        Assert.All(needs, n => Assert.Equal((NeedTier.Wellbeing, expected), (n.Tier, n.Satisfaction)));
        var reserves = needs[1];
        Assert.Equal(expected switch { Satisfaction.Met => Known(0.0), Satisfaction.Unmet => Known(0.75), _ => Unknown<double>() }, reserves.Deficit);
        Assert.Empty(new MedicalFamily(true, true, true, true).Propose(needs, facts));
    }

    [Fact]
    public void TheFamilyIsOneScheduleEntryWhoseImmediateProtectionIsTheCareOrders()
    {
        Assert.Equal([ActionKind.Tend, ActionKind.Rescue], MedicalRounds.Schedule.ProtectionKinds);
        Assert.Contains(ActionKind.Surgery, MedicalRounds.Schedule.Kinds);
        Assert.Contains(ActionKind.BedUse, MedicalRounds.Schedule.Kinds);
        Assert.Equal([FactFamily.Emergency, FactFamily.Pawns], new MedicalFamily(true, false, false, false).SeriousHarmReads);
        Assert.Empty(new MedicalFamily(false, true, true, true).SeriousHarmReads);
    }

    private static TendDoctorFacts Doctor(string id, int skill) => new()
    {
        Pawn = new(id), Dead = Known(false), Downed = Known(false), Drafted = Known(false), MentalState = Known(false), PlayerForced = Known(false),
        QueuedJobs = Known(0u), ExistingJobDef = Known(""), MedicineSkill = Known(skill), MedicineSkillDisabled = Known(false),
        DoctorWorkEnabled = Known(true), DoctorWorkOverrideDisabled = Known(false), ControlEligible = Known(true), TendCapacities = Known(true),
    };

    private static TendPatientFacts Patient(PawnId id, double bleedHours = double.NaN) => new()
    {
        Pawn = id, Dead = Known(false), Downed = Known(true), InBed = Known(false), NeedsTend = Known(true), NoCare = Known(false),
        Bleeding = Known(!double.IsNaN(bleedHours)), LifeThreatening = Known(false),
        HoursUntilDeathFromBloodLoss = double.IsNaN(bleedHours) ? Unknown<double>() : Known(bleedHours), ExistingJobDef = Known(""),
    };

    private static TendReachability Everyone(params string[] doctors) =>
        TendReachability.Observe(doctors.ToDictionary(d => new PawnId(d), _ => (IReadOnlyList<PawnId>)[P1, P2]));

    [Fact]
    public void EveryPatientsNeedReadsTheSamePairingSoTwoPatientsNeverPickOneDoctor()
    {
        // p2 bleeds out first, so it gets the better doctor; p1 the next.
        var care = new CriticalCare { Doctors = [Doctor("d1", 10), Doctor("d2", 5)], TendPatients = [Patient(P1), Patient(P2, 4)], Reach = Everyone("d1", "d2") };
        Assert.Equal((new PawnId("d1"), true), TendDoctorFor(care, P2));
        Assert.Equal((new PawnId("d2"), true), TendDoctorFor(care, P1));

        // An open tend holds its doctor and patient: the rest pair among themselves.
        var busy = care with { Busy = [new("d1"), P2] };
        Assert.Equal((new PawnId("d2"), true), TendDoctorFor(busy, P1));
        Assert.False(TendDoctorFor(busy, P2).Ok);

        // One doctor, two patients: only the more urgent one is paired.
        var one = care with { Doctors = [Doctor("d1", 10)] };
        Assert.True(TendDoctorFor(one, P2).Ok);
        Assert.False(TendDoctorFor(one, P1).Ok);
    }

    [Fact]
    public void RescuersPairOnePatientEachAndSkipTheBusy()
    {
        static RescuerFacts Rescuer(string id) => new()
        {
            Pawn = new(id), Dead = Known(false), Downed = Known(false), Drafted = Known(false), MentalState = Known(false),
            PlayerForced = Known(false), QueuedJobs = Known(0u), ExistingJobDef = Known(""),
        };
        static RescuePatientFacts Downed(PawnId id) => new() { Pawn = id, Dead = Known(false), Downed = Known(true), InBed = Known(false), ExistingJobDef = Known("") };
        var care = new CriticalCare { Rescuers = [Rescuer("r1"), Rescuer("r2")], RescuePatients = [Downed(P1), Downed(P2)] };
        Assert.Equal((new PawnId("r1"), true), RescuerFor(care, P1));
        Assert.Equal((new PawnId("r2"), true), RescuerFor(care, P2));
        Assert.Equal((new PawnId("r2"), true), RescuerFor(care with { Busy = [new("r1")] }, P1));
        Assert.False(RescuerFor(care with { Busy = [new("r1"), new("r2")] }, P1).Ok);
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<MedicalFacts> Runner { get; init; }
        public MedicalFacts Facts { get; set; } = new();

        /// <summary>The care orders each patient's Need commits on the next run: (MethodId, action).</summary>
        public Dictionary<PawnId, (string Method, DomainAction Action)> Orders { get; } = [];

        /// <summary>The Needs the work hook was called for, in order.</summary>
        public List<NeedId> Worked { get; } = [];

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run(bool immediate = false) =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World, Immediate = immediate }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    private static PlanSpec Plan(DomainAction action) => new(PlanIds.MintPlanId(), new(1), [action]);

    private static DomainAction Tend(string doctor, PawnId patient) => DomainAction.NewTendAction(new("tend-" + doctor + "-" + patient.Value), new Tend(new(doctor), patient));

    private static DomainAction Rescue(string rescuer, PawnId patient) => DomainAction.NewRescueAction(new("rescue-" + rescuer + "-" + patient.Value), new Rescue(new(rescuer), patient));

    private static Colony Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-medical-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("d1", "d2", "r1") }, ct);
        Colony? colony = null;
        var runner = new FamilyRunner<MedicalFacts>(new MedicalFamily(true, true, true, true), (_, _) => Task.FromResult(colony!.Facts),
            MedicalRounds.Schedule,
            (work, _, call) =>
            {
                colony!.Worked.Add(work.Need.Id);
                if (IsCriticalNeed(work.Need.Id, out var patient) && colony.Orders.TryGetValue(patient, out var order))
                {
                    return Task.FromResult(RoundsBuildingPlanner.OwnerCommitVerdict(journal.CommitOwnerWork(work.Owner, new MethodId(order.Method), "", Plan(order.Action), call)));
                }
                return Task.FromResult(Verdict.AwaitingPlan(work.Need.Id.Value, "no_method"));
            });
        colony = new Colony { Journal = journal, Runner = runner };
        return colony;
    }

    [Fact]
    public void TwoPatientsTendsAreAdmittedSideBySideUnderTheirOwnNeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = new MedicalFacts { Critical = Known(EquatableArray.From([P1, P2])) };
        colony.Orders[P1] = ("tend-p1", Tend("d1", P1));
        colony.Orders[P2] = ("tend-p2", Tend("d2", P2));
        var run = colony.Run(immediate: true);
        Assert.Equal(2, run.Methods.Count);
        Assert.All(run.Methods, m => Assert.Equal(ClaimOutcome.Admitted, m.Outcome));
        Assert.Equal([CriticalNeed(P1), CriticalNeed(P2)], run.Methods.Select(m => m.Need).Order());
        Assert.Equal(2, run.SeriousHarmPlans.Count);

        // The immediate pass judges only the critical Needs; the Wellbeing Needs wait for the ordinary pass.
        Assert.Equal([CriticalNeed(P1), CriticalNeed(P2)], colony.Worked.Order());
        Assert.False(run.Complete);

        // The same MethodId again continues the open tend; nothing new is written.
        var again = colony.Run(immediate: true);
        Assert.All(again.Methods, m => Assert.Equal(ClaimOutcome.Continued, m.Outcome));
        Assert.Single(colony.Journal.LoadNeed(CriticalNeed(P1), World, ct).Methods);
    }

    [Fact]
    public void ACareOrderOnAPawnAnotherOrderHoldsIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = new MedicalFacts { Critical = Known(EquatableArray.From([P1, P2])) };
        colony.Orders[P1] = ("tend-p1", Tend("d1", P1));
        // p2's rescue would take d1, whom p1's tend already holds.
        colony.Orders[P2] = ("rescue-p2", Rescue("d1", P2));
        var run = colony.Run(immediate: true);
        Assert.Equal(ClaimOutcome.Admitted, Assert.Single(run.Methods, m => m.Need == CriticalNeed(P1)).Outcome);
        var refused = Assert.Single(run.Reports, r => r.Need == CriticalNeed(P2)).Verdict;
        Assert.True(refused.Is(Cause.Claim), refused.ToString());
        Assert.Empty(colony.Journal.LoadNeed(CriticalNeed(P2), World, ct).Methods);
    }

    [Fact]
    public void TheOrdinaryPassWorksEveryUnmetMedicalNeed()
    {
        using var colony = Open();
        colony.Facts = new MedicalFacts
        {
            Critical = Known(EquatableArray.From([P1])), CareRecovered = Known(false), ReservesCovered = Known(false), SurgeryRecovered = Known(true),
        };
        var run = colony.Run();
        Assert.True(run.Complete);
        Assert.Equal([CriticalNeed(P1), MedicalCareNeed, MedicalReservesNeed], colony.Worked);
    }

    [Fact]
    public void TheForecastMedicalRowsAreTheFamilysNeeds()
    {
        var facts = new RoundsFacts { Tick = new(100), Colonists = Known(2L), Fuel = Known<EquatableArray<FuelConsumer>>([]), CriticalPatients = Known(1L) };
        var review = ReviewEssentialServices(facts, DefaultRoundsPolicy(), new() { Work = new() { Tick = facts.Tick } }, []);
        var medical = review.Coverage.Where(c => c.Service == EssentialService.Medical).ToArray();
        Assert.Contains(medical, c => c.Target.Owner == CriticalCensusNeed.FilingKey && c.Coverage == ServiceCoverage.Shortfall);
        Assert.Contains(medical, c => c.Target.Owner == SurgeryNeed.FilingKey);
        Assert.Contains(medical, c => c.Target.Owner == MedicalReservesNeed.FilingKey);
        var calm = ReviewEssentialServices(facts with { CriticalPatients = Known(0L) }, DefaultRoundsPolicy(), new() { Work = new() { Tick = facts.Tick } }, []);
        Assert.Contains(calm.Coverage, c => c.Target.Owner == CriticalCensusNeed.FilingKey && c.Coverage == ServiceCoverage.Adequate);
    }

    [Fact]
    public void ACriticalNeedReadsAsMedicalInOperationIntents()
    {
        Assert.Equal("Medical", OperationIntent.ConcernLabel(CriticalNeed(P1).FilingKey));
        Assert.Equal("Medicine", OperationIntent.ConcernLabel(MedicalReservesNeed.FilingKey));
    }
}
