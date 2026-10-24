using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A cadence run keeps the wait or refusal a ready Need ended on last time and skips its work (#3556): the work
/// runs again on evidence (a run that is not for the cadence alone), when the Need's standing changed, when the
/// last verdict was an admission, open work, a held claim or an unread field, in the immediate pass, and in
/// another world.
/// </summary>
public sealed class FamilyKeptWaitsTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly NeedId Fix = NeedId.Of("test-keep", "fix");

    private sealed record KeepFacts(Fact<bool> Fixed, Fact<double> Deficit = default);

    /// <summary>One Need, ready while the colony is not fixed; its work is the test's, counted and scripted.</summary>
    private sealed class KeepFamily : IFamily<KeepFacts>
    {
        public string Id => "test-keep";

        public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Emergency];

        public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [FactFamily.Emergency];

        public NeedTier Tier { get; set; } = NeedTier.Development;

        public IReadOnlyList<Need> Derive(KeepFacts facts) =>
            [new Need { Id = Fix, Tier = Tier, Satisfaction = !facts.Fixed.TryGetValue(out var v) ? throw new RequiredFactException("fixed") : v ? Satisfaction.Met : Satisfaction.Unmet, Deficit = facts.Deficit }];

        public IReadOnlyList<Need> DeriveSeriousHarm(KeepFacts facts) => Tier == NeedTier.SeriousHarm ? Derive(facts) : [];

        public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, KeepFacts facts) => [];
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required KeepFamily Family { get; init; }
        public required FamilyRunner<KeepFacts> Runner { get; init; }
        public KeepFacts Facts { get; set; } = new(Known(false));
        public Verdict Work { get; set; } = Verdict.AwaitingPlan("site", "no_space");
        public int Worked { get; set; }

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run(bool cadence, bool immediate = false, GenerationSnapshot? world = null) =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = world ?? World, Immediate = immediate, Cadence = cadence }, TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run) => Assert.Single(run.Reports, r => r.Need == Fix).Verdict;
    }

    private static Colony Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-kept-waits-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        Review(journal, World);
        var family = new KeepFamily();
        Colony? colony = null;
        var runner = new FamilyRunner<KeepFacts>(family, (_, _) => Task.FromResult(colony!.Facts), new FamilySchedule { Kinds = [ActionKind.Building] },
            (_, _, _) =>
            {
                colony!.Worked++;
                return Task.FromResult(colony.Work);
            });
        colony = new Colony { Journal = journal, Family = family, Runner = runner };
        return colony;
    }

    private static void Review(Journal journal, GenerationSnapshot world)
    {
        var ct = TestContext.Current.CancellationToken;
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with { Colonists = 3L, IndoorCapacity = 3L, BedCapacity = 3L, FieldCoverage = Known(1.0) };
        journal.ReviewRounds(new() { Current = world, Tick = new(100), Revision = journal.LoadRounds(ct).Revision, Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
    }

    [Fact]
    public void ACadenceRunKeepsTheWaitAndSkipsTheWorkUntilEvidence()
    {
        using var colony = Open();
        var first = colony.Run(cadence: false);
        Assert.Equal(colony.Work, Colony.Report(first));
        Assert.Equal(1, colony.Worked);
        Assert.Empty(first.Kept);

        // The next cadence runs keep the verdict without working the Need, and the entry's verdict is the kept one.
        var kept = colony.Run(cadence: true);
        Assert.Equal(colony.Work, Colony.Report(kept));
        Assert.Equal([Fix], kept.Kept);
        Assert.Equal(colony.Work, kept.Verdict);
        Assert.Equal(1, colony.Worked);
        colony.Run(cadence: true);
        Assert.Equal(1, colony.Worked);

        // A run on evidence (dirty, a wake, a full or an all step) works it again.
        var evidence = colony.Run(cadence: false);
        Assert.Empty(evidence.Kept);
        Assert.Equal(2, colony.Worked);
    }

    [Fact]
    public void ARefusalIsKeptAndAnAdmissionOpenWorkAHeldClaimOrAnUnreadFieldIsNot()
    {
        using var colony = Open();
        colony.Work = Verdict.Refuse(Cause.SharedAdmission, "safeguard", "");
        colony.Run(cadence: false);
        Assert.Equal([Fix], colony.Run(cadence: true).Kept);
        Assert.Equal(1, colony.Worked);

        foreach (var verdict in new[] { Verdict.BuildingReasonAdmitted, Verdict.BuildingReasonExistingWork, Verdict.ClaimHeld("pawn/a"), Verdict.FieldUnavailable("rooms") })
        {
            colony.Work = verdict;
            colony.Run(cadence: false);
            var worked = colony.Worked;
            Assert.Empty(colony.Run(cadence: true).Kept);
            Assert.Equal(worked + 1, colony.Worked);
        }
    }

    [Fact]
    public void AChangedStandingWorksTheNeedAgain()
    {
        using var colony = Open();
        colony.Run(cadence: false);
        Assert.Equal(1, colony.Worked);

        // Met on the cadence: nothing is kept, and the memory now holds no ready Need.
        colony.Facts = new(Known(true));
        var met = colony.Run(cadence: true);
        Assert.Empty(met.Kept);
        Assert.Equal(Verdict.BuildingReasonNoDeficit, Colony.Report(met));
        Assert.Equal(1, colony.Worked);

        // Ready again on the cadence: it was not ready last time, so its work runs.
        colony.Facts = new(Known(false));
        Assert.Empty(colony.Run(cadence: true).Kept);
        Assert.Equal(2, colony.Worked);
    }

    [Fact]
    public void ANeedDerivedDifferentlyWorksAgain()
    {
        // The live clearance census (#3681): the pawns cleared what the last step designated, so the Need's
        // deficit fell while it stayed ready; the refusal filed while the work was in flight is not kept.
        using var colony = Open();
        colony.Run(cadence: false);
        Assert.Equal([Fix], colony.Run(cadence: true).Kept);
        Assert.Equal(1, colony.Worked);

        colony.Facts = colony.Facts with { Deficit = Known(1.0) };
        Assert.Empty(colony.Run(cadence: true).Kept);
        Assert.Equal(2, colony.Worked);
        Assert.Equal([Fix], colony.Run(cadence: true).Kept);
        Assert.Equal(2, colony.Worked);
    }

    [Fact]
    public void TheImmediatePassNeitherKeepsNorRemembers()
    {
        using var colony = Open();
        colony.Family.Tier = NeedTier.SeriousHarm;
        colony.Run(cadence: false);
        Assert.Equal(1, colony.Worked);

        // An immediate step works the Need whatever the selection, and the ordinary cadence run after it still
        // keeps the last ordinary run's verdict.
        Assert.Empty(colony.Run(cadence: true, immediate: true).Kept);
        Assert.Equal(2, colony.Worked);
        Assert.Equal([Fix], colony.Run(cadence: true).Kept);
        Assert.Equal(2, colony.Worked);
    }

    [Fact]
    public void AnotherWorldForgetsTheKeptVerdicts()
    {
        using var colony = Open();
        colony.Run(cadence: false);
        var other = World with { Load = new("load-2") };
        Review(colony.Journal, other);
        Assert.Empty(colony.Run(cadence: true, world: other).Kept);
        Assert.Equal(2, colony.Worked);
    }

    [Fact]
    public void AKeptNeedsRowSaysSoAndOnlyWaitsAndRefusalsAreKeepable()
    {
        var entry = new PlannerEntry { Name = "test-keep", Configured = _ => true, Run = (_, _, _, _, _, _) => Task.FromResult(default(Verdict)) };
        var report = new NeedReport(Fix, Verdict.AwaitingPlan("site", "no_space"));
        Assert.True((bool)PlannerWave.NeedDecision(entry, report, TimeSpan.Zero, false, kept: true).Attrs["kept"]!);
        Assert.False(PlannerWave.NeedDecision(entry, report, TimeSpan.Zero, false).Attrs.ContainsKey("kept"));

        Assert.True(FamilyRunner.Keepable(Verdict.WaitFor(Cause.Facility, "export_bills")));
        Assert.True(FamilyRunner.Keepable(Verdict.NoSpace("comfort")));
        Assert.False(FamilyRunner.Keepable(Verdict.BuildingReasonNoDeficit));
        Assert.False(FamilyRunner.Keepable(Verdict.BuildingReasonAdmitted));
        Assert.False(FamilyRunner.Keepable(Verdict.BuildingReasonExistingWork));
        Assert.False(FamilyRunner.Keepable(Verdict.ClaimHeld("pawn/a")));
        Assert.False(FamilyRunner.Keepable(Verdict.FieldUnavailable("rooms")));
    }
}
