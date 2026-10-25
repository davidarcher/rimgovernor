using System.Text.Json;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Store;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using JournalStore = RimGovernor.Brain.Store.Store;
using PbCommon = RimGovernor.Contracts.Wire.Common;
using PbLifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The colony status read with no living home colonist is a valid report, not a read failure (#2856): the roster
/// read lists zero rows, the report says so, and the route projection serves it as data.
/// </summary>
public sealed class ColonyStatusTests
{
    private static readonly PbCommon.Identity WorldIdentity = new() { ColonyId = "colony-1", LoadToken = "load-1", MapId = 1 };

    private static readonly PbCommon.ObservationContext Context = new() { Identity = WorldIdentity, Tick = 74338, NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.AtCase(7) };

    private sealed class Worlds : IWorldSource
    {
        public Task<World> ReadWorldAsync(CancellationToken ct) => Task.FromResult(new World(new ColonyId("colony-1"), new LoadId("load-1"), new MapId(1)));
    }

    private sealed class Session : IPlayerSession
    {
        public Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null) => Task.FromResult(requested);

        public Task ManualAsync(CancellationToken ct) => Task.CompletedTask;

        public Task ManualForResumeAsync(CancellationToken ct) => Task.CompletedTask;

        public bool HoldsGrant(GenerationSnapshot scope) => false;

        public bool TargetsWorld(World world) => false;

        public void Disable() { }

        public ControlState State() => default!;

        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Native(params PawnState[] roster) : IColonyStatusNative
    {
        public PbCommon.ObservationContext RosterContext { get; init; } = Context;

        public Task<PbLifecycle.LoadedIdentity> IdentityAsync(CancellationToken ct) =>
            Task.FromResult(new PbLifecycle.LoadedIdentity { Context = Context, Paused = false });

        public Task<ColonyFactsSnapshot> ReadColonyFactsAsync(PbCommon.Identity identity, CancellationToken ct) =>
            Task.FromResult(ColonyFactsCoreTests.Snapshot() with { Context = Context, ColonistCount = (uint)roster.Count(r => r.Dead != true) });

        public Task<ResearchSnapshot> ReadResearchAsync(PbCommon.Identity identity, CancellationToken ct) =>
            Task.FromResult(new ResearchSnapshot { Context = Context, Finished = [], Slots = [], Projects = [] });

        public Task<PawnSnapshot> ReadHomeColonistsAsync(PbCommon.Identity identity, CancellationToken ct) =>
            Task.FromResult(new PawnSnapshot { Context = RosterContext, Pawns = [.. roster] });
    }

    private static Task<ColonyStatusReport> ReadAsync(params PawnState[] roster) => ReadNativeAsync(new Native(roster));

    private static async Task<ColonyStatusReport> ReadNativeAsync(Native native)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = JournalStore.Open(Path.Combine(Path.GetTempPath(), "rg-colony-status-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var timeout = TimeSpan.FromSeconds(5);
        using var player = Player.Create(new PlayerConfig(timeout, timeout), journal, new Session(), new Worlds(), ct);
        return await new ColonyStatus(player, native).ReadAsync(ct);
    }

    private static PawnState Colonist(string id, bool dead = false) =>
        PawnFixtures.Row(id) with { Dead = dead };

    [Fact]
    public async Task AChangedRosterGenerationCannotMixColonyStatusFrames()
    {
        var native = new Native(Colonist("pawn-1"))
        {
            RosterContext = Context with { NativeGeneration = new PbCommon.ObservedGeneration.AtCase(8) },
        };
        await Assert.ThrowsAsync<RimGovernor.Brain.Bridge.Contracts.ObservationChangedException>(() => ReadNativeAsync(native));
    }

    [Fact]
    public async Task CompletedReviewPublishesObservedEmptyCollectionsAndFalse()
    {
        var report = await ReadAsync();
        var pending = ReadProjections.Colony(report);
        var completed = ReadProjections.Colony(report with
        {
            Review = new ColonyStatusReview { Tick = new Tick(74300), Stockpiles = [], ForbiddenSupplies = false },
        });
        Assert.Null(pending.FoodPlanTick);
        Assert.Null(pending.Stockpiles);
        Assert.Null(pending.ForbiddenSupplies);
        Assert.Equal(74300, completed.FoodPlanTick);
        Assert.Empty(completed.Stockpiles!);
        Assert.False(completed.ForbiddenSupplies);
    }
    [Fact]
    public async Task CensusKeepsNoNeedAndPendingReviewAsNamedStates()
    {
        var report = await ReadAsync(Colonist("pawn-1"));
        var pawn = Assert.Single(report.Pawns);
        Assert.IsType<NeedLevel.Lacks>(pawn.Mood);
        Assert.IsType<FoodNeed.Lacks>(pawn.Food);
        Assert.False(pawn.Downed);
        Assert.Null(report.Review);
        var dto = ReadProjections.Colony(report);
        Assert.Null(dto.Stockpiles);
        Assert.Null(dto.ForbiddenSupplies);
        Assert.Null(dto.Pawns[0].Mood);
        Assert.Null(dto.Pawns[0].Food);
    }

    [Fact]
    public async Task ObservedNeedLevelsIncludingZeroSurviveProjection()
    {
        var row = Colonist("pawn-1");
        row = row with { Needs = row.Needs with { Mood = new NeedLevel.Has(0), Food = new FoodNeed.Has(0, RimGovernor.Contracts.Wire.Defs.HungerCategory.Starving) }, Downed = true };
        var report = await ReadAsync(row);
        var dto = ReadProjections.Colony(report);
        Assert.Equal(0, Assert.IsType<NeedLevel.Has>(report.Pawns[0].Mood).Level);
        Assert.True(dto.Pawns[0].Downed);
        Assert.Equal(0, dto.Pawns[0].Mood);
        Assert.Equal(0, dto.Pawns[0].Food);
        Assert.Equal(1, dto.Downed);
    }

    [Fact]
    public async Task NoEatersAndZeroRunwayRemainDistinct()
    {
        var report = await ReadAsync();
        var noEaters = report with { FoodRunway = new FoodRunway.NoDemand() };
        var zeroRunway = report with { FoodRunway = new FoodRunway.Days { Value = 0 } };
        Assert.Null(ReadProjections.Colony(noEaters).FoodRunwayDays);
        Assert.Equal(0, ReadProjections.Colony(zeroRunway).FoodRunwayDays);
    }
    [Fact]
    public async Task StatusHasObservedTechTierBeforeAnyRoutineReview()
    {
        var report = await ReadAsync();
        Assert.Equal(RimGovernor.Brain.Policy.TechTier.Camp, report.TechTier);
    }

    [Fact]
    public async Task EmptyRosterReportsNoColonistsWithoutFailing()
    {
        var report = await ReadAsync();

        Assert.Equal(ColonyRoster.NoColonists, report.Roster);
        Assert.Empty(report.Pawns);
        Assert.Equal(74338, report.RosterTick.Value);
        Assert.Equal(0, report.Colonists);

        using var body = JsonDocument.Parse(PlayerJson.Serialize(ReadProjections.Colony(report)));
        Assert.Equal("no_colonists", body.RootElement.GetProperty("roster").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("pawns").GetArrayLength());
    }

    [Fact]
    public async Task RosterOfOnlyTheDeadReportsNoColonists()
    {
        var report = await ReadAsync(Colonist("pawn-1", dead: true));

        Assert.Equal(ColonyRoster.NoColonists, report.Roster);
        Assert.Empty(report.Pawns);
    }

    [Fact]
    public async Task LivingColonistsAreListed()
    {
        var report = await ReadAsync(Colonist("pawn-1"), Colonist("pawn-2", dead: true));

        Assert.Equal(ColonyRoster.Colonists, report.Roster);
        Assert.Equal("pawn-1", Assert.Single(report.Pawns).Id.Value);
    }
}
