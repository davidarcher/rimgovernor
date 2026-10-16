using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The military family (#3259): defense, pests and training are Needs with pure met, unmet and unknown predicates;
/// pest hunts are Need-owned Methods admitted by claims, side by side for different animals.
/// </summary>
public sealed class MilitaryFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static Need Derive(MilitaryFacts facts, NeedId id) => Assert.Single(new MilitaryFamily().Derive(facts), n => n.Id == id);

    [Fact]
    public void DefenseIsMetUnmetOrUnknownByTheArmedFootholdHostilesAndUnarmed()
    {
        var met = Derive(new MilitaryFacts { Hostiles = Known(0L), Armed = Known(3L), Unarmed = Known(0L), Foothold = Known(3L) }, MilitaryFamily.Defense);
        Assert.Equal((Satisfaction.Met, 0.0, NeedTier.Wellbeing), (met.Satisfaction, met.Deficit.Value, met.Tier));

        var unarmed = Derive(new MilitaryFacts { Hostiles = Known(0L), Armed = Known(3L), Unarmed = Known(1L), Foothold = Known(3L) }, MilitaryFamily.Defense);
        Assert.Equal(Satisfaction.Unmet, unarmed.Satisfaction);
        Assert.True(unarmed.Deficit.Value > 0);
        Assert.Contains(unarmed.Evidence, e => e.Fact == "unarmed" && e.Value.Value == "1");

        var hostile = Derive(new MilitaryFacts { Hostiles = Known(2L), Armed = Known(3L), Unarmed = Known(0L), Foothold = Known(3L) }, MilitaryFamily.Defense);
        Assert.Equal(Satisfaction.Unmet, hostile.Satisfaction);

        var unknown = Derive(new MilitaryFacts(), MilitaryFamily.Defense);
        Assert.Equal(Satisfaction.Unknown, unknown.Satisfaction);
        Assert.False(unknown.Deficit.IsKnown);
    }

    [Fact]
    public void PestsIsMetUnmetOrUnknownByTheCensusAndStaleHunts()
    {
        var met = Derive(new MilitaryFacts { Pests = Known(0) }, MilitaryFamily.Pests);
        Assert.Equal((Satisfaction.Met, 0.0, NeedTier.Wellbeing), (met.Satisfaction, met.Deficit.Value, met.Tier));

        var unmet = Derive(new MilitaryFacts { Pests = Known(2) }, MilitaryFamily.Pests);
        Assert.Equal((Satisfaction.Unmet, 2.0), (unmet.Satisfaction, unmet.Deficit.Value));

        // A hunt of the Need's own whose animal is gone keeps the Need unmet until the work step cancels it.
        var stale = Derive(new MilitaryFacts { Pests = Known(0), StaleHunts = [new StaleHunt(new("p"), new("p-0"))] }, MilitaryFamily.Pests);
        Assert.Equal((Satisfaction.Unmet, 1.0), (stale.Satisfaction, stale.Deficit.Value));
        Assert.Contains(stale.Evidence, e => e.Fact == "stale_hunts" && e.Value.Value == "1");

        Assert.Equal(Satisfaction.Unknown, Derive(new MilitaryFacts(), MilitaryFamily.Pests).Satisfaction);
    }

    [Fact]
    public void TrainingIsMetUnmetOrUnknownByTheReview()
    {
        var met = Derive(new MilitaryFacts { Training = Known(new TrainingReview { Capable = 3 }) }, MilitaryFamily.Training);
        Assert.Equal((Satisfaction.Met, NeedTier.Development), (met.Satisfaction, met.Tier));

        var unmet = Derive(new MilitaryFacts { Training = Known(new TrainingReview { Capable = 4, Below = 1, ShootingBelow = 1 }) }, MilitaryFamily.Training);
        Assert.Equal((Satisfaction.Unmet, 0.25), (unmet.Satisfaction, unmet.Deficit.Value));

        Assert.Equal(Satisfaction.Unknown, Derive(new MilitaryFacts(), MilitaryFamily.Training).Satisfaction);
    }

    [Fact]
    public void DisabledNeedsAreNotDerived()
    {
        Assert.Equal([MilitaryFamily.Pests], new MilitaryFamily(defense: false, training: false).Derive(new MilitaryFacts()).Select(n => n.Id));
    }

    private static AcquisitionSource Pest(string id, int x) =>
        new() { Id = id, Resource = "Meat_Rat", Definition = "Rat", Cell = new(x, 5), Hunt = true };

    private static PestHunt Hunt(string method, params AcquisitionSource[] rows) => new(new MethodId(method), [.. rows]);

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<MilitaryFacts> Runner { get; init; }
        public MilitaryFacts Facts { get; set; } = new();

        public ConcernProgress? Row(NeedId need) => Journal.LoadRounds(TestContext.Current.CancellationToken).Progress.SingleOrDefault(p => p.Concern == need.FilingKey);

        public void File(FamilyRun run) =>
            Journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [PlannerWave.NeedProgressOf(MilitaryFamily.FamilyId, run)!], TestContext.Current.CancellationToken);

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run, NeedId need) => Assert.Single(run.Reports, r => r.Need == need).Verdict;
    }

    private static Colony Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-military-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with { Colonists = Known(3L), IndoorCapacity = Known(3L), BedCapacity = Known(3L), FieldCoverage = Known(1.0) };
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        Colony? colony = null;
        var runner = new FamilyRunner<MilitaryFacts>(new MilitaryFamily(), (_, _) => Task.FromResult(colony!.Facts), MilitaryRounds.Schedule);
        colony = new Colony { Journal = journal, Runner = runner };
        return colony;
    }

    private static MilitaryFacts Unmet(PestHunt? hunt) => new()
    {
        Hostiles = Known(0L),
        Armed = Known(0L),
        Unarmed = Known(3L),
        Foothold = Known(3L),
        Pests = Known(2),
        Hunt = hunt,
        Training = Known(new TrainingReview { Capable = 3 }),
    };

    [Fact]
    public void DefenseIsHeldAndAPestHuntIsAdmittedUnderItsNeed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = Unmet(Hunt("pest-hunt-0123456789abcdef", Pest("rat-a", 5)));
        var run = colony.Run();

        var defense = Colony.Report(run, MilitaryFamily.Defense);
        Assert.True(defense.Is(Cause.AwaitingPlan));
        Assert.Equal(MilitaryFamily.UnsupportedEquipment, defense.Refusal.Subject);
        Assert.Empty(colony.Journal.LoadNeed(MilitaryFamily.Defense, World, ct).Methods);

        Assert.Equal(Outcome.Admitted, Colony.Report(run, MilitaryFamily.Pests).Outcome);
        var method = Assert.Single(run.Methods);
        Assert.Equal("pest-hunt-0123456789abcdef", method.Method.Value);
        Assert.Equal(MilitaryFamily.Pests.FilingKey, colony.Journal.PlanMethod(method.Plan, ct)!.Concern);
        Assert.Equal("Clear pests: pest hunt, pest Rat", OperationIntent.Of(colony.Journal.PlanMethod(method.Plan, ct)!));

        // The same selection continues its open Method.
        var again = colony.Run();
        Assert.Equal(BuildingRuntimePackage.BuildingReasonExistingWork, Colony.Report(again, MilitaryFamily.Pests));
        Assert.Equal(ClaimOutcome.Continued, Assert.Single(again.Methods).Outcome);
    }

    [Fact]
    public void HuntsOfDifferentAnimalsRunSideBySideAndASecondHuntOfOneAnimalIsHeldByItsClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = Unmet(Hunt("pest-hunt-a", Pest("rat-a", 5)));
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run(), MilitaryFamily.Pests).Outcome);

        colony.Facts = Unmet(Hunt("pest-hunt-b", Pest("rat-b", 9)));
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run(), MilitaryFamily.Pests).Outcome);
        Assert.Equal(2, colony.Journal.LoadNeed(MilitaryFamily.Pests, World, ct).Methods.Count);

        // A fresh selection that hunts rat-a again overlaps the open Method's claim and is refused with nothing written.
        colony.Facts = Unmet(Hunt("pest-hunt-c", Pest("rat-a", 6), Pest("rat-c", 12)));
        var held = Colony.Report(colony.Run(), MilitaryFamily.Pests);
        Assert.True(held.Is(Cause.Claim));
        Assert.Equal("rat-a", held.Refusal.Subject);
        Assert.Equal(2, colony.Journal.LoadNeed(MilitaryFamily.Pests, World, ct).Methods.Count);
    }

    [Fact]
    public void AnOutdoorHazardProposesNoHuntAndARunWithoutOneWaitsOnItsOpenWork()
    {
        using var colony = Open();
        colony.Facts = Unmet(Hunt("pest-hunt-a", Pest("rat-a", 5))) with { HuntingHeld = true };
        var held = colony.Run();
        Assert.Empty(held.Methods);

        colony.Facts = Unmet(Hunt("pest-hunt-a", Pest("rat-a", 5)));
        _ = colony.Run();
        colony.Facts = Unmet(null);
        Assert.Equal(BuildingRuntimePackage.BuildingReasonExistingWork, Colony.Report(colony.Run(), MilitaryFamily.Pests));
    }

    [Fact]
    public void NeedRowsReachRoundsProgressWithTheirStatus()
    {
        using var colony = Open();
        colony.Facts = Unmet(Hunt("pest-hunt-a", Pest("rat-a", 5)));
        colony.File(colony.Run());

        var defense = colony.Row(MilitaryFamily.Defense)!;
        Assert.Equal((Cause.AwaitingPlan, MilitaryFamily.UnsupportedEquipment), (defense.Planner, defense.PlannerSubject));
        var pests = colony.Row(MilitaryFamily.Pests)!;
        Assert.Equal(("pest-hunt-a", true), (pests.Method, pests.Open));
        // Training is met: no row.
        Assert.Null(colony.Row(MilitaryFamily.Training));
    }

    [Fact]
    public void TheFamilyIsOneDueQueueEntryReplacingTheEquipPestAndTrainingPlanners()
    {
        var entry = ClockPlannerCatalog.FamilyEntry(new FamilyRunner<MilitaryFacts>(new MilitaryFamily(), (_, _) => Task.FromResult(new MilitaryFacts()), MilitaryRounds.Schedule));
        Assert.Equal("military", entry.Name);
        Assert.Contains(ActionKind.Acquisition, entry.Kinds);
        Assert.Contains(ActionKind.Building, entry.Kinds);
        Assert.Contains(ActionKind.Equip, entry.Kinds);
        Assert.Contains(RimGovernor.Brain.Facts.Section.Emergency, entry.Sections);
        Assert.DoesNotContain(ClockPlannerCatalog.Entries, e => e.Name is "equip" or "pestAcquisition" or "training");
        Assert.False(entry.Immediate);
    }
}
