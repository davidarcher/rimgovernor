using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Policy;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;
using PbCommon = RimGovernor.Contracts.Wire.Common;
using PbLifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3511: a new colony started from a theme adopts the theme into the new world's goals once it completes.</summary>
public sealed class ThemedLifecycleTests
{
    private static readonly PbCommon.ObservationContext Colony = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "c", LoadToken = "l", MapId = 1 }, Tick = 0 };

    private sealed class Generating : ILifecycleWriter
    {
        public bool Done { get; set; }

        public bool Fails { get; set; }

        public Task<PbLifecycle.NewColonyReply> NewColonyAsync(PbLifecycle.NewColonyRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PbLifecycle.NewColonyReply { Outcome = new PbLifecycle.NewColonyPending { RequestId = request.RequestId, Phase = PbLifecycle.NewColonyPhase.GeneratingWorld, ElapsedMs = 0, RerollCount = 0 } });

        public Task<PbLifecycle.NewColonyReply> ReadNewColonyAsync(string requestId, CancellationToken cancellationToken) =>
            Task.FromResult(new PbLifecycle.NewColonyReply
            {
                Outcome = Fails ? new PbCommon.Failure { RefusalClass = RimGovernor.Contracts.Wire.Common.RefusalClass.Unclassified, Code = PbCommon.FailureCode.NativeFailure }
                    : Done ? new PbLifecycle.NewColonyCompleted { RequestId = requestId, Context = Colony, Paused = true, ByteLength = 0 }
                    : new PbLifecycle.NewColonyPending { RequestId = requestId, Phase = PbLifecycle.NewColonyPhase.GeneratingWorld, ElapsedMs = 0, RerollCount = 0 },
            });

        public Task<PbLifecycle.SaveReply> SaveAsync(PbLifecycle.SaveRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PbLifecycle.SaveReply> ReadSaveAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PbLifecycle.LoadReply> LoadAsync(PbLifecycle.LoadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PbLifecycle.LoadReply> ReadLoadAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PbLifecycle.MenuReply> MenuAsync(PbLifecycle.MenuRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PbLifecycle.MenuReply> ReadMenuAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static PbLifecycle.NewColonyRequest Request(string id, string? theme) => new() { RequestId = id, TimeoutMs = 600_000, Spec = NewColonyFixtures.Spec() with { Theme = theme } };

    private static (ThemedLifecycle Lifecycle, Generating Inner, List<string> Adopted) Build()
    {
        var inner = new Generating();
        var adopted = new List<string>();
        return (new ThemedLifecycle(inner, (context, theme, _) =>
        {
            adopted.Add(context.Identity.ColonyId + ":" + theme);
            return Task.CompletedTask;
        }), inner, adopted);
    }

    [Fact]
    public async Task TheThemeIsAdoptedOnceWhenAPollFirstReadsTheColonyCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (lifecycle, inner, adopted) = Build();

        _ = await lifecycle.NewColonyAsync(Request("r1", "naked-cannibals"), ct);
        _ = await lifecycle.ReadNewColonyAsync("r1", ct);
        Assert.Empty(adopted);

        inner.Done = true;
        _ = await lifecycle.ReadNewColonyAsync("r1", ct);
        _ = await lifecycle.ReadNewColonyAsync("r1", ct);

        Assert.Equal(["c:naked-cannibals"], adopted);
    }

    [Fact]
    public async Task AnUnthemedOrFailedColonyAdoptsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (lifecycle, inner, adopted) = Build();

        _ = await lifecycle.NewColonyAsync(Request("plain", null), ct);
        _ = await lifecycle.NewColonyAsync(Request("failed", "fortress"), ct);
        inner.Fails = true;
        _ = await lifecycle.ReadNewColonyAsync("failed", ct);
        inner.Fails = false;
        inner.Done = true;
        _ = await lifecycle.ReadNewColonyAsync("plain", ct);
        _ = await lifecycle.ReadNewColonyAsync("failed", ct);

        Assert.Empty(adopted);
    }

    [Fact]
    public void AdoptingThemeMintsItsGoalsInTheNewWorldAndWakesTheGoalsPlanner()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = new RimGovernor.Brain.Store.World(new("c"), new("l"), new(1));
        using var store = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-themed-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var wake = new WakeSignal();
        var goals = new HostGoals(store, () => home, wake, new GoalStatusBoard());
        var book = new PresetBook(
            [new ThemePreset("demo", "Demo", [new PresetMember("meme.Nudism", new GoalRecord { Kind = GoalKind.Meme, Target = "Nudism" })])],
            new Everything());

        goals.AdoptTheme(home, book, "demo", ct);
        goals.AdoptTheme(home, book, "demo", ct);

        var record = store.LoadGoals(home, ct);
        Assert.Equal("demo", Assert.Single(record.Themes).Preset);
        Assert.Equal("Nudism", Assert.Single(record.Goals).Target);
        Assert.Equal(record.Goals, goals.Current().Record.Goals);
        Assert.Equal([HostGoals.PlannerName], wake.TakeInvalidated().Planners);
        Assert.Throws<InvalidOperationException>(() => goals.AdoptTheme(home, book, "no-such-theme", ct));
    }

    private sealed class Everything : IPresetCatalog
    {
        public bool Has(PresetDefClass kind, string name) => true;
    }
}
