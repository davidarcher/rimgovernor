using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The layout family (#3256): home coverage, firebreak, climate envelope and stone shell are plain thresholds over
/// this review's censuses; the firebreak waits on home coverage and the stone shell on the envelope when those are
/// composed; the pure Methods are admitted under the Need owner, side by side, held only by their claims. The
/// defensive layout (#3314) is met while its stored layout is verified and no fight waits; its record survives a
/// reload, and an older save's Standard fields are ignored. The routes Need (#3265) reads the census fresh, with no
/// latch.
/// </summary>
public sealed class LayoutFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly LayoutNeeds All = new() { HomeCoverage = true, Firebreak = true, StoneShell = true, ClimateEnvelope = true };

    private static HomeAreaPlan Diff(params (int X, int Z)[] set) => new()
    {
        Set = EquatableArray.From(set.Select(c => new Cell(c.X, c.Z))),
        Clear = EquatableArray.From(Array.Empty<Cell>()),
    };

    private static FirebreakWork Ring(params (int X, int Z)[] cut) => new() { Cut = [.. cut.Select(c => new Cell(c.X, c.Z))] };

    private static Need Single(LayoutNeeds composed, LayoutFacts facts) => Assert.Single(new LayoutFamily(composed).Derive(facts));

    [Fact]
    public void TheHomeCoverageNeedIsUnmetWhileTheHomeAreaDiffersAndUnknownUnread()
    {
        var composed = new LayoutNeeds { HomeCoverage = true };
        var met = Single(composed, new() { HomeCoverage = Known(Diff()) });
        Assert.Equal((LayoutFamily.HomeCoverage, NeedTier.Wellbeing, Satisfaction.Met, Known(0.0)), (met.Id, met.Tier, met.Satisfaction, met.Deficit));

        var unmet = Single(composed, new() { HomeCoverage = Known(Diff((1, 1), (1, 2))) });
        Assert.Equal((Satisfaction.Unmet, Known(2.0)), (unmet.Satisfaction, unmet.Deficit));
        Assert.Contains(new NeedEvidence("set_cells", Known("2")), unmet.Evidence);

        // Auto-expand still on is a diff of its own.
        Assert.Equal(Satisfaction.Unmet, Single(composed, new() { HomeCoverage = Known(Diff() with { AutoOff = true }) }).Satisfaction);

        var unread = Single(composed, new() { HomeCoverage = Unknown<HomeAreaPlan>() });
        Assert.Equal((Satisfaction.Unknown, false), (unread.Satisfaction, unread.Deficit.IsKnown));
        Assert.Equal(Satisfaction.Unknown, Single(composed, new()).Satisfaction);
    }

    [Fact]
    public void TheFirebreakEnvelopeAndStoneShellNeedsArePlainThresholds()
    {
        var firebreak = new LayoutNeeds { Firebreak = true };
        Assert.Equal(Satisfaction.Met, Single(firebreak, new() { Firebreak = Known(Ring()) }).Satisfaction);
        var owed = Single(firebreak, new() { Firebreak = Known(Ring((3, 3)) with { Deconstruct = [new(4, 4)] }) });
        Assert.Equal((LayoutFamily.Firebreak, Satisfaction.Unmet, Known(2.0)), (owed.Id, owed.Satisfaction, owed.Deficit));
        Assert.Equal(Satisfaction.Unknown, Single(firebreak, new() { Firebreak = Unknown<FirebreakWork>() }).Satisfaction);

        var envelope = new LayoutNeeds { ClimateEnvelope = true };
        Assert.Equal(Satisfaction.Met, Single(envelope, new() { ClimateEnvelope = Known(0) }).Satisfaction);
        var rooms = Single(envelope, new() { ClimateEnvelope = Known(2) });
        Assert.Equal((LayoutFamily.ClimateEnvelope, NeedTier.Wellbeing, Satisfaction.Unmet, Known(2.0)), (rooms.Id, rooms.Tier, rooms.Satisfaction, rooms.Deficit));
        Assert.Equal(Satisfaction.Unknown, Single(envelope, new() { ClimateEnvelope = Unknown<int>() }).Satisfaction);

        var stone = new LayoutNeeds { StoneShell = true };
        Assert.Equal(Satisfaction.Met, Single(stone, new() { StoneShell = Known<EquatableArray<string>>([]) }).Satisfaction);
        var walls = Single(stone, new() { StoneShell = Known<EquatableArray<string>>(["wall-1"]) });
        Assert.Equal((LayoutFamily.StoneShell, NeedTier.Development, Satisfaction.Unmet, Known(1.0)), (walls.Id, walls.Tier, walls.Satisfaction, walls.Deficit));
        Assert.Equal(Satisfaction.Unknown, Single(stone, new() { StoneShell = Unknown<EquatableArray<string>>() }).Satisfaction);

        // The envelope and the stone shell are the runtime step's: the family proposes nothing for them purely.
        var family = new LayoutFamily(envelope with { StoneShell = true });
        var facts = new LayoutFacts { ClimateEnvelope = Known(0), StoneShell = Known<EquatableArray<string>>(["wall-1"]) };
        Assert.Empty(family.Propose(family.Derive(facts), facts));
    }

    [Fact]
    public void TheFirebreakWaitsOnHomeCoverageAndTheStoneShellOnTheEnvelopeOnlyWhenComposed()
    {
        var needs = new LayoutFamily(All).Derive(new()).ToDictionary(n => n.Id);
        Assert.Equal([LayoutFamily.HomeCoverage, LayoutFamily.ClimateEnvelope, LayoutFamily.Firebreak, LayoutFamily.StoneShell], new LayoutFamily(All).Derive(new()).Select(n => n.Id));
        Assert.Equal([LayoutFamily.HomeCoverage], needs[LayoutFamily.Firebreak].Preconditions);
        Assert.Equal([LayoutFamily.ClimateEnvelope], needs[LayoutFamily.StoneShell].Preconditions);
        Assert.Empty(needs[LayoutFamily.HomeCoverage].Preconditions);
        Assert.Empty(needs[LayoutFamily.ClimateEnvelope].Preconditions);

        // Without the other Need composed there is nothing to wait on.
        Assert.Empty(Single(new LayoutNeeds { Firebreak = true }, new()).Preconditions);
        Assert.Empty(Single(new LayoutNeeds { StoneShell = true }, new()).Preconditions);
    }

    [Fact]
    public void TheHomeCoverageMethodIsTheDiffAndTheSameDiffContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var diff = Diff((1, 1), (1, 2)) with { AutoOff = true };
        using var colony = Open(new LayoutNeeds { HomeCoverage = true }, new() { HomeCoverage = Known(diff) });
        var first = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(first, LayoutFamily.HomeCoverage).Outcome);
        var method = Assert.Single(first.Methods);
        Assert.Equal((LayoutFamily.HomeCoverage, ClaimOutcome.Admitted, diff.MethodId()), (method.Need, method.Outcome, method.Method));
        Assert.Equal(LayoutFamily.HomeCoverage.FilingKey, colony.Journal.PlanMethod(method.Plan, ct)!.Concern);
        var actions = LayoutFamily.HomeCoverageActions(new PlanId("plan-1"), diff);
        Assert.Equal([ActionKind.AutoHomeArea, ActionKind.Area], actions.Select(a => a.Kind));
        Assert.True(actions[1].Area is { Home: true, Operation: AreaOperation.SetCells });

        // The same diff again continues the open Method.
        Assert.Equal(Verdict.BuildingReasonExistingWork, Colony.Report(colony.Run(), LayoutFamily.HomeCoverage));
        Assert.Single(colony.Journal.LoadNeed(LayoutFamily.HomeCoverage, World, ct).Methods);
    }

    [Fact]
    public void TheFirebreakIsNotWorkedWhileHomeCoverageIsUnmet()
    {
        var ct = TestContext.Current.CancellationToken;
        var ring = Ring((20, 20));
        using var colony = Open(new LayoutNeeds { HomeCoverage = true, Firebreak = true }, new()
        {
            HomeCoverage = Known(Diff((1, 1))),
            Firebreak = Known(ring),
            FirebreakStep = LayoutRounds.FirebreakCandidate(ring, new Dictionary<Cell, CoverClearance>()),
        });
        var run = colony.Run();
        Assert.All(run.Methods, m => Assert.Equal(LayoutFamily.HomeCoverage, m.Need));
        Assert.Empty(colony.Journal.LoadNeed(LayoutFamily.Firebreak, World, ct).Methods);

        // Home coverage met: the ring order is admitted.
        colony.Facts = colony.Facts with { HomeCoverage = Known(Diff()) };
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run(), LayoutFamily.Firebreak).Outcome);
        Assert.Single(colony.Journal.LoadNeed(LayoutFamily.Firebreak, World, ct).Methods);
    }

    [Fact]
    public void FirebreakOrdersOverDisjointCellsRunSideBySideAndAnOverlappingCutIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var none = new Dictionary<Cell, CoverClearance>();
        LayoutFacts Order(FirebreakWork ring) => new() { Firebreak = Known(ring), FirebreakStep = LayoutRounds.FirebreakCandidate(ring, none) };
        using var colony = Open(new LayoutNeeds { Firebreak = true }, Order(Ring((10, 10), (10, 11))));
        var first = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(first, LayoutFamily.Firebreak).Outcome);
        Assert.StartsWith("firebreak-", Assert.Single(first.Methods).Method.Value, StringComparison.Ordinal);

        // The same order continues; an order over other cells is admitted beside it.
        Assert.Equal(Verdict.BuildingReasonExistingWork, Colony.Report(colony.Run(), LayoutFamily.Firebreak));
        colony.Facts = Order(Ring((12, 10)));
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run(), LayoutFamily.Firebreak).Outcome);
        Assert.Equal(2, colony.Journal.LoadNeed(LayoutFamily.Firebreak, World, ct).Methods.Count);

        // An order cutting a cell an open order holds is refused on that cell's claim.
        colony.Facts = Order(Ring((10, 11), (14, 10)));
        var refused = Colony.Report(colony.Run(), LayoutFamily.Firebreak);
        Assert.True(refused.Is(Cause.Claim), refused.ToString());
        Assert.Equal(2, colony.Journal.LoadNeed(LayoutFamily.Firebreak, World, ct).Methods.Count);

        // Nothing owed builds no order.
        Assert.Null(LayoutRounds.FirebreakCandidate(Ring(), none));
    }

    [Fact]
    public void TheDefenseNeedIsMetOnlyWhileTheLayoutIsVerifiedAndNoFightWaits()
    {
        var composed = new LayoutNeeds { Defense = true };
        Need Of(bool standing, bool verified, bool waiting) =>
            Single(composed, new() { Defense = Known(new DefenseLayoutFacts { Standing = standing, Verified = verified, WaitingFight = waiting }) });

        var met = Of(standing: true, verified: true, waiting: false);
        Assert.Equal((LayoutFamily.Defense, NeedTier.Development, Satisfaction.Met, Known(0.0)), (met.Id, met.Tier, met.Satisfaction, met.Deficit));
        Assert.Contains(new NeedEvidence("verified", Known("true")), met.Evidence);

        // A fight waiting in rooms to harden, or a verification that is not current, is work; a tier owed is the deficit.
        Assert.Equal((Satisfaction.Unmet, Known(0.0)), (Of(true, true, true).Satisfaction, Of(true, true, true).Deficit));
        Assert.Equal((Satisfaction.Unmet, Known(0.0)), (Of(true, false, false).Satisfaction, Of(true, false, false).Deficit));
        Assert.Equal((Satisfaction.Unmet, Known(1.0)), (Of(false, false, false).Satisfaction, Of(false, false, false).Deficit));

        // No hysteresis: the same facts give the same answer whatever came before.
        Assert.Equal(Satisfaction.Met, Of(true, true, false).Satisfaction);

        var unread = Single(composed, new() { Defense = Unknown<DefenseLayoutFacts>() });
        Assert.Equal((Satisfaction.Unknown, false), (unread.Satisfaction, unread.Deficit.IsKnown));
        Assert.Equal(Satisfaction.Unknown, Single(composed, new()).Satisfaction);
        Assert.DoesNotContain(new LayoutFamily(All).Derive(new()), n => n.Id == LayoutFamily.Defense);
    }

    /// <summary>
    /// layout/routes: unmet while a listed facility is reachable by no colonist, met once any colonist reaches it (no
    /// latch holds it open, decision 16), unknown while the census is unread; a pending breach does not hold it.
    /// </summary>
    [Fact]
    public void TheRoutesNeedReadsTheCensusFreshWithNoLatch()
    {
        var composed = new LayoutNeeds { Routes = true };
        RoutesObservation Census(bool reachable, string pending = "") => new()
        {
            Pawns = ["a"],
            Facilities = [new() { Id = "store", Kind = "storage", Travel = [new() { Pawn = "a", Reachable = reachable }] }],
        };
        LayoutFacts Facts(RoutesObservation census) =>
            new() { Routes = Known(LayoutFamily.RoutesStandingOf(Known(census), default, RoundsRules.DefaultRoundsPolicy())) };

        var unmet = Single(composed, Facts(Census(reachable: false)));
        Assert.Equal((LayoutFamily.Routes, NeedTier.Development, Satisfaction.Unmet, Known(1.0)), (unmet.Id, unmet.Tier, unmet.Satisfaction, unmet.Deficit));
        var met = Single(composed, Facts(Census(reachable: true)));
        Assert.Equal((Satisfaction.Met, Known(0.0)), (met.Satisfaction, met.Deficit));
        Assert.Equal(Satisfaction.Unknown, Single(composed, new()).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Single(composed, new() { Routes = Known(LayoutFamily.RoutesStandingOf(default, default, RoundsRules.DefaultRoundsPolicy())) }).Satisfaction);
        Assert.Equal([WorkType.Construction], LaborRules.NeedLabor(LayoutFamily.Routes));
    }

    [Fact]
    public void FoundationRemovalsAndDigsClaimTheirCells()
    {
        var actions = new[]
        {
            DomainAction.NewFoundationRemovalAction(new("f"), new FoundationRemoval("TestBridge", new(3, 4))),
            DomainAction.NewExcavationAction(new("d"), new Excavation(new(5, 6), "TestRock")),
        };
        var claims = MethodClaimsRules.MethodClaimsOf(new WorkAdmissionEvidence(), [.. actions.Select(a => new WorkAction(new("p"), a, new ProgressView { Action = a.Id, Stage = Stage.Pending }))]);
        Assert.Contains("foundation/3/4", claims.Targets);
        Assert.Contains("dig/5/6", claims.Targets);
    }

    [Fact]
    public void TheDefenseLayoutRecordSurvivesAReloadAndAnOlderSavesStandardIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new RimGovernor.Brain.Store.World(World.Colony, World.Load, World.Map);
        using var source = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-defense-layout-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        source.SaveDefenseLayout(new DefenseLayoutRecord
        {
            World = world,
            Killbox = "killbox-1",
            Firing = [new(1, 1)],
            Retreat = [new(1, 2)],
            Tiers = [new DefenseTierRecord { Name = DefenseLayout.TierFiringLine, Attempts = 2 }],
            Complete = true,
            VerifiedCombat = "plan-fight",
        }, ct);
        var saved = new Dictionary<string, string>(source.GovernorStateBlobs(ct));

        // An older save's record carries the retired EnsureDefensiveLayout Standard and its episode (#3314).
        var blob = System.Text.Json.Nodes.JsonNode.Parse(saved[Journal.GovernorDefenseLayoutKey])!;
        blob["record"]!["Standard"] = "std-legacy";
        blob["record"]!["Episode"] = 7;
        saved[Journal.GovernorDefenseLayoutKey] = blob.ToJsonString();

        using var loaded = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-defense-layout-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        loaded.RebuildOwners(null, ct);
        loaded.RebuildFamilies(saved, ct);
        var record = loaded.LoadDefenseLayout(world, ct);
        Assert.NotNull(record);
        Assert.Equal(("killbox-1", true, "plan-fight"), (record.Killbox, record.Complete, record.VerifiedCombat));
        Assert.Equal(2, Assert.Single(record.Tiers).Attempts);
        var written = loaded.GovernorStateBlobs(ct)[Journal.GovernorDefenseLayoutKey];
        Assert.DoesNotContain("Standard", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Episode", written, StringComparison.Ordinal);
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<LayoutFacts> Runner { get; init; }
        public required LayoutFacts Facts { get; set; }

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run, NeedId need) => Assert.Single(run.Reports, r => r.Need == need).Verdict;
    }

    /// <summary>A reviewed colony whose layout family reads <paramref name="facts"/>; its runtime steps have no Method.</summary>
    private static Colony Open(LayoutNeeds composed, LayoutFacts facts)
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-layout-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a", "b", "c") }, ct);
        Colony? colony = null;
        var runner = new FamilyRunner<LayoutFacts>(new LayoutFamily(composed), (_, _) => Task.FromResult(colony!.Facts), LayoutRounds.Schedule,
            (work, _, _) => Task.FromResult(Verdict.AwaitingPlan(work.Need.Id.Value, "no_method")));
        colony = new Colony { Journal = journal, Runner = runner, Facts = facts };
        return colony;
    }
}
