using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The rooms family (#3255): lighting, flooring, art, comfort, hosting, the party spot and gatherings are plain
/// thresholds with no latch, the comfort steps chain by precondition, a party is held, and the building Methods are
/// admitted under their Needs side by side, held only by their claims.
/// </summary>
public sealed class RoomsFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static RoomsFamily All() => new(true, true, true, true, true);

    // Each Need's own predicate, as the full composition derives it: the family raises on the first Need whose fact is unread.
    private static Need Of(RoomsFacts facts, NeedId id) =>
        id == RoomsFamily.Lighting ? RoomsFamily.LightingNeed(facts)
        : id == RoomsFamily.Flooring ? RoomsFamily.FlooringNeed(facts)
        : id == RoomsFamily.Art ? RoomsFamily.ArtNeed(facts)
        : id == RoomsFamily.Comfort ? RoomsFamily.ComfortNeed(facts)
        : id == RoomsFamily.Hosting ? RoomsFamily.HostingNeed(facts)
        : id == RoomsFamily.PartySpot ? RoomsFamily.PartySpotNeed(facts)
        : id == RoomsFamily.WorkChairs ? RoomsFamily.WorkChairsNeed(facts)
        : RoomsFamily.GatheringsNeed(facts, partySpot: true);

    /// <summary>Every fact of the full composition read, with nothing owed.</summary>
    private static readonly RoomsFacts Settled = new()
    {
        Lighting = Known(new LightingReview { Known = true }),
        Flooring = Known(new FlooringReview { Known = true }),
        Profiles = Known<EquatableArray<PawnProfile>>([]),
        SculptureRoomsOwed = Known(false),
        BasicComfort = Known(ComfortTests.NoComfortObservation),
        HostingRecovered = Known(true),
        HostingDeficit = 0.0,
        PartySpotOwed = Known(false),
        WorkChairsOwed = Known(false),
        Gathering = Known(new GatheringPlan()),
    };

    private static PawnProfile Artist(string id, string inspiration = "") => new()
    {
        Id = new(id),
        WorkSkill = new Dictionary<WorkType, string> { [WorkType.Art] = "Artistic" },
        Skills = new Dictionary<string, ProfileSkill> { ["Artistic"] = new() { Name = "Artistic", Level = 10 } },
        Inspiration = Known(inspiration),
    };

    [Fact]
    public void TheLightingNeedIsAPlainThresholdOverTheLightingReview()
    {
        var met = Of(new() { Lighting = Known(new LightingReview { Known = true }) }, RoomsFamily.Lighting);
        Assert.Equal((RoomsFamily.Lighting, NeedTier.Development, Satisfaction.Met, 0.0), (met.Id, met.Tier, met.Satisfaction, met.Deficit));

        var dark = Of(new() { Lighting = Known(new LightingReview { Known = true, Active = true, Dark = ["bench-1"] }) }, RoomsFamily.Lighting);
        Assert.Equal((Satisfaction.Unmet, 1.0), (dark.Satisfaction, dark.Deficit));

        // A planned room's unbuilt lamps reopen the Need with no bench dark.
        var planned = Of(new() { Lighting = Known(new LightingReview { Known = true }.WithPlanned(Known<EquatableArray<int>>([4]))) }, RoomsFamily.Lighting);
        Assert.Equal(Satisfaction.Unmet, planned.Satisfaction);

        Assert.Equal("lighting census", Assert.Throws<RequiredFactException>(() => Of(new(), RoomsFamily.Lighting)).Fact);
    }

    [Fact]
    public void TheFlooringNeedIsAPlainThresholdOverTheFlooringReview()
    {
        var met = Of(new() { Flooring = Known(new FlooringReview { Known = true }) }, RoomsFamily.Flooring);
        Assert.Equal((RoomsFamily.Flooring, NeedTier.Development, Satisfaction.Met, 0.0), (met.Id, met.Tier, met.Satisfaction, met.Deficit));

        var owed = Of(new() { Flooring = Known(new FlooringReview { Known = true, Active = true }) }, RoomsFamily.Flooring);
        Assert.Equal((Satisfaction.Unmet, 1.0), (owed.Satisfaction, owed.Deficit));

        Assert.Equal("flooring census", Assert.Throws<RequiredFactException>(() => Of(new(), RoomsFamily.Flooring)).Fact);
    }

    [Fact]
    public void TheArtNeedIsUnmetWhileAnArtistIsInspiredOrABedroomIsOwedASculptureAnArtistCanCarve()
    {
        var none = Of(new() { Profiles = Known<EquatableArray<PawnProfile>>([Artist("a")]), SculptureRoomsOwed = Known(false) }, RoomsFamily.Art);
        Assert.Equal((RoomsFamily.Art, NeedTier.Development, Satisfaction.Met, 0.0), (none.Id, none.Tier, none.Satisfaction, none.Deficit));

        var owed = Of(new() { Profiles = Known<EquatableArray<PawnProfile>>([Artist("a")]), SculptureRoomsOwed = Known(true) }, RoomsFamily.Art);
        Assert.Equal((Satisfaction.Unmet, 1.0), (owed.Satisfaction, owed.Deficit));

        // No colonist can sculpt: an owed bedroom has no remedy, so the Need is met.
        Assert.Equal(Satisfaction.Met, Of(new() { Profiles = Known<EquatableArray<PawnProfile>>([]), SculptureRoomsOwed = Known(true) }, RoomsFamily.Art).Satisfaction);

        // An inspired artist decides the Need while the bedrooms are unread.
        Assert.Equal(Satisfaction.Unmet, Of(new() { Profiles = Known<EquatableArray<PawnProfile>>([Artist("a", ArtRules.InspiredCreativity)]) }, RoomsFamily.Art).Satisfaction);

        Assert.Equal("colonist profiles", Assert.Throws<RequiredFactException>(() => Of(new() { SculptureRoomsOwed = Known(true) }, RoomsFamily.Art)).Fact);
        Assert.Equal("sculpture rooms owed", Assert.Throws<RequiredFactException>(() => Of(new() { Profiles = Known<EquatableArray<PawnProfile>>([Artist("a")]) }, RoomsFamily.Art)).Fact);
    }

    [Fact]
    public void TheComfortNeedIsTheBasicComfortReview()
    {
        var empty = ComfortTests.NoComfortObservation;
        var met = Of(new() { BasicComfort = Known(empty) }, RoomsFamily.Comfort);
        Assert.Equal((RoomsFamily.Comfort, NeedTier.Wellbeing, Satisfaction.Met, 0.0), (met.Id, met.Tier, met.Satisfaction, met.Deficit));
        Assert.Empty(met.Preconditions);

        // A colonist with no seat nor recreation: both halves short.
        var bare = Of(new() { BasicComfort = Known(empty with { Census = empty.Census with { People = [new PawnId("a")] } }) }, RoomsFamily.Comfort);
        Assert.Equal((Satisfaction.Unmet, 1.0), (bare.Satisfaction, bare.Deficit));

        Assert.Equal("basic comfort census", Assert.Throws<RequiredFactException>(() => Of(new(), RoomsFamily.Comfort)).Fact);
    }

    [Fact]
    public void HostingThePartySpotAndGatheringsChainByPreconditionWithNoPhaseLatch()
    {
        var hosting = Of(new() { HostingRecovered = Known(false), HostingDeficit = 0.5 }, RoomsFamily.Hosting);
        Assert.Equal((NeedTier.Development, Satisfaction.Unmet, 0.5), (hosting.Tier, hosting.Satisfaction, hosting.Deficit));
        Assert.Equal([RoomsFamily.Comfort], hosting.Preconditions);
        Assert.Equal(Satisfaction.Met, Of(new() { HostingRecovered = Known(true), HostingDeficit = 0.0 }, RoomsFamily.Hosting).Satisfaction);
        Assert.Equal("hosted comfort", Assert.Throws<RequiredFactException>(() => Of(new(), RoomsFamily.Hosting)).Fact);

        var spot = Of(new() { PartySpotOwed = Known(true) }, RoomsFamily.PartySpot);
        Assert.Equal((Satisfaction.Unmet, 1.0), (spot.Satisfaction, spot.Deficit));
        Assert.Equal([RoomsFamily.Hosting], spot.Preconditions);
        Assert.Equal(Satisfaction.Met, Of(new() { PartySpotOwed = Known(false) }, RoomsFamily.PartySpot).Satisfaction);
        Assert.Equal("party spot", Assert.Throws<RequiredFactException>(() => Of(new(), RoomsFamily.PartySpot)).Fact);

        var chairs = Of(new() { WorkChairsOwed = Known(true) }, RoomsFamily.WorkChairs);
        Assert.Equal((NeedTier.Development, Satisfaction.Unmet, 1.0), (chairs.Tier, chairs.Satisfaction, chairs.Deficit));
        Assert.Equal([RoomsFamily.Comfort], chairs.Preconditions);
        Assert.Equal(Satisfaction.Met, Of(new() { WorkChairsOwed = Known(false) }, RoomsFamily.WorkChairs).Satisfaction);
        Assert.Equal("work chairs", Assert.Throws<RequiredFactException>(() => Of(new(), RoomsFamily.WorkChairs)).Fact);

        var party = Of(new() { Gathering = Known(new GatheringPlan { Organizer = new("a") }) }, RoomsFamily.Gatherings);
        Assert.Equal((NeedTier.Wellbeing, Satisfaction.Unmet, 1.0), (party.Tier, party.Satisfaction, party.Deficit));
        Assert.Equal([RoomsFamily.PartySpot], party.Preconditions);
        Assert.Equal(Satisfaction.Met, Of(new() { Gathering = Known(new GatheringPlan()) }, RoomsFamily.Gatherings).Satisfaction);
        Assert.Equal("gathering mood census", Assert.Throws<RequiredFactException>(() => Of(new(), RoomsFamily.Gatherings)).Fact);

        // Without the comfort Needs composed the gatherings Need has no precondition.
        Assert.Empty(Assert.Single(new RoomsFamily(false, false, false, false, true).Derive(Settled)).Preconditions);
        Assert.Equal([RoomsFamily.Lighting, RoomsFamily.Flooring, RoomsFamily.Art, RoomsFamily.Comfort, RoomsFamily.Hosting, RoomsFamily.PartySpot, RoomsFamily.WorkChairs, RoomsFamily.Gatherings],
            All().Derive(Settled).Select(n => n.Id));
        // Any one composed Need's fact unread raises for the family.
        Assert.Equal("party spot", Assert.Throws<RequiredFactException>(() => All().Derive(Settled with { PartySpotOwed = default })).Fact);
    }

    [Fact]
    public void AnOwedPartyIsHeldAndNothingElseIsProposed()
    {
        var family = All();
        var facts = Settled with { Gathering = Known(new GatheringPlan { Organizer = new("a") }), Lighting = Known(new LightingReview { Known = true, Active = true }) };
        var held = Assert.Single(family.Propose(family.Derive(facts), facts));
        Assert.Equal(RoomsFamily.Gatherings, held.Need);
        Assert.Equal(RoomsFamily.UnsupportedGathering, held.Hold!.Subject);
        Assert.Empty(family.Propose([Of(facts, RoomsFamily.Lighting)], facts));
    }

    [Fact]
    public void TheArtBillsAreFiledUnderTheArtNeed()
    {
        var (key, need) = RoundsArtPlanner.ReportTarget();
        Assert.Equal(RoomsFamily.Art.FilingKey, need);
        Assert.NotEqual(need, key);
        Assert.True(RoomsFamily.ComfortStep(RoomsFamily.PartySpot.FilingKey));
        Assert.False(RoomsFamily.ComfortStep(RoomsFamily.Lighting.FilingKey));
    }

    [Fact]
    public void TheFamilyIsOneDueQueueEntryReplacingTheLightingFlooringComfortAndGatheringPlanners()
    {
        var entry = ClockPlannerCatalog.FamilyEntry(new FamilyRunner<RoomsFacts>(All(), (_, _) => Task.FromResult(new RoomsFacts()), RoomsRounds.Schedule));
        Assert.Equal("rooms", entry.Name);
        Assert.Contains(ActionKind.Building, entry.Kinds);
        Assert.DoesNotContain(ClockPlannerCatalog.Entries, e => e.Name is "lighting" or "flooring" or "basicComfort" or "comfort" or "gathering");
        Assert.False(entry.Immediate);
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<RoomsFacts> Runner { get; init; }

        public RoomsFacts Facts { get; set; } = new()
        {
            Lighting = Known(new LightingReview { Known = true, Active = true }),
            BasicComfort = Known(ComfortTests.NoComfortObservation with { Census = ComfortTests.NoComfort with { People = [new PawnId("a")] } }),
            HostingRecovered = Known(true),
            HostingDeficit = 0.0,
            PartySpotOwed = Known(false),
            WorkChairsOwed = Known(false),
        };

        /// <summary>The building Methods each Need commits on the next run, each a (Need, MethodId, definition, cell).</summary>
        public List<(NeedId Need, string Method, string Definition, Cell Cell)> Builds { get; } = [];

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run, NeedId need) => Assert.Single(run.Reports, r => r.Need == need).Verdict;
    }

    private static PlanSpec Build(NeedId need, string definition, Cell cell)
    {
        var id = PlanIds.MintPlanId();
        return new PlanSpec(id, new(1), [DomainAction.NewBuildingAction(new(id.Value + "-0"), new Building(definition, cell, Rotation.North, ""), ConstructionTierRules.PlannerTier(need.FilingKey, Phase.None))]);
    }

    /// <summary>A reviewed colony whose rooms family works its lighting and comfort Needs by committing the listed builds under each Need.</summary>
    private static Colony Open()
    {
        var journal = Reviewed();
        Colony? colony = null;
        var runner = new FamilyRunner<RoomsFacts>(new RoomsFamily(true, false, false, true, false), (_, _) => Task.FromResult(colony!.Facts),
            RoomsRounds.Schedule,
            (work, _, call) =>
            {
                var verdicts = new List<Verdict>();
                foreach (var (need, method, definition, cell) in colony!.Builds.Where(b => b.Need == work.Need.Id))
                {
                    verdicts.Add(RoundsBuildingPlanner.OwnerCommitVerdict(journal.CommitOwnerWork(work.Owner, new MethodId(method), "", Build(need, definition, cell), call)));
                }
                return Task.FromResult(verdicts.Count == 0 ? Verdict.AwaitingPlan(work.Need.Id.Value, "no_method") : verdicts[^1]);
            });
        colony = new Colony { Journal = journal, Runner = runner };
        return colony;
    }

    /// <summary>A journal holding an enabled review of <see cref="World"/> with three pawns' work resources.</summary>
    private static Journal Reviewed()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-rooms-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with
        {
            Resources = Known<EquatableArray<Amount>>([new(new("WoodLog"), 1000)]),
            Colonists = 3L, IndoorCapacity = 3L, BedCapacity = 3L, FieldCoverage = Known(1.0),
            WorkAdmission = facts.WorkAdmission with
            {
                Buildings = Known<EquatableArray<WorkBuildingCost>>([
                    new("TorchLamp", "", Known<EquatableArray<Amount>>([new(new("WoodLog"), 20)])) { Size = Known(new Cell(1, 1)) },
                    new("DiningChair", "", Known<EquatableArray<Amount>>([new(new("WoodLog"), 10)])) { Size = Known(new Cell(1, 1)) }]),
            },
        };
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        return journal;
    }

    [Fact]
    public void LampsAndFurnitureAreAdmittedSideBySideAndABuildOnAHeldCellIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Builds.AddRange([
            (RoomsFamily.Lighting, "lamp-bench-1", "TorchLamp", new Cell(5, 5)),
            (RoomsFamily.Lighting, "lamp-bench-2", "TorchLamp", new Cell(9, 9)),
            (RoomsFamily.Comfort, "chair-dining", "DiningChair", new Cell(12, 12))]);
        var run = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(run, RoomsFamily.Lighting).Outcome);
        Assert.Equal(Outcome.Admitted, Colony.Report(run, RoomsFamily.Comfort).Outcome);
        Assert.Equal(3, run.Methods.Count);
        Assert.All(run.Methods, m => Assert.Equal(ClaimOutcome.Admitted, m.Outcome));
        Assert.Equal(2, colony.Journal.LoadNeed(RoomsFamily.Lighting, World, ct).Methods.Count);
        Assert.Single(colony.Journal.LoadNeed(RoomsFamily.Comfort, World, ct).Methods);

        // The same MethodIds again continue the open plans; a chair on a lamp's cell is refused on that claim.
        colony.Builds.Add((RoomsFamily.Comfort, "chair-reading", "DiningChair", new Cell(5, 5)));
        var refused = Colony.Report(colony.Run(), RoomsFamily.Comfort);
        Assert.True(refused.Is(Cause.Claim), refused.ToString());
        Assert.Single(colony.Journal.LoadNeed(RoomsFamily.Comfort, World, ct).Methods);
        Assert.Equal(2, colony.Journal.LoadNeed(RoomsFamily.Lighting, World, ct).Methods.Count);
    }

    [Fact]
    public void HostingWaitsOnComfortAndDoesNotWorkBeforeIt()
    {
        using var colony = Open();
        colony.Facts = colony.Facts with { HostingRecovered = Known(false), HostingDeficit = 1.0, PartySpotOwed = Known(true) };
        colony.Builds.Add((RoomsFamily.Hosting, "hosting-chair", "DiningChair", new Cell(20, 20)));
        var run = colony.Run();
        Assert.DoesNotContain(run.Methods, m => m.Need == RoomsFamily.Hosting);
        Assert.False(Colony.Report(run, RoomsFamily.Hosting).Outcome == Outcome.Admitted);
        Assert.False(Colony.Report(run, RoomsFamily.PartySpot).Outcome == Outcome.Admitted);
    }
}
