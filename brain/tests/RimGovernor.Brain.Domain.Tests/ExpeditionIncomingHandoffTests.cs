using System.Reflection;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;
using PbCommon = RimGovernor.Contracts.Wire.Common;
using PbObservations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The incoming expedition handoff (#3389) reads the world progression census itself, after the cheap identity,
/// control and map checks; the step bundle never carries one.
/// </summary>
public sealed class ExpeditionIncomingHandoffTests
{
    private const string Colony = "colony";
    private const string Load = "load";
    private const int HomeMap = 1;
    private const int ExpeditionMap = 2;
    private const int DestinationTile = 7;
    private const string Pawn = "pawn";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GenerationSnapshot Home => new(new(Colony), new(HomeMap), new(Load), new("review"), new(1), new(1));

    /// <summary>The census read, counted: the one thing the step bundle lacks.</summary>
#pragma warning disable CA1852 // DispatchProxy derives the proxy from its base, so the base cannot be sealed
    private class Native : DispatchProxy
    {
        public int Reads { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IClockStepNative.ReadWorldProgressionAsync))
            {
                throw new NotSupportedException(targetMethod?.Name + " is outside the handoff rig");
            }
            Reads++;
            return Task.FromResult(new WorldProgressionRead
            {
                Context = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = Colony, LoadToken = Load, MapId = ExpeditionMap }, Tick = 100 },
                Maps = [new PbObservations.WorldMap { Id = ExpeditionMap, Tile = DestinationTile, Home = true, Pawns = [PawnFixtures.Row(Pawn)], QuestWorkers = [] }],
            });
        }
    }
#pragma warning restore CA1852

    private sealed class Worlds : IWorldSource
    {
        public Task<World> ReadWorldAsync(CancellationToken ct) => Task.FromResult(new World(new(Colony), new(Load), new(ExpeditionMap)));
    }

    private sealed class Session : IPlayerSession, IPlayerMapFocusConfirmation
    {
        private ControlState _state = new(Home, true, true);

        public int Confirmed { get; private set; }

        public Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null)
        {
            var granted = requested with { Native = new NativeGeneration(2) };
            _state = new ControlState(granted, true, true);
            return Task.FromResult(granted);
        }

        public Task ManualAsync(CancellationToken ct) => Task.CompletedTask;

        public Task ManualForResumeAsync(CancellationToken ct) => Task.CompletedTask;

        public bool HoldsGrant(GenerationSnapshot scope) => false;

        public bool TargetsWorld(World world) => false;

        public void Disable() => _state = _state with { Enabled = false };

        public ControlState State() => _state;

        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;


        public Task ConfirmMapFocusAsync(GenerationSnapshot before, GenerationSnapshot target, NativeGeneration generation, CancellationToken ct)
        {
            Confirmed++;
            return Task.CompletedTask;
        }
    }

    /// <summary>A bundle exactly as the step read returns it: a context and no world progression.</summary>
    private static PbObservations.BundleSnapshot Bundle(int map) => new()
    {
        Context = new PbCommon.ObservationContext { Identity = new PbCommon.Identity { ColonyId = Colony, LoadToken = Load, MapId = map }, Tick = 100, NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.AtCase(1) },
    };

    /// <summary>A running control for the home map and an accepted departure of <see cref="Pawn"/> to the expedition tile.</summary>
    private static async Task<(Journal Journal, Player Player, Session Session)> RigAsync()
    {
        IntentActionRules.RegisterIntentKinds();
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-expedition-handoff-" + Guid.NewGuid().ToString("N") + ".db"), Ct);
        _ = journal.ReviewRounds(new RoundsRequest { Current = Home, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create(Pawn) }, Ct);
        var plan = new PlanSpec(new("expedition-plan"), new(1),
            [DomainAction.NewCaravanDepartureAction(new("expedition-action"), new CaravanDeparture([new(Pawn)], [], DestinationTile))]);
        _ = TestNeed.Commit(journal, Home, new("quest-expedition-q1-site1-a"), plan, Ct);
        TestNeed.File(journal, met: false, new("quest-expedition-q1-site1-a"), Ct);
        var scope = Home with { Plan = plan.Id };
        _ = journal.Prepare(plan.Id, plan.Actions[0].Id, scope, new(100), Ct);
        var dispatched = journal.Dispatch(plan.Id, plan.Actions[0].Id, scope, new(100), Ct);
        _ = journal.RecordReceipt(plan.Id, plan.Actions[0].Id, dispatched.View.Attempt, Receipt.Accepted, Ct);
        var seed = new ControlRequest("seed", ControlKind.Resume, Player.PlayerWorld(Home));
        _ = await journal.BeginControlAsync(seed, Ct);
        _ = await journal.CompleteControlAsync(seed.RequestId, ControlPhase.Running, new NativeGeneration(1), Ct);
        var session = new Session();
        var timeout = TimeSpan.FromSeconds(5);
        var player = Player.Create(new PlayerConfig(timeout, timeout), journal, session, new Worlds(), Ct);
        return (journal, player, session);
    }

    [Fact]
    public async Task HandsOffToTheExpeditionMapWhenTheCensusNamesTheCrewThereAndTheBundleCarriesNone()
    {
        var (journal, player, session) = await RigAsync();
        using var _ = journal;
        using var __ = player;
        var native = DispatchProxy.Create<IClockStepNative, Native>();

        using var entry = await player.EnterAsync("test", false, Ct);
        var handled = await player.ExpeditionIncomingHandoffAsync(Bundle(ExpeditionMap), native, entry.Call, entry.Epoch);

        Assert.True(handled);
        Assert.Equal(1, ((Native)(object)native).Reads);
        Assert.Equal(1, session.Confirmed);
        Assert.Equal(new MapId(ExpeditionMap), session.State().Snapshot.Map);
    }

    [Fact]
    public async Task ReadsNoCensusWhileTheStepStaysOnTheControlledMap()
    {
        var (journal, player, session) = await RigAsync();
        using var _ = journal;
        using var __ = player;
        var native = DispatchProxy.Create<IClockStepNative, Native>();

        using var entry = await player.EnterAsync("test", false, Ct);
        var handled = await player.ExpeditionIncomingHandoffAsync(Bundle(HomeMap), native, entry.Call, entry.Epoch);

        Assert.False(handled);
        Assert.Equal(0, ((Native)(object)native).Reads);
        Assert.Equal(0, session.Confirmed);
    }
}
