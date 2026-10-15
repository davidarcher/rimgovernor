using System.Text;
using System.Text.Json;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Store;
using Xunit;
using ExecAuthority = RimGovernor.Brain.Executor.Authority;
using JournalStore = RimGovernor.Brain.Store.Store;
using PbClock = RimGovernor.Contracts.Wire.Clock;
using PbCommon = RimGovernor.Contracts.Wire.Common;
using PbLifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The player Resume and Pause routes own the game clock (#3172). Clock ownership is implicit: the bot holds the clock
/// exactly while the player state is enabled, so Resume lets the coordinator admit an owned epoch and Pause pauses a
/// running owned epoch once and releases the clock. Everything below the routes is the real player, coordinator and
/// journal; the session and the native clock are fakes.
/// </summary>
public sealed class ResumePauseClockOwnerTests
{
    private const string Colony = "colony-1";
    private const string Load = "load-1";
    private const int Map = 1;
    private const ulong Generation = 7;
    private const long Tick = 100;

    private static readonly PbCommon.Identity WorldIdentity = new() { ColonyId = Colony, LoadToken = Load, MapId = Map };

    private static readonly PbCommon.ObservationContext Context = new() { Identity = WorldIdentity, Tick = Tick, NativeGeneration = Generation };

    private sealed class Worlds : IWorldSource
    {
        public Task<World> ReadWorldAsync(CancellationToken ct) => Task.FromResult(new World(new ColonyId(Colony), new LoadId(Load), new MapId(Map)));
    }

    /// <summary>The native clock: one game that is either paused by nobody, running under the bot's epoch, or paused by the bot.</summary>
    private sealed class FakeClock : IClockNative, IClockWriter
    {
        private PbClock.Epoch? _epoch;

        /// <summary>The player unpauses by hand; the bot is not asked and does not act.</summary>
        public void PlayerUnpauses() => Paused = false;

        public int Starts { get; private set; }

        public int OwnedPauses { get; private set; }

        public bool Paused { get; private set; } = true;

        public Task<PbLifecycle.IdentityReply> IdentityAsync(CancellationToken ct) =>
            Task.FromResult(new PbLifecycle.IdentityReply { Outcome = new PbLifecycle.IdentityReplyOutcome.LoadedCase(new PbLifecycle.LoadedIdentity { Context = Context, Paused = Paused }) });

        public Task<PbClock.StatusReply> ReadClockStatusAsync(PbCommon.Identity identity, CancellationToken ct) =>
            Task.FromResult(new PbClock.StatusReply { Outcome = new PbClock.StatusReplyOutcome.StatusCase(Status()) });

        public Task<PbClock.AttemptReply> ReadClockAttemptAsync(PbClock.AttemptRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<PbClock.ControlReply> StartAsync(PbClock.StartRequest request, CancellationToken ct)
        {
            Starts++;
            Paused = false;
            var attempt = request.Authority!.Attempt!;
            _epoch = new PbClock.Epoch
            {
                Owner = new PbClock.EpochOwner { ControllerSessionId = attempt.ControllerSessionId!, Epoch = 1 },
                Origin = Context,
                RequestedSpeed = request.Speed!.Value,
                Policy = request.Policy!,
                StartTick = Tick,
                TickDeadline = Tick + request.MaxTicks!.Value,
                LastTick = Tick,
            };
            var receipt = new PbClock.ControlReceipt
            {
                Attempt = attempt,
                AdmittedContext = Context,
                Outcome = new PbClock.ControlReceiptOutcome.AppliedCase(new PbClock.AppliedControl { Status = Status() }),
            };
            return Task.FromResult(new PbClock.ControlReply { Outcome = new PbClock.ControlReplyOutcome.ReceiptCase(receipt) });
        }

        public Task<PbClock.StatusReply> OwnedPauseAsync(PbClock.OwnedRequest request, CancellationToken ct)
        {
            OwnedPauses++;
            Paused = true;
            return ReadClockStatusAsync(WorldIdentity, ct);
        }

        public Task<PbClock.ControlReply> ChangeSpeedAsync(PbClock.SpeedRequest request, PbClock.Epoch original, CancellationToken ct) => throw new NotSupportedException();

        private PbClock.Status Status()
        {
            var status = new PbClock.Status
            {
                Context = Context,
                NativeTickBoundary = true,
                DurableEvents = true,
                NewestCursor = 0,
                ObservedSpeed = Paused ? PbClock.ObservedSpeed.Paused : PbClock.ObservedSpeed.Fast,
                ActualPaused = Paused,
            };
            if (_epoch is null)
            {
                return status with { State = new PbClock.StatusState.NeverStartedCase(new PbClock.NeverStarted()) };
            }
            if (!Paused)
            {
                return status with { State = new PbClock.StatusState.RunningCase(new PbClock.Running { Epoch = _epoch }) };
            }
            var stopped = new PbClock.Stopped
            {
                Epoch = _epoch,
                Reason = PbClock.StopReason.RequestedPause,
                ActualPaused = true,
                PauseVerified = true,
                PauseRequested = true,
                StoppedAtUnixMs = 0,
            };
            return status with { State = new PbClock.StatusState.StoppedCase(stopped) };
        }
    }

    /// <summary>
    /// The session the sink of the real one stands in for: an acquisition makes the clock coordinator's authority
    /// the grant, a Disable withdraws it, and a Manual drains the owned clock (pausing a running epoch).
    /// </summary>
    private sealed class FakeSession(ClockCoordinator clock) : IPlayerSession
    {
        private ControlState _state;

        public Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null)
        {
            var granted = requested with { Native = new NativeGeneration(Generation) };
            _state = new ControlState(granted, true, true);
            clock.UpdateAuthority(new ExecAuthority { Snapshot = granted, Enabled = true });
            return Task.FromResult(granted);
        }

        public Task ManualAsync(CancellationToken ct)
        {
            Disable();
            return clock.Cleanup(ct);
        }

        public Task ManualForResumeAsync(CancellationToken ct) => ManualAsync(ct);

        public bool HoldsGrant(GenerationSnapshot scope) => false;

        public bool TargetsWorld(World world) => _state.ObservationKnown && _state.Snapshot.Colony == world.Colony && _state.Snapshot.Load == world.Load && _state.Snapshot.Map == world.Map;

        public void Disable()
        {
            _state = _state with { Enabled = false };
            clock.UpdateAuthority(new ExecAuthority { Snapshot = _state.Snapshot, Enabled = false });
        }

        public ControlState State() => _state;

        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class PlayerControl(Player player) : IPlayerControl
    {
        public Task<ControlRecord> ResumeAsync(ControlRequest request, CancellationToken cancellationToken) => player.ResumeAsync(request, cancellationToken);

        public Task<ControlRecord> PauseAsync(ControlRequest request, CancellationToken cancellationToken) => player.PauseAsync(request, cancellationToken);

        public PlayerState State()
        {
            var state = player.State();
            return new PlayerState(state.Enabled, state.ObservationKnown, state.Snapshot);
        }
    }

    private sealed class Reader(JournalStore journal) : IControlReader
    {
        public Task<ControlRecord> CurrentControlAsync(CancellationToken cancellationToken) => journal.CurrentControlAsync(cancellationToken);
    }

    private sealed class Rig : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Dispatcher _dispatcher;
        private readonly Player _player;

        public Rig()
        {
            var path = Path.Combine(Path.GetTempPath(), "rg-resume-pause-clock-" + Guid.NewGuid().ToString("N") + ".db");
            Journal = JournalStore.Open(path, _lifetime.Token);
            var timeout = TimeSpan.FromSeconds(5);
            Coordinator = ClockCoordinator.Create(Journal, Clock, Clock, SystemClock.Instance, new ClockCoordinatorConfig { CallTimeout = timeout, JournalTimeout = timeout });
            var session = new FakeSession(Coordinator);
            _player = Player.Create(new PlayerConfig(timeout, timeout), Journal, session, new Worlds(), _lifetime.Token);
            var routes = new RouteTable();
            _ = PlayerRoutes.Register(routes, new PlayerControl(_player), new Reader(Journal), null);
            _dispatcher = new Dispatcher(routes, 1 << 20);
        }

        public JournalStore Journal { get; }

        public FakeClock Clock { get; } = new();

        public ClockCoordinator Coordinator { get; }

        public async Task<JsonElement> PostAsync(string path, string requestId)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { requestId, expected = new { colonyId = Colony, loadToken = Load, mapId = Map } });
            var head = new RequestHead { Method = "POST", Target = path, Host = "127.0.0.1:1", ContentType = "application/json", ContentLength = body.Length };
            var response = await _dispatcher.DispatchAsync(head, new Dictionary<string, string>(), (_, _) => Task.FromResult(body), _lifetime.Token);
            Assert.Equal(200, response.Status);
            return JsonDocument.Parse(response.Body).RootElement.Clone();
        }

        /// <summary>The bot's attempt to take the clock: a start the coordinator admits only under live authority.</summary>
        public async Task<Exception?> TryStartOwnedEpochAsync()
        {
            var snapshot = _player.State().Snapshot;
            var policy = BridgeFunctions.WithHazardThresholds(new PbClock.WatchPolicy
            {
                Mode = PbClock.WatchMode.Colony,
                HealthDropFraction = .1f,
                MinHealthFraction = .5f,
                HostileWithin = 20f,
                InjuryStopCooldownMs = 0,
            });
            var sequence = await Journal.ReadClockSequenceAsync(_lifetime.Token);
            var intent = new ClockIntent(sequence.NextRequestId(), "", snapshot, new ClockCommand(Start: new ClockStart(PbClock.Speed.Ultrafast, policy, 600)));
            var (attempt, error) = await Coordinator.Command(intent, _lifetime.Token);
            if (error is null)
            {
                Assert.Equal(ClockPhase.Applied, attempt!.Phase);
            }
            return error;
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            Coordinator.Stop(CancellationToken.None).GetAwaiter().GetResult();
            Coordinator.Dispose();
            _player.Dispose();
            Journal.Dispose();
            _lifetime.Dispose();
        }
    }

    [Fact]
    public async Task ResumeGivesTheBotTheClockAndPausePausesItOnceAndReleasesIt()
    {
        using var rig = new Rig();

        // Before Resume nobody owns the clock for the bot: the coordinator admits nothing.
        Assert.NotNull(await rig.TryStartOwnedEpochAsync());
        Assert.Equal(0, rig.Clock.Starts);

        var resumed = await rig.PostAsync(PlayerRoutes.ResumePath, "resume-1");
        Assert.Equal("running", resumed.GetProperty("record").GetProperty("phase").GetString());
        Assert.True(resumed.GetProperty("state").GetProperty("enabled").GetBoolean());

        // After Resume the bot owns the clock: it starts an owned epoch and the game runs.
        Assert.Null(await rig.TryStartOwnedEpochAsync());
        Assert.Equal(1, rig.Clock.Starts);
        Assert.False(rig.Clock.Paused);

        var paused = await rig.PostAsync(PlayerRoutes.PausePath, "pause-1");
        Assert.Equal("paused", paused.GetProperty("record").GetProperty("phase").GetString());
        Assert.False(paused.GetProperty("state").GetProperty("enabled").GetBoolean());

        // Pause pauses the game exactly once and hands the clock back: the bot can no longer start an epoch.
        Assert.Equal(1, rig.Clock.OwnedPauses);
        Assert.True(rig.Clock.Paused);
        Assert.NotNull(await rig.TryStartOwnedEpochAsync());
        Assert.Equal(1, rig.Clock.Starts);

        // A second Pause finds no running owned epoch and pauses nothing more.
        _ = await rig.PostAsync(PlayerRoutes.PausePath, "pause-2");
        Assert.Equal(1, rig.Clock.OwnedPauses);
    }

    [Fact]
    public async Task ManualUnpauseAfterPauseLeavesTheClockAlone()
    {
        using var rig = new Rig();
        _ = await rig.PostAsync(PlayerRoutes.ResumePath, "resume-1");
        Assert.Null(await rig.TryStartOwnedEpochAsync());
        _ = await rig.PostAsync(PlayerRoutes.PausePath, "pause-1");
        Assert.Equal(1, rig.Clock.OwnedPauses);

        // The player unpauses by hand. The bot has no authority, so nothing starts and nothing re-pauses the game.
        rig.Clock.PlayerUnpauses();
        var state = await rig.PostAsync(PlayerRoutes.PausePath, "pause-2");
        Assert.False(state.GetProperty("state").GetProperty("enabled").GetBoolean());
        Assert.False(rig.Clock.Paused);
        Assert.NotNull(await rig.TryStartOwnedEpochAsync());
        Assert.Equal(1, rig.Clock.Starts);
        Assert.Equal(1, rig.Clock.OwnedPauses);
    }
}
