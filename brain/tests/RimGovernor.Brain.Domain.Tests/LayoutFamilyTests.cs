using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
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

    private static PlannedRoom[] Rooms(int n) => [.. Enumerable.Range(0, n).Select(_ => new PlannedRoom())];

    private static Need Single(LayoutNeeds composed, LayoutFacts facts) => Assert.Single(new LayoutFamily(composed).Derive(facts));

    /// <summary>Every fact of the <see cref="All"/> composition read and settled.</summary>
    private static readonly LayoutFacts Settled = new()
    {
        HomeCoverage = Diff(), Firebreak = Ring(), StoneShell = [], ClimateEnvelope = Rooms(0),
    };

    [Fact]
    public void TheHomeCoverageNeedIsUnmetWhileTheHomeAreaDiffersAndUnknownUnread()
    {
        var composed = new LayoutNeeds { HomeCoverage = true };
        var met = Single(composed, new() { HomeCoverage = Diff() });
        Assert.Equal((LayoutFamily.HomeCoverage, NeedTier.Wellbeing, Satisfaction.Met, 0.0), (met.Id, met.Tier, met.Satisfaction, met.Deficit));

        var unmet = Single(composed, new() { HomeCoverage = Diff((1, 1), (1, 2)) });
        Assert.Equal((Satisfaction.Unmet, 2.0), (unmet.Satisfaction, unmet.Deficit));

        // Auto-expand still on is a diff of its own.
        Assert.Equal(Satisfaction.Unmet, Single(composed, new() { HomeCoverage = Diff() with { AutoOff = true } }).Satisfaction);

        Assert.Equal("home area plan", Assert.Throws<RequiredFactException>(() => Single(composed, new())).Fact);
    }

    [Fact]
    public void TheFirebreakEnvelopeAndStoneShellNeedsArePlainThresholds()
    {
        var firebreak = new LayoutNeeds { Firebreak = true };
        Assert.Equal(Satisfaction.Met, Single(firebreak, new() { Firebreak = Ring() }).Satisfaction);
        var owed = Single(firebreak, new() { Firebreak = Ring((3, 3)) with { Deconstruct = [new(4, 4)] } });
        Assert.Equal((LayoutFamily.Firebreak, Satisfaction.Unmet, 2.0), (owed.Id, owed.Satisfaction, owed.Deficit));
        Assert.Equal("firebreak ring", Assert.Throws<RequiredFactException>(() => Single(firebreak, new())).Fact);

        var envelope = new LayoutNeeds { ClimateEnvelope = true };
        Assert.Equal(Satisfaction.Met, Single(envelope, new() { ClimateEnvelope = Rooms(0) }).Satisfaction);
        var rooms = Single(envelope, new() { ClimateEnvelope = Rooms(2) });
        Assert.Equal((LayoutFamily.ClimateEnvelope, NeedTier.Wellbeing, Satisfaction.Unmet, 2.0), (rooms.Id, rooms.Tier, rooms.Satisfaction, rooms.Deficit));
        Assert.Equal("climate envelope", Assert.Throws<RequiredFactException>(() => Single(envelope, new())).Fact);

        var stone = new LayoutNeeds { StoneShell = true };
        Assert.Equal(Satisfaction.Met, Single(stone, new() { StoneShell = [] }).Satisfaction);
        var walls = Single(stone, new() { StoneShell = ["wall-1"] });
        Assert.Equal((LayoutFamily.StoneShell, NeedTier.Development, Satisfaction.Unmet, 1.0), (walls.Id, walls.Tier, walls.Satisfaction, walls.Deficit));
        Assert.Equal("flammable walls", Assert.Throws<RequiredFactException>(() => Single(stone, new())).Fact);

        // The envelope and the stone shell are the runtime step's: the family proposes nothing for them purely.
        var family = new LayoutFamily(envelope with { StoneShell = true });
        var facts = new LayoutFacts { ClimateEnvelope = Rooms(0), StoneShell = ["wall-1"] };
        Assert.Empty(family.Propose(family.Derive(facts), facts));
    }

    [Fact]
    public void TheFirebreakWaitsOnHomeCoverageAndTheStoneShellOnTheEnvelopeOnlyWhenComposed()
    {
        var needs = new LayoutFamily(All).Derive(Settled).ToDictionary(n => n.Id);
        Assert.Equal([LayoutFamily.HomeCoverage, LayoutFamily.ClimateEnvelope, LayoutFamily.Firebreak, LayoutFamily.StoneShell], new LayoutFamily(All).Derive(Settled).Select(n => n.Id));
        Assert.Equal([LayoutFamily.HomeCoverage], needs[LayoutFamily.Firebreak].Preconditions);
        Assert.Equal([LayoutFamily.ClimateEnvelope], needs[LayoutFamily.StoneShell].Preconditions);
        Assert.Empty(needs[LayoutFamily.HomeCoverage].Preconditions);
        Assert.Empty(needs[LayoutFamily.ClimateEnvelope].Preconditions);

        // Without the other Need composed there is nothing to wait on.
        Assert.Empty(Single(new LayoutNeeds { Firebreak = true }, Settled).Preconditions);
        Assert.Empty(Single(new LayoutNeeds { StoneShell = true }, Settled).Preconditions);
    }

    [Fact]
    public void TheHomeCoverageMethodIsTheDiffAndTheSameDiffContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var diff = Diff((1, 1), (1, 2)) with { AutoOff = true };
        using var colony = Open(new LayoutNeeds { HomeCoverage = true }, new() { HomeCoverage = diff });
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
            HomeCoverage = Diff((1, 1)),
            Firebreak = ring,
            FirebreakStep = LayoutReview.FirebreakCandidate(ring, new Dictionary<Cell, CoverClearance>()),
        });
        var run = colony.Run();
        Assert.All(run.Methods, m => Assert.Equal(LayoutFamily.HomeCoverage, m.Need));
        Assert.Empty(colony.Journal.LoadNeed(LayoutFamily.Firebreak, World, ct).Methods);

        // Home coverage met: the ring order is admitted.
        colony.Facts = colony.Facts with { HomeCoverage = Diff() };
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run(), LayoutFamily.Firebreak).Outcome);
        Assert.Single(colony.Journal.LoadNeed(LayoutFamily.Firebreak, World, ct).Methods);
    }

    [Fact]
    public void FirebreakOrdersOverDisjointCellsRunSideBySideAndAnOverlappingCutIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var none = new Dictionary<Cell, CoverClearance>();
        LayoutFacts Order(FirebreakWork ring) => new() { Firebreak = ring, FirebreakStep = LayoutReview.FirebreakCandidate(ring, none) };
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
        Assert.Null(LayoutReview.FirebreakCandidate(Ring(), none));
    }

    [Fact]
    public void TheDefenseNeedIsMetOnlyWhileTheLayoutIsVerifiedAndNoFightWaits()
    {
        var composed = new LayoutNeeds { Defense = true };
        Need Of(double deficit, bool verified, bool waiting) =>
            Single(composed, new() { Defense = new DefenseLayoutFacts { Deficit = deficit, Verified = verified, WaitingFight = waiting } });

        var met = Of(deficit: 0, verified: true, waiting: false);
        Assert.Equal((LayoutFamily.Defense, NeedTier.Development, Satisfaction.Met, 0.0), (met.Id, met.Tier, met.Satisfaction, met.Deficit));

        // A fight waiting in rooms to harden, or a comparison that is not current, is work; the protection the chosen
        // alternative owes is the deficit, and holds the Need unmet while it is owed.
        Assert.Equal((Satisfaction.Unmet, 0.0), (Of(0, true, true).Satisfaction, Of(0, true, true).Deficit));
        Assert.Equal((Satisfaction.Unmet, 0.0), (Of(0, false, false).Satisfaction, Of(0, false, false).Deficit));
        Assert.Equal((Satisfaction.Unmet, 0.4), (Of(0.4, true, false).Satisfaction, Of(0.4, true, false).Deficit));

        // No hysteresis: the same facts give the same answer whatever came before.
        Assert.Equal(Satisfaction.Met, Of(0, true, false).Satisfaction);

        Assert.Equal("defense layout", Assert.Throws<RequiredFactException>(() => Single(composed, new())).Fact);
        Assert.DoesNotContain(new LayoutFamily(All).Derive(Settled), n => n.Id == LayoutFamily.Defense);
    }

    /// <summary>
    /// layout/routes: unmet while a listed facility is reachable by no colonist, met once any colonist reaches it (no
    /// latch holds it open, decision 16), unknown while the census is unread; a pending breach does not hold it.
    /// </summary>
    [Fact]
    public void TheRoutesNeedReadsTheCensusFreshWithNoLatch()
    {
        var composed = new LayoutNeeds { Routes = true };
        RimGovernor.Brain.GameFacts.Observations.RoutesFacts Census(bool reachable, string pending = "") => new()
        {
            PawnIds = ["a"], Traffic = [], TrafficSamples = 0,
            Facilities = [new() { Id = "store", Kind = RouteFacilityKind.Storage, Cell = new(4, 4), Travel = [reachable ? new RouteTravel.Measured { Pawn = "a", Cost = 30, Cells = 12 } : new RouteTravel.Unreachable { Pawn = "a" }], Breaches = [] }],
        };
        LayoutFacts Facts(RimGovernor.Brain.GameFacts.Observations.RoutesFacts census) =>
            new() { Routes = LayoutFamily.RoutesStandingOf(census, RoomTestFacts.NoRooms, Rules.DefaultPlannerSettings()) };

        var unmet = Single(composed, Facts(Census(reachable: false)));
        Assert.Equal((LayoutFamily.Routes, NeedTier.Development, Satisfaction.Unmet, 1.0), (unmet.Id, unmet.Tier, unmet.Satisfaction, unmet.Deficit));
        var met = Single(composed, Facts(Census(reachable: true)));
        Assert.Equal((Satisfaction.Met, 0.0), (met.Satisfaction, met.Deficit));
        Assert.Equal("routes census", Assert.Throws<RequiredFactException>(() => Single(composed, new())).Fact);
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
            Tiers = [new DefenseTierRecord { Name = DefenseLayout.TierFiringLine, Attempts = 2 }],
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
        Assert.Equal("killbox-1", record.Killbox);
        Assert.Equal(2, Assert.Single(record.Tiers).Attempts);
        var written = loaded.GovernorStateBlobs(ct)[Journal.GovernorDefenseLayoutKey];
        Assert.DoesNotContain("Standard", written, StringComparison.Ordinal);
        Assert.DoesNotContain("Episode", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// A retained tier settles against the census: it is built while every placement stands and falls back to unbuilt
    /// when one is gone, with its attempt count untouched; a legacy removal tier drops once its buildings are gone.
    /// </summary>
    [Fact]
    public void ARetainedTierSettlesAgainstTheCensus()
    {
        var barricade = new DefenseBuilding { Definition = "Barricade", Cell = new(1, 0), Rotation = Rotation.North, Stuff = "WoodLog" };
        var tier = new DefenseTierRecord { Name = DefenseLayout.TierFiringLine, Buildings = [barricade], Attempts = 2 };
        var record = new DefenseLayoutRecord { Tiers = [tier] };

        var census = new DefenseCensus { Edifice = { [barricade.Cell] = "Barricade" } };
        Assert.True(DefenseLayoutPlanner.DefenseTierCensusUpdate(record, census));
        Assert.True(record.AllBuilt());
        Assert.False(DefenseLayoutPlanner.DefenseTierCensusUpdate(record, census));

        census.Edifice.Clear();
        Assert.True(DefenseLayoutPlanner.DefenseTierCensusUpdate(record, census));
        Assert.Equal((false, 2), (tier.Built, tier.Attempts));
        Assert.False(record.AllBuilt());
    }

    /// <summary>
    /// The <c>family/defense_layout</c> blob round-trips unchanged through a save and a rebuild, and carries no
    /// verification (#3008): Complete, VerifiedTick, VerifiedCombat and TurretsProbedTick live in the ColonyReview's memory,
    /// and the tiers carry no Refused cells.
    /// </summary>
    [Fact]
    public void TheDefenseLayoutBlobRoundTripsWithoutVerification()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new RimGovernor.Brain.Store.World(World.Colony, World.Load, World.Map);
        using var source = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-defense-layout-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        source.SaveDefenseLayout(new DefenseLayoutRecord
        {
            World = world,
            Killbox = "killbox-1",
            KillboxDigest = "digest-1",
            Entrances = [new(9, 9)],
            Tiers =
            [
                new DefenseTierRecord
                {
                    Name = DefenseLayout.TierFiringLine, Attempts = 1, Built = true,
                    Buildings = [new DefenseBuilding { Definition = "Barricade", Cell = new(1, 0), Rotation = Rotation.North, Stuff = "WoodLog" }],
                },
                new DefenseTierRecord
                {
                    Name = new DefenseTierName("perimeter-remove-xb-03"), Nodes = ["ring-1"], Remove = true,
                    Buildings = [new DefenseBuilding { Definition = "Wall", Cell = new(5, 5), Rotation = Rotation.North, Stuff = "WoodLog" }],
                },
            ],
        }, ct);
        var saved = new Dictionary<string, string>(source.GovernorStateBlobs(ct));
        var blob = saved[Journal.GovernorDefenseLayoutKey];
        foreach (var gone in new[] { "Complete", "VerifiedTick", "VerifiedCombat", "TurretsProbedTick", "Refused", "TrapLane", "Firing", "Retreat", "Reserved" })
        {
            Assert.DoesNotContain(gone, blob, StringComparison.Ordinal);
        }

        using var loaded = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-defense-layout-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        loaded.RebuildOwners(null, ct);
        loaded.RebuildFamilies(saved, ct);
        var record = loaded.LoadDefenseLayout(world, ct);
        Assert.NotNull(record);
        Assert.Equal(("killbox-1", "digest-1"), (record.Killbox, record.KillboxDigest));
        var firing = record.Tiers[0];
        Assert.Equal((1, true, "Barricade"), (firing.Attempts, firing.Built, Assert.Single(firing.Buildings).Definition));
        var ring = record.Tiers[1];
        // A removal tier names the nodes it came from and stores its buildings: the plan no longer resolves them.
        Assert.Equal((true, "ring-1", "Wall"), (ring.Remove, Assert.Single(ring.Nodes), Assert.Single(ring.Buildings).Definition));
        Assert.Equal(blob, loaded.GovernorStateBlobs(ct)[Journal.GovernorDefenseLayoutKey]);
    }

    /// <summary>
    /// Combat fights from the saved layout once its firing line stood in the last census, and its trap corridor too when
    /// it has one (#3008): no verification is read. A combat fixture's one built firing-line tier passes.
    /// </summary>
    [Fact]
    public void CombatFightsFromALayoutWhoseFiringLineIsBuilt()
    {
        DefenseTierRecord Firing(bool built) => new() { Name = DefenseLayout.TierFiringLine, Built = built };
        DefenseTierRecord Corridor(bool built) => new()
        {
            Name = DefenseLayout.TierTrapCorridor, Built = built,
            Buildings = [new DefenseBuilding { Definition = "TrapSpike", Cell = new(4, 4), Stuff = "WoodLog" }],
        };
        static DefenseLayoutRecord Layout(params DefenseTierRecord[] tiers) => new() { Tiers = [.. tiers] };

        Assert.True(Layout(Firing(true)).CombatReady());
        Assert.False(Layout(Firing(false)).CombatReady());
        Assert.False(Layout(Corridor(true)).CombatReady());
        Assert.False(Layout(Firing(true), Corridor(false)).CombatReady());
        Assert.True(Layout(Firing(true), Corridor(true)).CombatReady());
        Assert.True(Layout(Firing(true), new DefenseTierRecord { Name = DefenseLayout.TierTrapCorridor }).CombatReady());
    }

    /// <summary>The defense verification is per world and in memory (#3008): another load or map sees none, so its layout re-verifies.</summary>
    [Fact]
    public void TheDefenseVerificationIsPerWorld()
    {
        var verification = new DefenseVerification();
        var here = new RimGovernor.Brain.Store.World(World.Colony, World.Load, World.Map);
        var reload = new RimGovernor.Brain.Store.World(World.Colony, new("reload"), World.Map);
        verification.Update(here, v => v with { Complete = true, Tick = new(10), Combat = "plan-fight" });
        Assert.True(verification.Get(here).Complete);
        Assert.Equal(new DefenseVerified(), verification.Get(reload));
        verification.Update(reload, v => v with { Tick = new(20) });
        Assert.Equal(new DefenseVerified { Tick = new(20) }, verification.Get(reload));
        Assert.False(verification.Get(here).Complete);
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
        journal.Review(new() { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a", "b", "c") }, ct);
        Colony? colony = null;
        var runner = new FamilyRunner<LayoutFacts>(new LayoutFamily(composed), (_, _) => Task.FromResult(colony!.Facts), LayoutReview.Schedule,
            (work, _, _) => Task.FromResult(Verdict.AwaitingPlan(work.Need.Id.Value, "no_method")));
        colony = new Colony { Journal = journal, Runner = runner, Facts = facts };
        return colony;
    }
}
