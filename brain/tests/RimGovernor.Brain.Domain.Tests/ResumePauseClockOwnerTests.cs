using System.Text.Json;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Store;
using Xunit;
using ExecAuthority = RimGovernor.Brain.Executor.Authority;
using JournalStore = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The player Resume and Pause routes own the game clock (#3172). Clock ownership is implicit: the bot runs the clock
/// exactly while the player state is enabled, so Resume lets the bot Run and Pause holds the clock once with the
/// Review reason. Everything below the routes is the real player, clock holder and journal; the session and the
/// native clock are fakes.
/// </summary>
public sealed class ResumePauseClockOwnerTests
{
    private const string Colony = "colony-1";
    private const string Load = "load-1";
    private const int Map = 1;
    private const ulong Generation = 7;
    private const long Tick = 100;

    private static readonly RimGovernor.Brain.GameFacts.Common.Identity WorldIdentity = new() { ColonyId = Colony, LoadToken = Load, MapId = Map };

    private static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new() { Identity = WorldIdentity, Tick = Tick, NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(Generation) };

    private sealed class Worlds : IWorldSource
    {
        public Task<World> ReadWorldAsync(CancellationToken ct) => Task.FromResult(new World(new ColonyId(Colony), new LoadId(Load), new MapId(Map)));
    }

    /// <summary>The native clock: one game that is either held or running at the speed of the last Run; a hold of a held clock keeps its first reason.</summary>
    private sealed class FakeClock : IClockNative, IClockWriter
    {
        public int Runs { get; private set; }

        /// <summary>The holds that took effect: a hold of an already held clock does not count.</summary>
        public int Holds { get; private set; }

        public RimGovernor.Brain.GameFacts.Clock.HoldReason Reason { get; private set; } = RimGovernor.Brain.GameFacts.Clock.HoldReason.NeverRun;

        public bool Held { get; private set; } = true;

        public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> IdentityAsync(CancellationToken ct) =>
            Task.FromResult(new RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity { Context = Context, Paused = Held });

        public Task<RimGovernor.Brain.GameFacts.Clock.Status> RunAsync(RimGovernor.Brain.GameOrders.Clock.RunRequest request, CancellationToken ct)
        {
            Runs++;
            Held = false;
            return Task.FromResult(Status());
        }

        public Task<RimGovernor.Brain.GameFacts.Clock.Status> HoldAsync(RimGovernor.Brain.GameOrders.Clock.HoldRequest request, CancellationToken ct)
        {
            if (!Held)
            {
                Holds++;
                Held = true;
                Reason = request.Reason;
            }
            return Task.FromResult(Status());
        }

        public Task<RimGovernor.Brain.GameFacts.Clock.HeartbeatAck> HeartbeatAsync(RimGovernor.Brain.GameOrders.Clock.HeartbeatRequest request, CancellationToken ct) =>
            Task.FromResult(new RimGovernor.Brain.GameFacts.Clock.HeartbeatAck());

        private RimGovernor.Brain.GameFacts.Clock.Status Status() => new()
        {
            Context = Context,
            State = Held ? RimGovernor.Brain.GameFacts.Clock.ClockState.Held : RimGovernor.Brain.GameFacts.Clock.ClockState.Running,
            Hold = Held ? new RimGovernor.Brain.GameFacts.Clock.HoldState.HeldCase(Reason, 1, new RimGovernor.Brain.GameFacts.Clock.HoldTrigger.OrdinaryCase()) : new RimGovernor.Brain.GameFacts.Clock.HoldState.RunningCase(),
            LastRun = new RimGovernor.Brain.GameFacts.Clock.RunHistory.NeverRunCase(), Pacing = new RimGovernor.Brain.GameFacts.Clock.Pacing.StoppedCase(),
            PlayerRequest = new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase(), EffectiveTicksPerSecond = 0,
            NativeTickBoundary = true,
            HoldSerial = 0,
            ActualPaused = Held,
        };
    }

    /// <summary>
    /// The session the sink of the real one stands in for: an acquisition makes the clock holder's authority the
    /// grant, a Disable withdraws it, and a Manual holds the clock for review.
    /// </summary>
    private sealed class FakeSession(ClockHolder clock) : IPlayerSession
    {
        private ControlState _state;

        /// <summary>When set, Acquire throws it: the native grant failed.</summary>
        public Exception? AcquireFailure { get; set; }

        public Task<GenerationSnapshot> AcquireAsync(GenerationSnapshot requested, CancellationToken ct, NativeGeneration? handoff = null)
        {
            if (AcquireFailure is { } failure)
            {
                throw failure;
            }
            var granted = requested with { Native = new NativeGeneration(Generation) };
            _state = new ControlState(granted, true, true);
            clock.UpdateAuthority(new ExecAuthority { Snapshot = granted, Enabled = true });
            return Task.FromResult(granted);
        }

        public Task ManualAsync(CancellationToken ct)
        {
            Disable();
            return clock.HoldAsync(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, "bot_off", ct);
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
            Holder = ClockHolder.Create(Clock, timeout);
            Session = new FakeSession(Holder);
            _player = Player.Create(new PlayerConfig(timeout, timeout), Journal, Session, new Worlds(), _lifetime.Token);
            var routes = new RouteTable();
            var control = new HostPlayerControl(_player, Journal, new StepSupervisor(faultLimit: 5), new HostLog(RequiredFactSink.Emitter(), "player"));
            _ = PlayerRoutes.Register(routes, control, new Reader(Journal));
            _dispatcher = new Dispatcher(routes, 1 << 20);
        }

        public JournalStore Journal { get; }

        public FakeClock Clock { get; } = new();

        public ClockHolder Holder { get; }

        public FakeSession Session { get; }

        public async Task<JsonElement> PostAsync(string path, string requestId, int status = 200)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { requestId, expected = new { colonyId = Colony, loadToken = Load, mapId = Map } });
            var head = new RequestHead { Method = "POST", Target = path, Host = "127.0.0.1:1", ContentType = "application/json", ContentLength = body.Length };
            var response = await _dispatcher.DispatchAsync(head, new Dictionary<string, string>(), (_, _) => Task.FromResult(body), _lifetime.Token);
            Assert.Equal(status, response.Status);
            return JsonDocument.Parse(response.Body).RootElement.Clone();
        }

        /// <summary>The bot runs the clock the way the run driver does while authority is enabled.</summary>
        public async Task RunClockAsync()
        {
            Assert.True(_player.State().Enabled);
            var policy = new RimGovernor.Brain.GameOrders.Clock.WatchPolicy
            {
                SeriousSingleHitDamage = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousSingleHitDamage,
                SeriousSummaryHealthFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousSummaryHealthFloor,
                SeriousBleedRateFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousBleedRateFloor,
                SeriousVitalPartFloor = RimGovernor.Brain.Policy.HazardThresholdsRules.SeriousVitalPartFloor,
                ExplosiveNearMarginCells = RimGovernor.Brain.Policy.HazardThresholdsRules.ExplosiveNearMarginCells,
                MeleeReachCells = RimGovernor.Brain.Policy.HazardThresholdsRules.MeleeReachCells,
                InjurySeverityFloorTicks = RimGovernor.Brain.Policy.HazardThresholdsRules.InjurySeverityFloorTicks,
                HungerHoldCategory = RimGovernor.Brain.Policy.HazardThresholdsRules.HungerHoldCategory,
                Mode = RimGovernor.Brain.GameOrders.Clock.WatchMode.Colony,
                HealthDropFraction = .1f,
                MinHealthFraction = .5f,
                HostileWithin = 20f,
                InjuryStopCooldownMs = 0,
            };
            _ = await Clock.RunAsync(new RimGovernor.Brain.GameOrders.Clock.RunRequest { Identity = WorldIdentity, ExpectedGeneration = Generation, Speed = RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast, Policy = policy, PlannedHoldSerial = 0, HoldAtTick = 2500 }, _lifetime.Token);
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            Holder.Stop(CancellationToken.None).GetAwaiter().GetResult();
            Holder.Dispose();
            _player.Dispose();
            Journal.Dispose();
            _lifetime.Dispose();
        }
    }

    [Fact]
    public async Task ResumeLetsTheBotRunTheClockAndPauseHoldsItOnceForReview()
    {
        using var rig = new Rig();

        // Before Resume the clock was never run and the bot has seen no world: a Pause holds nothing.
        _ = await rig.PostAsync(PlayerRoutes.PausePath, "pause-0");
        Assert.Equal(0, rig.Clock.Holds);

        var resumed = await rig.PostAsync(PlayerRoutes.ResumePath, "resume-1");
        Assert.Equal("running", resumed.GetProperty("record").GetProperty("phase").GetString());
        Assert.True(resumed.GetProperty("state").GetProperty("enabled").GetBoolean());

        // After Resume the bot runs the clock and the game runs.
        await rig.RunClockAsync();
        Assert.Equal(1, rig.Clock.Runs);
        Assert.False(rig.Clock.Held);

        var paused = await rig.PostAsync(PlayerRoutes.PausePath, "pause-1");
        Assert.Equal("paused", paused.GetProperty("record").GetProperty("phase").GetString());
        Assert.False(paused.GetProperty("state").GetProperty("enabled").GetBoolean());

        // Pause holds the clock exactly once, for review.
        Assert.Equal(1, rig.Clock.Holds);
        Assert.True(rig.Clock.Held);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, rig.Clock.Reason);

        // A second Pause finds the clock held and keeps the first hold.
        _ = await rig.PostAsync(PlayerRoutes.PausePath, "pause-2");
        Assert.Equal(1, rig.Clock.Holds);
    }

    [Fact]
    public async Task APauseWhileTheBotNeverRanTheClockLeavesItHeldForNeverRun()
    {
        using var rig = new Rig();
        _ = await rig.PostAsync(PlayerRoutes.ResumePath, "resume-1");

        // Resume alone runs nothing: the game stays held until the bot sends Run, and a Pause keeps the first reason.
        _ = await rig.PostAsync(PlayerRoutes.PausePath, "pause-1");
        Assert.Equal(0, rig.Clock.Runs);
        Assert.Equal(0, rig.Clock.Holds);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.NeverRun, rig.Clock.Reason);
    }

    // #3549: an uncertain resume's 503 names the step that threw and its exception, not only "unresolved".
    [Fact]
    public async Task AnUncertainResumeNamesTheStepThatThrewAndItsCause()
    {
        using var rig = new Rig();
        rig.Session.AcquireFailure = new InvalidOperationException("native refused the grant");

        var reply = await rig.PostAsync(PlayerRoutes.ResumePath, "resume-1", status: 503);

        Assert.Equal("uncertain", reply.GetProperty("record").GetProperty("phase").GetString());
        var error = reply.GetProperty("error");
        Assert.Equal("uncertain", error.GetProperty("code").GetString());
        Assert.Contains("resume acquire: InvalidOperationException: native refused the grant", error.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }
}
