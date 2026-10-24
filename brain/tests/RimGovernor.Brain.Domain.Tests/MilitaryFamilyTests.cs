using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The military family (#3259): defense, pests, training and equipment are Needs with pure met, unmet and unknown predicates;
/// pest hunts and apparel policies are Need-owned Methods admitted by claims, side by side for different animals or pawns.
/// </summary>
public sealed class MilitaryFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    // Only the Need under test is composed: the family raises on the first composed Need whose fact is unread.
    private static Need Derive(MilitaryFacts facts, NeedId id) => Assert.Single(new MilitaryFamily(
        id == MilitaryFamily.Defense, id == MilitaryFamily.Pests, id == MilitaryFamily.Training, id == MilitaryFamily.Equipment,
        id == MilitaryFamily.RestoreWorkers, id == MilitaryFamily.Combat).Derive(facts));

    [Fact]
    public void DefenseIsMetUnmetOrUnknownByTheArmedFootholdHostilesAndUnarmed()
    {
        var met = Derive(new MilitaryFacts { Hostiles = Known(0L), Armed = Known(3L), Unarmed = Known(0L), Foothold = 3L }, MilitaryFamily.Defense);
        Assert.Equal((Satisfaction.Met, 0.0, NeedTier.Wellbeing), (met.Satisfaction, met.Deficit.Value, met.Tier));

        var unarmed = Derive(new MilitaryFacts { Hostiles = Known(0L), Armed = Known(3L), Unarmed = Known(1L), Foothold = 3L }, MilitaryFamily.Defense);
        Assert.Equal(Satisfaction.Unmet, unarmed.Satisfaction);
        Assert.True(unarmed.Deficit.Value > 0);

        var hostile = Derive(new MilitaryFacts { Hostiles = Known(2L), Armed = Known(3L), Unarmed = Known(0L), Foothold = 3L }, MilitaryFamily.Defense);
        Assert.Equal(Satisfaction.Unmet, hostile.Satisfaction);

        Assert.Equal("armed fighters, hostiles or unarmed colonists", Assert.Throws<RequiredFactException>(() => Derive(new MilitaryFacts(), MilitaryFamily.Defense)).Fact);
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

        Assert.Equal("pests", Assert.Throws<RequiredFactException>(() => Derive(new MilitaryFacts(), MilitaryFamily.Pests)).Fact);
    }

    [Fact]
    public void TrainingIsMetUnmetOrUnknownByTheReview()
    {
        var met = Derive(new MilitaryFacts { Training = Known(new TrainingReview { Capable = 3 }) }, MilitaryFamily.Training);
        Assert.Equal((Satisfaction.Met, NeedTier.Development), (met.Satisfaction, met.Tier));

        var unmet = Derive(new MilitaryFacts { Training = Known(new TrainingReview { Capable = 4, Below = 1, ShootingTrainees = 1 }) }, MilitaryFamily.Training);
        Assert.Equal((Satisfaction.Unmet, 0.25), (unmet.Satisfaction, unmet.Deficit.Value));

        Assert.Equal("training review", Assert.Throws<RequiredFactException>(() => Derive(new MilitaryFacts(), MilitaryFamily.Training)).Fact);
    }

    [Fact]
    public void DisabledNeedsAreNotDerived()
    {
        Assert.Equal([MilitaryFamily.Pests], new MilitaryFamily(defense: false, training: false, equipment: false, restoreWorkers: false, combat: false).Derive(new MilitaryFacts { Pests = Known(0) }).Select(n => n.Id));
        Assert.Empty(new MilitaryFamily(restoreWorkers: false, combat: false).SeriousHarmReads);
    }

    /// <summary>The combat Need (#3311) is unmet by live hostiles or squad-hunt prey, met by neither, unknown while the census is unread.</summary>
    [Fact]
    public void CombatIsUnmetByHostilesOrHuntPreyMetByNeitherAndUnknownWhileTheCensusIsUnread()
    {
        var hostile = Derive(new MilitaryFacts { Hostiles = Known(2L) }, MilitaryFamily.Combat);
        Assert.Equal((Satisfaction.Unmet, 2.0, NeedTier.SeriousHarm), (hostile.Satisfaction, hostile.Deficit.Value, hostile.Tier));

        var hunt = Derive(new MilitaryFacts { Hostiles = Known(0L), HuntPrey = [new PawnId("deer")] }, MilitaryFamily.Combat);
        Assert.Equal((Satisfaction.Unmet, 1.0), (hunt.Satisfaction, hunt.Deficit.Value));

        var clear = Derive(new MilitaryFacts { Hostiles = Known(0L) }, MilitaryFamily.Combat);
        Assert.Equal((Satisfaction.Met, 0.0), (clear.Satisfaction, clear.Deficit.Value));

        Assert.Equal("hostiles", Assert.Throws<RequiredFactException>(() => Derive(new MilitaryFacts(), MilitaryFamily.Combat)).Fact);

        Assert.Equal(Situation.Active, RoundsRules.CombatSituation(Known(1L), 0));
        Assert.Equal(Situation.Active, RoundsRules.CombatSituation(Known(0L), 1));
        Assert.Equal(Situation.Clear, RoundsRules.CombatSituation(Known(0L), 0));
        Assert.Equal(Situation.Unclear, RoundsRules.CombatSituation(Unknown<long>(), 0));
    }

    [Fact]
    public void RestoreWorkersIsUnmetByAStrayDraftWithNoHostileAndUnknownWhileTheCensusIsUnread()
    {
        var unmet = Derive(new MilitaryFacts { StrayDrafts = Known(2), Hostiles = Known(0L) }, MilitaryFamily.RestoreWorkers);
        Assert.Equal((Satisfaction.Unmet, 2.0, NeedTier.SeriousHarm), (unmet.Satisfaction, unmet.Deficit.Value, unmet.Tier));

        // A standing hostile owns the drafts; no stray draft is met whatever the hostiles.
        Assert.Equal(Satisfaction.Met, Derive(new MilitaryFacts { StrayDrafts = Known(2), Hostiles = Known(1L) }, MilitaryFamily.RestoreWorkers).Satisfaction);
        Assert.Equal(Satisfaction.Met, Derive(new MilitaryFacts { StrayDrafts = Known(0) }, MilitaryFamily.RestoreWorkers).Satisfaction);

        Assert.Equal("stray drafts or hostiles", Assert.Throws<RequiredFactException>(() => Derive(new MilitaryFacts { Hostiles = Known(0L) }, MilitaryFamily.RestoreWorkers)).Fact);
        Assert.Equal("stray drafts or hostiles", Assert.Throws<RequiredFactException>(() => Derive(new MilitaryFacts { StrayDrafts = Known(1) }, MilitaryFamily.RestoreWorkers)).Fact);
    }

    [Fact]
    public void EquipmentIsUnmetByAShellShortageAndOtherwiseUnknownWithoutTheGearReview()
    {
        Assert.Equal("gear review", Assert.Throws<RequiredFactException>(() => Derive(new MilitaryFacts(), MilitaryFamily.Equipment)).Fact);
        // An empty census has every pawn outfitted (#3904).
        var empty = Derive(new MilitaryFacts { Gear = Known(ColonyModelFixtures.NoGear()) }, MilitaryFamily.Equipment);
        Assert.Equal((Satisfaction.Met, 0.0), (empty.Satisfaction, empty.Deficit.Value));

        var shells = Derive(new MilitaryFacts { ShellsShort = Known(true) }, MilitaryFamily.Equipment);
        Assert.Equal((Satisfaction.Unmet, 1.0), (shells.Satisfaction, shells.Deficit.Value));

        Assert.Equal("gear review", Assert.Throws<RequiredFactException>(() => Derive(new MilitaryFacts { ShellsShort = Known(false) }, MilitaryFamily.Equipment)).Fact);
    }

    [Fact]
    public void EquipmentProposesNothingWithoutAGearCensus()
    {
        Assert.Empty(MilitaryFamily.EquipmentMethods(default));
        Assert.Empty(MilitaryFamily.EquipmentMethods(Known(ColonyModelFixtures.NoGear())));
    }

    private static DomainAction Dress(string id, string pawn, string def = "Apparel_Parka") =>
        DomainAction.NewApparelPolicyAction(new(id), new(new ApparelPolicySpec(new(pawn), "token-" + id, "policy-" + id, [def], 0, 1, 0, 6)));

    private static NeedCommit Commit(Journal journal, string method, DomainAction action) =>
        journal.CommitClaimedMethod(MilitaryFamily.Equipment, World, new(method), "", new PlanSpec(new(method + "-plan"), new(1), [action]), TestContext.Current.CancellationToken);

    [Fact]
    public void ApparelPoliciesForTwoPawnsAreAdmittedTogetherAndASecondForOnePawnIsHeldByItsClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        Assert.Equal(ClaimOutcome.Admitted, Commit(colony.Journal, "apparel-policy-a", Dress("d1", "a")).Outcome);
        Assert.Equal(ClaimOutcome.Admitted, Commit(colony.Journal, "apparel-policy-b", Dress("d2", "b")).Outcome);
        Assert.Equal(2, colony.Journal.LoadNeed(MilitaryFamily.Equipment, World, ct).Methods.Count);

        // The same policy (same MethodId) continues; a different policy for pawn a is refused with nothing written.
        Assert.Equal(ClaimOutcome.Continued, Commit(colony.Journal, "apparel-policy-a", Dress("d1", "a")).Outcome);
        var refused = Commit(colony.Journal, "apparel-policy-a2", Dress("d3", "a", "Apparel_Duster"));
        Assert.Equal(ClaimOutcome.Conflict, refused.Outcome);
        Assert.Equal("gear/a", refused.Conflict!.Claim);
        Assert.Equal(2, colony.Journal.LoadNeed(MilitaryFamily.Equipment, World, ct).Methods.Count);

        // An outfit prune claims the outfit database: a second prune is held while the first is open.
        Assert.Equal(ClaimOutcome.Admitted, Commit(colony.Journal, "outfit-prune-1", DomainAction.NewPolicyPruneAction(new("p1"), new(PolicyDatabase.Outfit, ["1"]))).Outcome);
        var prune = Commit(colony.Journal, "outfit-prune-2", DomainAction.NewPolicyPruneAction(new("p2"), new(PolicyDatabase.Outfit, ["2"])));
        Assert.Equal(("policy/Outfit", ClaimOutcome.Conflict), (prune.Conflict?.Claim, prune.Outcome));
    }

    [Fact]
    public void ApparelPolicyMethodsAreNamedByTheirValue()
    {
        var a = new ApparelPolicy(new ApparelPolicySpec(new("a"), "t", "policy-a", ["Apparel_Parka"], 0, 1, 0, 6));
        var b = a with { };
        Assert.Equal(MilitaryFamily.ApparelPolicyEncoded(a), MilitaryFamily.ApparelPolicyEncoded(b));
        var other = new ApparelPolicy(new ApparelPolicySpec(new("a"), "t", "policy-a", ["Apparel_Duster"], 0, 1, 0, 6));
        Assert.NotEqual(MilitaryFamily.ApparelPolicyEncoded(a), MilitaryFamily.ApparelPolicyEncoded(other));
        // #3285: the default filter keeps its MethodId; allowing tainted apparel is another value.
        Assert.DoesNotContain("AllowTainted", MilitaryFamily.ApparelPolicyEncoded(a), StringComparison.Ordinal);
        var allowed = new ApparelPolicy(a.Spec with { AllowTainted = true });
        Assert.NotEqual(MilitaryFamily.ApparelPolicyEncoded(a), MilitaryFamily.ApparelPolicyEncoded(allowed));
    }

    [Fact]
    public void EquipmentWorkReportsTheFirstAdmissionThenAnyRealVerdictThenTheUnsupportedReplacement()
    {
        var spot = Verdict.BuildingReasonNoDeficit;
        var bench = Verdict.BuildingReasonAdmitted;
        Assert.Equal(bench, MilitaryRounds.EquipmentVerdict([spot, bench], false));
        var held = MilitaryRounds.EquipmentVerdict([spot, Verdict.BuildingReasonDisabled], false);
        Assert.True(held.Is(Cause.AwaitingPlan));
        Assert.Equal(MilitaryFamily.UnsupportedApparelReplacement, held.Refusal.Subject);
        Assert.Equal(MilitaryFamily.Equipment.Value, MilitaryRounds.EquipmentVerdict([spot], true).Refusal.Subject);
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

        public FamilyRun Run(bool immediate = false) =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World, Immediate = immediate }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run, NeedId need) => Assert.Single(run.Reports, r => r.Need == need).Verdict;
    }

    private static Colony Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-military-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with { Colonists = 3L, IndoorCapacity = 3L, BedCapacity = 3L, FieldCoverage = Known(1.0) };
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        Colony? colony = null;
        // The runs read no gear census, so equipment (which would raise on it) is not composed.
        var runner = new FamilyRunner<MilitaryFacts>(new MilitaryFamily(equipment: false), (_, _) => Task.FromResult(colony!.Facts), MilitaryRounds.Schedule);
        colony = new Colony { Journal = journal, Runner = runner };
        return colony;
    }

    private static MilitaryFacts Unmet(PestHunt? hunt) => new()
    {
        Hostiles = Known(0L),
        StrayDrafts = Known(0),
        Armed = Known(0L),
        Unarmed = Known(3L),
        Foothold = 3L,
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
        Assert.Equal(Verdict.BuildingReasonExistingWork, Colony.Report(again, MilitaryFamily.Pests));
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
        Assert.Equal(Verdict.BuildingReasonExistingWork, Colony.Report(colony.Run(), MilitaryFamily.Pests));
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
    public void TheImmediatePassJudgesOnlyCombatAndRestoreWorkersAndItsRowReachesRoundsProgress()
    {
        using var colony = Open();
        colony.Facts = Unmet(Hunt("pest-hunt-a", Pest("rat-a", 5))) with { StrayDrafts = Known(1) };
        var run = colony.Run(immediate: true);
        Assert.Equal([MilitaryFamily.Combat, MilitaryFamily.RestoreWorkers], run.Standings.Select(s => s.Need.Id));
        Assert.Empty(run.Methods);
        Assert.Empty(run.SeriousHarmPlans);
        colony.File(run);
        Assert.NotNull(colony.Row(MilitaryFamily.RestoreWorkers));
        Assert.Empty(colony.Journal.LoadNeed(MilitaryFamily.RestoreWorkers, World, TestContext.Current.CancellationToken).Methods);
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
        Assert.DoesNotContain(ClockPlannerCatalog.Entries, e => e.Name is "equip" or "pestAcquisition" or "training" or "gear" or "armory");
        Assert.Contains(ActionKind.ApparelPolicy, entry.Kinds);
        Assert.Contains(ActionKind.PolicyPrune, entry.Kinds);
        // Restore-workers is a SeriousHarm Need: the family runs in the immediate pass, woken by any draft's end,
        // and replaced the undraft planner (#3311).
        Assert.True(entry.Immediate);
        Assert.Contains(ActionKind.OwnedDraft, entry.Kinds);
        Assert.DoesNotContain(ClockPlannerCatalog.Entries, e => e.Name == "undraft");
    }
}
