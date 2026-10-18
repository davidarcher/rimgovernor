using System.Reflection;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using JournalStore = RimGovernor.Brain.Store.Store;
using NativeEvents = RimGovernor.Brain.Bridge;
using PbAuthority = RimGovernor.Contracts.Wire.Authority;
using PbClock = RimGovernor.Contracts.Wire.Clock;
using PbCommon = RimGovernor.Contracts.Wire.Common;
using PbOperations = RimGovernor.Contracts.Wire.Operations;
using PbObservations = RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Executor;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The hold state machine of the clock run driver (#3203, epic #3168). The driver, session, player, scheduler and
/// journal are real; the native game is one fake that answers the facade (through the real
/// <see cref="FacadeClockWriter"/>), the driver's reads, the colony event queue and the authority. The fake models the
/// native Supervisor contract: Run resumes a held clock or changes its speed, Hold keeps the first reason, a hazard
/// takes a hold by itself, and every transition is pushed to the real <see cref="ColonyEventChannel"/> the driver's
/// consumer reads.
/// </summary>
public sealed class ClockRunDriverTests
{
    private const string Colony = "colony-1";
    private const string Load = "load-1";
    private const int Map = 1;
    private const ulong StartGeneration = 6;
    private const long StartTick = 100;
    private const string RootPlanId = "root/colony-1/load-1/1";

    private static readonly PbCommon.Identity WorldIdentity = new() { ColonyId = Colony, LoadToken = Load, MapId = Map };

    private static readonly World PlayerWorld = new(new ColonyId(Colony), new LoadId(Load), new MapId(Map));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Worlds : IWorldSource
    {
        public Task<World> ReadWorldAsync(CancellationToken ct) => Task.FromResult(PlayerWorld);
    }

    /// <summary>Every call advances the time by a millisecond, so a later reading is always strictly later.</summary>
    private sealed class TickingClock : IClock
    {
        private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

        public DateTimeOffset Now() => new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    /// <summary>An interface whose every member throws: the parts of a seam the rig never reaches.</summary>
#pragma warning disable CA1852 // DispatchProxy derives the proxy from its base, so the base cannot be sealed
    private class Unreached : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name + " is outside the clock rig");
    }

    /// <summary>The facade of the fake game: only the clock writes are answered, and they reach the game.</summary>
    private class ClockFacade : DispatchProxy
    {
        public FakeGame Game { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(INativeFacade.RunClockAsync) => Task.FromResult(Game.Run((PbClock.RunRequest)args![0]!)),
                nameof(INativeFacade.HoldClockAsync) => Task.FromResult(Game.Hold((PbClock.HoldRequest)args![0]!)),
                nameof(INativeFacade.HeartbeatClockAsync) => Task.FromResult(Game.Heartbeat()),
                _ => throw new NotSupportedException(targetMethod?.Name + " is outside the clock rig"),
            };
    }
#pragma warning restore CA1852

    /// <summary>
    /// One native game. It is held (never run) at first, authority is an Auto grant, and the clock events are pushed
    /// to a native-owned queue the driver's consumer opens.
    /// </summary>
    private sealed class FakeGame : IClockDriverNative, INativeAuthority
    {
        private readonly Lock _mu = new();
        private readonly ColonyEventChannel _channel = new();
        private int _appended;
        private long _pushed;
        private int _blockedContextReads;
        private ulong _generation = StartGeneration;
        private bool _active;
        private bool _held = true;
        private PbClock.HoldReason _reason = PbClock.HoldReason.NeverRun;
        private long _heldAtMs;
        private PbClock.Speed _speed;
        private long _tick = StartTick;
        private int _runs;
        private ulong _holdSerial;
        private readonly List<ulong> _staleSerials = [];
        private int _holds;
        private int _heartbeats;
        private readonly List<PbClock.RunRequest> _runRequests = [];
        private readonly List<PbClock.HoldRequest> _holdRequests = [];

        /// <summary>Set to refuse the next Run with this failure (a game-forced pause, a generation that moved).</summary>
        public PbCommon.FailureCode? RefuseRun { get; set; }

        /// <summary>Runs inside Hold before the game looks at its state: a hazard that fires first.</summary>
        public Action? BeforeHold { get; set; }

        /// <summary>Runs inside Run after the clock resumed: a hazard that fires as the Run lands.</summary>
        public Action? AfterResume { get; set; }

        public int Runs => Read(() => _runs);

        /// <summary>Hold requests that took effect: a Hold of an already held clock does not count.</summary>
        public int Holds => Read(() => _holds);

        public int Heartbeats => Read(() => _heartbeats);

        public bool Held => Read(() => _held);

        public PbClock.HoldReason Reason => Read(() => _reason);

        public PbClock.Speed TargetSpeed => Read(() => _speed);

        public long Tick => Read(() => _tick);

        /// <summary>Bumped on every hold entry, the brain's own included, as native's is.</summary>
        public ulong HoldSerial => Read(() => _holdSerial);

        /// <summary>The planned serials of the resumes the hold-serial guard refused.</summary>
        public IReadOnlyList<ulong> StaleSerials => Read(() => (IReadOnlyList<ulong>)[.. _staleSerials]);

        /// <summary>The events pushed since the consumer opened the queue, the dropped ones included.</summary>
        public long Pushed => Read(() => _pushed);

        public IReadOnlyList<PbClock.RunRequest> RunRequests => Read(() => (IReadOnlyList<PbClock.RunRequest>)[.. _runRequests]);

        public IReadOnlyList<PbClock.HoldRequest> HoldRequests => Read(() => (IReadOnlyList<PbClock.HoldRequest>)[.. _holdRequests]);

        /// <summary>Set to hold every bare context read (the consumer's per-batch read) until it completes.</summary>
        public TaskCompletionSource? ContextGate { get; set; }

        /// <summary>The bare context reads waiting on <see cref="ContextGate"/>.</summary>
        public int BlockedContextReads => Volatile.Read(ref _blockedContextReads);

        /// <summary>Pushes an invalidation of <paramref name="family"/>, observed in <paramref name="world"/> (this one by default).</summary>
        public void Invalidate(PbClock.FactFamily family, PbCommon.Identity? world = null)
        {
            lock (_mu)
            {
                Append(o => new NativeEvents.ObservationInvalidated(o, "", [family], "test", [], null), world);
            }
        }

        private T Read<T>(Func<T> read)
        {
            lock (_mu)
            {
                return read();
            }
        }

        public void Advance(long ticks)
        {
            lock (_mu)
            {
                _tick += ticks;
            }
        }

        private PbCommon.ObservationContext Context() => new() { Identity = WorldIdentity, Tick = _tick, NativeGeneration = _generation };

        private void Append(Func<NativeEvents.ColonyEventOrigin, NativeEvents.ColonyEvent> build, PbCommon.Identity? world = null)
        {
            _appended++;
            _pushed++;
            var identity = world ?? WorldIdentity;
            _channel.Publish(build(new NativeEvents.ColonyEventOrigin(identity.ColonyId, identity.LoadToken, identity.MapId, _tick, _tick)));
        }

        private PbClock.Status Status() => new()
        {
            Context = Context(),
            State = _held ? PbClock.ClockState.Held : PbClock.ClockState.Running,
            Reason = _held ? _reason : null,
            TargetSpeed = _speed == PbClock.Speed.Unspecified ? null : _speed,
            HeldAtUnixMs = _held ? _heldAtMs : null,
            NativeTickBoundary = true,
            HoldSerial = _holdSerial,
            ActualPaused = _held,
            CombatEvent = _held && _reason == PbClock.HoldReason.CombatEvent ? PbClock.CombatEvent.Downed : null,
        };

        /// <summary>
        /// The native Run: a stale generation, a refusal or a resume planned before the latest hold entry changes
        /// nothing; otherwise a held clock resumes and the speed is the request's.
        /// </summary>
        public PbClock.StatusReply Run(PbClock.RunRequest request)
        {
            lock (_mu)
            {
                if (RefuseRun is { } refusal)
                {
                    RefuseRun = null;
                    return Failure(refusal);
                }
                if (request.ExpectedGeneration.GetValueOrDefault() != _generation)
                {
                    return Failure(PbCommon.FailureCode.StaleGeneration);
                }
                if (_held && request.PlannedHoldSerial.GetValueOrDefault() != _holdSerial)
                {
                    _staleSerials.Add(request.PlannedHoldSerial.GetValueOrDefault());
                    return Failure(PbCommon.FailureCode.Unavailable);
                }
                _runs++;
                _runRequests.Add(request);
                _speed = request.Speed.GetValueOrDefault();
                if (_held)
                {
                    var from = _reason;
                    Append(o => new NativeEvents.Resumed(o, "", _speed, from));
                    _held = false;
                    AfterResume?.Invoke();
                }
                return new PbClock.StatusReply { Outcome = new PbClock.StatusReplyOutcome.StatusCase(Status()) };
            }
        }

        /// <summary>The native Hold: a clock already held keeps its first reason.</summary>
        public PbClock.StatusReply Hold(PbClock.HoldRequest request)
        {
            lock (_mu)
            {
                _holdRequests.Add(request);
                var before = BeforeHold;
                BeforeHold = null;
                before?.Invoke();
                if (!_held)
                {
                    _holds++;
                    TakeHold(request.Reason.GetValueOrDefault(), new NativeEvents.PauseEvidence(true, true, null, null));
                }
                return new PbClock.StatusReply { Outcome = new PbClock.StatusReplyOutcome.StatusCase(Status()) };
            }
        }

        /// <summary>The supervisor holds the clock itself on a hazard, as a native watcher does; a held clock keeps its first reason.</summary>
        public void Hazard(PbClock.HoldReason reason, NativeEvents.ColonyHoldEvidence evidence)
        {
            lock (_mu)
            {
                if (!_held)
                {
                    TakeHold(reason, evidence);
                }
            }
        }

        public void HostileHazard() =>
            Hazard(PbClock.HoldReason.Hostile, new NativeEvents.PawnEvidence(new NativeEvents.EventPawn("raider-1", "Raider", 0, 0, "hostile within range")));

        private void TakeHold(PbClock.HoldReason reason, NativeEvents.ColonyHoldEvidence evidence)
        {
            _held = true;
            _holdSerial++;
            _reason = reason;
            _heldAtMs = 1_000_000 + _appended;
            var heldAt = _heldAtMs;
            Append(o => new NativeEvents.HoldEntered(o, "", reason, evidence, o.Tick, null, heldAt, new NativeEvents.RunSegment(0, 0, 0, 0, 0)));
        }

        public PbClock.HeartbeatReply Heartbeat()
        {
            lock (_mu)
            {
                _heartbeats++;
                return new PbClock.HeartbeatReply
                {
                    Outcome = new PbClock.HeartbeatReplyOutcome.AckCase(new PbClock.HeartbeatAck()),
                };
            }
        }

        private PbClock.StatusReply Failure(PbCommon.FailureCode code) =>
            new() { Outcome = new PbClock.StatusReplyOutcome.FailureCase(new PbCommon.Failure { Code = code, Detail = "refused by the fake native", ObservedContext = Context() }) };

        public async Task<PbObservations.BundleSnapshot?> ReadStepAsync(StepRequest request, CancellationToken ct)
        {
            if (!request.ClockStatus && ContextGate is { } gate)
            {
                Interlocked.Increment(ref _blockedContextReads);
                await gate.Task.WaitAsync(ct);
            }
            lock (_mu)
            {
                return new PbObservations.BundleSnapshot { Context = Context(), ClockStatus = request.ClockStatus ? Status() : null };
            }
        }

        public Task<EmergencyObservation> ReadEmergencyAsync(PbCommon.Identity identity, CancellationToken ct)
        {
            lock (_mu)
            {
                var facts = new EmergencyFacts { ColonistsComplete = Fact.Known(true), DependentPatients = Fact.Known(EquatableArray.From<EmergencyDependentPatient>([])) };
                return Task.FromResult(new EmergencyObservation(Context(), facts));
            }
        }

        public Task<System.Threading.Channels.ChannelReader<NativeEvents.ColonyEvent>> OpenColonyEventsAsync(CancellationToken ct)
        {
            lock (_mu)
            {
                _pushed = 0;
                return Task.FromResult(_channel.Open());
            }
        }

        public long ColonyEventsLost => _channel.Lost;

        public Task<PbAuthority.StatusReply> ReadAuthorityAsync(PbCommon.Identity identity, CancellationToken ct)
        {
            lock (_mu)
            {
                PbAuthority.StatusState state = _active
                    ? new PbAuthority.ActiveAuthority { Mode = PbAuthority.Mode.Auto }
                    : new PbAuthority.InactiveAuthority { Reason = PbAuthority.RevocationReason.Manual };
                return Task.FromResult(new PbAuthority.StatusReply
                {
                    Outcome = new PbAuthority.StatusReplyOutcome.StatusCase(new PbAuthority.Status { Context = Context(), State = state }),
                });
            }
        }

        public Task<PbAuthority.ControlReply> SetModeAsync(PbAuthority.SetMode request, CancellationToken ct)
        {
            lock (_mu)
            {
                _generation++;
                _active = true;
                return Task.FromResult(new PbAuthority.ControlReply
                {
                    Outcome = new PbAuthority.ControlReplyOutcome.GrantedCase(new PbAuthority.Granted { Context = Context(), Authority = new PbAuthority.ActiveAuthority { Mode = PbAuthority.Mode.Auto } }),
                });
            }
        }

        public Task<PbAuthority.ControlReply> RevokeAsync(PbAuthority.Revoke request, CancellationToken ct)
        {
            lock (_mu)
            {
                _generation++;
                _active = false;
                return Task.FromResult(new PbAuthority.ControlReply
                {
                    Outcome = new PbAuthority.ControlReplyOutcome.RevokedCase(new PbAuthority.Revoked { Context = Context(), Authority = new PbAuthority.InactiveAuthority { Reason = request.Reason } }),
                });
            }
        }
    }

    private sealed record RigOptions
    {
        public Func<PbCommon.Identity, long, CancellationToken, Task>? Autosave { get; init; }

        public long AutosaveEveryTicks { get; init; }

        /// <summary>Gives the root plan a coupled action whose prerequisite has not completed yet.</summary>
        public bool CoupledPlan { get; init; }

        /// <summary>The signal the driver's event consumer wakes; none by default.</summary>
        public WakeSignal? Wake { get; init; }
    }

    /// <summary>The real stack over one fake game, resumed and with the run driver started.</summary>
    private sealed class Rig : IDisposable
    {
        private static readonly ActionId First = new("first");
        private static readonly ActionId Second = new("second");

        private readonly CancellationTokenSource _lifetime = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rg-clock-run-" + Guid.NewGuid().ToString("N"));
        private bool _stopped;

        private Rig(FakeGame game)
        {
            Game = game;
        }

        public FakeGame Game { get; }

        public TickingClock Time { get; } = new();

        public JournalStore Journal { get; private set; } = null!;

        public Session Session { get; private set; } = null!;

        public Player Player { get; private set; } = null!;

        public ClockScheduler Scheduler { get; private set; } = null!;

        public ClockRunDriver Driver { get; private set; } = null!;

        public ClockTargetSpeed Target { get; } = new(PbClock.Speed.Ultrafast);

        public static async Task<Rig> StartAsync(RigOptions? options = null)
        {
            options ??= new RigOptions();
            var rig = new Rig(new FakeGame());
            Directory.CreateDirectory(rig._root);
            var lifetime = rig._lifetime.Token;
            var timeout = TimeSpan.FromSeconds(10);
            rig.Journal = JournalStore.Open(Path.Combine(rig._root, "journal.db"), lifetime);
            if (options.CoupledPlan)
            {
                var root = JournalStore.RootPlanId(PlayerWorld);
                rig.Journal.CreatePlan(
                    new PlanSpec(root, new PlanRevision(1), [Wall(First, 3), Wall(Second, 4)], [new ActionDependency(Second, First, true)]),
                    lifetime);
            }
            var facade = DispatchProxy.Create<INativeFacade, ClockFacade>();
            ((ClockFacade)(object)facade).Game = rig.Game;
            var writer = new FacadeClockWriter(facade);
            rig.Session = await Session.CreateAsync(
                new SessionConfig
                {
                    Executor = new Limits { MaxAge = TimeSpan.FromSeconds(30), RunTimeout = TimeSpan.FromSeconds(30), JournalTimeout = TimeSpan.FromSeconds(30) },
                    Control = new ControlConfig { CallTimeout = timeout, StopWrites = _ => Task.CompletedTask, Worlds = new Worlds() },
                    Clock = new ClockCapabilities(DispatchProxy.Create<IClockNative, Unreached>(), writer),
                },
                rig.Journal,
                DispatchProxy.Create<IBoundaryNative, Unreached>(),
                rig.Game,
                DispatchProxy.Create<IActionsWriter, Unreached>(),
                rig.Time,
                lifetime);
            rig.Player = Player.Create(new PlayerConfig(timeout, timeout), rig.Journal, rig.Session, new Worlds(), lifetime);
            var resumed = await rig.Player.ResumeAsync(new ControlRequest("resume-1", ControlKind.Resume, PlayerWorld), Ct);
            Assert.Equal(ControlPhase.Running, resumed.Phase);
            if (options.CoupledPlan)
            {
                await rig.CompleteFirstActionAsync();
            }
            rig.Scheduler = new ClockScheduler(rig.Player, DispatchProxy.Create<IClockStepNative, Unreached>(), new ClockSchedulerConfig { MaxAge = TimeSpan.FromSeconds(30), Worker = true }, rig.Time);
            var config = new ClockRunConfig
            {
                TargetSpeed = rig.Target,
                Policy = ClockExpectation.WithHazardThresholds(new PbClock.WatchPolicy
                {
                    Mode = PbClock.WatchMode.Colony,
                    HealthDropFraction = .1f,
                    MinHealthFraction = .5f,
                    HostileWithin = 20f,
                    InjuryStopCooldownMs = 0,
                }),
                Autosave = options.Autosave,
                AutosaveEveryTicks = options.AutosaveEveryTicks,
                // The driver beats often so a test waits milliseconds for a heartbeat.
                HeartbeatInterval = TimeSpan.FromMilliseconds(10),
                CallTimeout = timeout,
            };
            rig.Driver = ClockRunDriver.Create(rig.Player, rig.Session, rig.Scheduler, rig.Game, writer, config, rig.Time, options.Wake, lifetime);
            // The consumer opens the queue on its own thread, and an open drops what was pushed before it: a test
            // that pushed first would see its event vanish, and the consumer would not know the world until a
            // later one arrived. Started means the queue is open, so every push a test makes is delivered.
            await UntilAsync(() => rig.Driver.EventsOpened);
            return rig;
        }

        private static DomainAction Wall(ActionId id, int x) =>
            DomainAction.NewBuildingAction(id, new Building("TestWall", new Cell(x, 3), Rotation.North, "Wool"), ConstructionTier.Survive);

        // Drives the prerequisite of the coupled action to Completed in the journal, so the coupled one is ready.
        private async Task CompleteFirstActionAsync()
        {
            var scope = Session.State().Snapshot;
            var plan = scope.Plan;
            var tick = new Tick(StartTick - 2);
            _ = await Journal.PrepareAsync(plan, First, scope, tick, Ct);
            var dispatched = await Journal.DispatchAsync(plan, First, scope, tick, Ct);
            _ = await Journal.RecordReceiptAsync(plan, First, dispatched.View.Attempt, Receipt.Accepted, Ct);
            // Another test in the process may have registered the kind as an intent, whose receipt already completes it.
            if (!ActionKind.Building.IntentMode())
            {
                _ = await Journal.ObserveAsync(plan, new Observation(First, dispatched.View.Attempt, scope, new Tick(StartTick - 1), Effect.Completed), scope, Ct);
            }
        }

        /// <summary>
        /// One scheduler step has finished, and the driver acts on it. The step read the game's current hold serial
        /// and the evidence the consumer had delivered by then.
        /// </summary>
        public Task<bool> StepAsync() => Driver.AfterStepAsync(new ClockSchedulerResult { HoldSerial = Game.HoldSerial }, Ct);

        /// <summary>Waits until the consumer has delivered every event the game pushed.</summary>
        public Task DeliveredAsync() => UntilAsync(() => Driver.EventsSeen >= Game.Pushed);

        public async Task StopAsync()
        {
            await Driver.StopAsync(Ct);
            _stopped = true;
        }

        public void Dispose()
        {
            try
            {
                if (!_stopped)
                {
                    Driver.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
            }
            catch (Exception)
            {
                // the test body has already reported what failed
            }
            _lifetime.Cancel();
            Driver.Dispose();
            Player.Dispose();
            Journal.Dispose();
            _lifetime.Dispose();
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A deadline that guards a hang: the condition is the state a test waits for, never a latency.</summary>
    private static async Task UntilAsync(Func<bool> condition) => await UntilAsync(() => Task.FromResult(condition()));

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            await Task.Delay(2, deadline.Token);
        }
    }

    [Fact]
    public async Task RunsAtTheTargetSpeedUnderAColonyWatchAndResendsOnlyWhenItChanges()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();

        // A clock never run is held for NeverRun: the first step Runs it at the target speed under the base colony watch.
        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
        var run = Assert.Single(rig.Game.RunRequests);
        Assert.Equal(PbClock.Speed.Ultrafast, run.Speed);
        Assert.Equal(PbClock.WatchMode.Colony, run.Policy!.Mode);
        Assert.Empty(run.Policy.AcknowledgedHostileIds);
        Assert.True(rig.Scheduler.ClockRunning);

        // Run is idempotent and doubles as the speed change: the same watch and speed are not sent again.
        Assert.False(await rig.StepAsync());
        Assert.Equal(1, rig.Game.Runs);

        rig.Target.Set(PbClock.Speed.Fast);
        Assert.True(await rig.StepAsync());
        Assert.Equal(2, rig.Game.Runs);
        Assert.Equal(PbClock.Speed.Fast, rig.Game.TargetSpeed);
        Assert.False(await rig.StepAsync());
        Assert.Equal(2, rig.Game.Runs);
        Assert.Equal(0, rig.Game.Holds);
    }

    [Fact]
    public async Task ADeferredStepNeverTouchesTheClock()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();

        Assert.False(await rig.Driver.AfterStepAsync(new ClockSchedulerResult { Deferred = true }, Ct));
        Assert.Equal(0, rig.Game.Runs);
        Assert.True(rig.Game.Held);
    }

    [Fact]
    public async Task ANativeHazardHoldResumesAtTheNextStepThatReadItsSerial()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        rig.Game.HostileHazard();
        await rig.DeliveredAsync();
        Assert.Equal(PbClock.HoldReason.Hostile, rig.Game.Reason);

        // The step read the hold's serial, so it planned against the held world and Run resumes the clock.
        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
        Assert.Equal(2, rig.Game.Runs);
        Assert.Equal(rig.Game.HoldSerial, rig.Game.RunRequests[^1].PlannedHoldSerial);
    }

    [Fact]
    public async Task ARefusedRunIsRetriedAtTheNextStepNotAFault()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();

        rig.Game.RefuseRun = PbCommon.FailureCode.NativeFailure;
        var refused = await Assert.ThrowsAsync<HeldException>(() => rig.StepAsync());
        Assert.Contains("clock run refused", refused.Message, StringComparison.Ordinal);
        Assert.True(rig.Game.Held);
        Assert.True(rig.Session.State().Enabled);

        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
        Assert.Equal(1, rig.Game.Runs);
    }

    // A step read the running clock; a hazard held it after that read and before the driver decided, and the consumer has
    // already delivered it, so the decision itself would resume. Returns the serial the step read.
    private static async Task<ulong> HeldAfterTheStepReadAsync(Rig rig)
    {
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        var stepRead = rig.Game.HoldSerial;
        rig.Game.HostileHazard();
        await rig.DeliveredAsync();
        Assert.NotEqual(stepRead, rig.Game.HoldSerial);
        return stepRead;
    }

    [Fact]
    public async Task TheRunQuotesTheStepReadsHoldSerialNotTheDecisionsLaterRead()
    {
        using var rig = await Rig.StartAsync();
        var stepRead = await HeldAfterTheStepReadAsync(rig);

        await Assert.ThrowsAsync<HeldException>(() => rig.Driver.AfterStepAsync(new ClockSchedulerResult { HoldSerial = stepRead }, Ct));
        Assert.Equal([stepRead], rig.Game.StaleSerials);
        Assert.Equal(1, rig.Game.Runs);
    }

    [Fact]
    public async Task ARunRefusedForAHoldTheStepDidNotSeeReplansFromAFreshReadNotAFault()
    {
        using var rig = await Rig.StartAsync();
        var stepRead = await HeldAfterTheStepReadAsync(rig);

        var refused = await Assert.ThrowsAsync<HeldException>(() => rig.Driver.AfterStepAsync(new ClockSchedulerResult { HoldSerial = stepRead }, Ct));
        Assert.Contains("clock run refused", refused.Message, StringComparison.Ordinal);
        Assert.True(rig.Game.Held);
        Assert.Equal(PbClock.HoldReason.Hostile, rig.Game.Reason);
        Assert.True(rig.Session.State().Enabled);

        // The next step reads the hold, so its Run quotes the current serial and resumes.
        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
        Assert.Equal(rig.Game.HoldSerial, rig.Game.RunRequests[^1].PlannedHoldSerial);
    }

    [Fact]
    public async Task AutosaveHoldsTheRunningClockOnceDueSavesWhileHeldAndRunResumes()
    {
        var saved = new List<long>();
        var heldWhenSaving = new List<bool>();
        Rig? current = null;
        using var rig = await Rig.StartAsync(new RigOptions
        {
            AutosaveEveryTicks = 500,
            Autosave = (_, tick, _) =>
            {
                saved.Add(tick);
                heldWhenSaving.Add(current!.Game.Held);
                return Task.CompletedTask;
            },
        });
        current = rig;
        await rig.DeliveredAsync();

        // The first step finds the clock held before it ever ran and Runs it without a save: only a due autosave saves.
        // The first running step only marks where the save interval starts, and a short run is not due.
        Assert.True(await rig.StepAsync());
        Assert.Empty(saved);
        rig.Game.Advance(100);
        Assert.False(await rig.StepAsync());
        Assert.Equal(0, rig.Game.Holds);

        rig.Game.Advance(500);
        Assert.True(await rig.StepAsync());
        Assert.Equal(1, rig.Game.Holds);
        Assert.Equal(PbClock.HoldReason.Autosave, rig.Game.Reason);
        Assert.Empty(saved);

        // The held step saves at the held tick, then resumes.
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        Assert.Equal([rig.Game.Tick], saved);
        Assert.False(rig.Game.Held);
        Assert.Equal(2, rig.Game.Runs);

        // The interval restarts at the hold: the next step is not due, and the one save was made with the clock held.
        Assert.False(await rig.StepAsync());
        Assert.Equal(1, rig.Game.Holds);
        Assert.Equal([true], heldWhenSaving);

        // The next interval holds and saves once more; no save ever happens at a running step.
        rig.Game.Advance(600);
        Assert.True(await rig.StepAsync());
        Assert.Single(saved);
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        Assert.Equal(2, saved.Count);
        Assert.All(heldWhenSaving, held => Assert.True(held));
        Assert.False(rig.Game.Held);
    }

    [Fact]
    public async Task OtherHoldsNeverSaveAndAHazardThatBeatsTheAutosaveHoldStillSavesWhileHeld()
    {
        var saved = new List<long>();
        using var rig = await Rig.StartAsync(new RigOptions
        {
            CoupledPlan = true,
            AutosaveEveryTicks = 500,
            Autosave = (_, tick, _) =>
            {
                saved.Add(tick);
                return Task.CompletedTask;
            },
        });
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        // The coupled-order hold is not an autosave: nothing is saved, though the clock is held and paused.
        Assert.True(await rig.StepAsync());
        Assert.Equal(PbClock.HoldReason.CoupledOrder, rig.Game.Reason);
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        Assert.Empty(saved);

        // The first running step with nothing newly coupled marks where the interval starts.
        Assert.False(await rig.StepAsync());

        // A hazard that fires as the autosave hold lands keeps its reason; the due save still runs inside that hold.
        rig.Game.Advance(600);
        rig.Game.BeforeHold = rig.Game.HostileHazard;
        Assert.True(await rig.StepAsync());
        Assert.Equal(PbClock.HoldReason.Hostile, rig.Game.Reason);
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        Assert.Equal([rig.Game.Tick], saved);
    }

    [Fact]
    public async Task AFailedAutosaveNeverKeepsTheClockFromRunning()
    {
        using var rig = await Rig.StartAsync(new RigOptions
        {
            AutosaveEveryTicks = 500,
            Autosave = (_, _, _) => throw new InvalidOperationException("disk full"),
        });
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        rig.Game.Advance(100);
        Assert.False(await rig.StepAsync());
        rig.Game.Advance(500);
        Assert.True(await rig.StepAsync());
        await rig.DeliveredAsync();

        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
    }

    [Fact]
    public async Task AReviewHoldTheBrainTookIsReleasedByTheNextRun()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        // A checkpoint save or the player's pause holds the clock for review through the session.
        await rig.Session.HoldClockAsync(PbClock.HoldReason.Review, "checkpoint", Ct);
        Assert.Equal(PbClock.HoldReason.Review, rig.Game.Reason);
        await rig.DeliveredAsync();

        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
        Assert.Equal(2, rig.Game.Runs);
    }

    [Fact]
    public async Task ACoupledOrderTakesABrainHoldOnceAndRunReleasesItWithoutHoldingAgain()
    {
        using var rig = await Rig.StartAsync(new RigOptions { CoupledPlan = true });
        await rig.DeliveredAsync();

        // The clock was held, so the first step Runs it; the coupled order is then newly ready under the running clock.
        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);

        Assert.True(await rig.StepAsync());
        Assert.True(rig.Game.Held);
        Assert.Equal(PbClock.HoldReason.CoupledOrder, rig.Game.Reason);
        Assert.Equal(1, rig.Game.Holds);

        // The step the hold asked for has planned against the held world: Run resumes, and the same order does not hold again.
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
        Assert.Equal(2, rig.Game.Runs);
        Assert.False(await rig.StepAsync());
        Assert.Equal(1, rig.Game.Holds);
    }

    [Fact]
    public async Task TheFirstHoldReasonWinsWhenTheBrainHoldsAClockNativeAlreadyHeld()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        rig.Game.HostileHazard();
        await rig.Session.HoldClockAsync(PbClock.HoldReason.Review, "checkpoint", Ct);

        // The brain's hold was a success that changed nothing: the hazard's reason stands and no second hold took.
        Assert.Equal(PbClock.HoldReason.Hostile, rig.Game.Reason);
        Assert.Equal(0, rig.Game.Holds);
        Assert.Single(rig.Game.HoldRequests);

        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
    }

    [Fact]
    public async Task AHazardThatFiresBeforeTheBrainsAutosaveHoldKeepsItsReason()
    {
        using var rig = await Rig.StartAsync(new RigOptions { AutosaveEveryTicks = 500, Autosave = (_, _, _) => Task.CompletedTask });
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        rig.Game.Advance(100);
        Assert.False(await rig.StepAsync());
        rig.Game.Advance(500);

        // A step that read the hold serial before the hazard is refused. The hazard fires inside the next step, so the
        // serial read before that step predates it, whenever the consumer delivers it.
        var earlySerial = rig.Game.HoldSerial;
        rig.Game.BeforeHold = rig.Game.HostileHazard;
        Assert.True(await rig.StepAsync());
        Assert.Equal(PbClock.HoldReason.Hostile, rig.Game.Reason);
        Assert.Equal(0, rig.Game.Holds);

        await rig.DeliveredAsync();
        await Assert.ThrowsAsync<HeldException>(() => rig.Driver.AfterStepAsync(new ClockSchedulerResult { HoldSerial = earlySerial }, Ct));
        Assert.True(rig.Game.Held);
        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
    }

    [Fact]
    public async Task ARunLandingWhileANewHazardFiresLeavesTheClockHeldUntilTheNextStep()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        rig.Game.HostileHazard();
        await rig.DeliveredAsync();

        // The Run resumes the clock and a second hazard holds it again before the driver sees the reply.
        rig.Game.AfterResume = () =>
        {
            rig.Game.AfterResume = null;
            rig.Game.Hazard(PbClock.HoldReason.ColonistDowned, new NativeEvents.PawnEvidence(new NativeEvents.EventPawn("colonist-1", "Ann", 0, 0, "downed")));
        };
        Assert.True(await rig.StepAsync());
        Assert.True(rig.Game.Held);
        Assert.Equal(PbClock.HoldReason.ColonistDowned, rig.Game.Reason);
        Assert.False(rig.Scheduler.ClockRunning);
        Assert.Equal(2, rig.Game.Runs);

        // The new hold leaves the clock held until a step plans against it.
        await rig.DeliveredAsync();
        Assert.Equal(PbClock.HoldReason.ColonistDowned, rig.Game.Reason);

        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
        Assert.Equal(3, rig.Game.Runs);
    }

    [Fact]
    public async Task TheHeartbeatIsSentWhileRunningAndWhileHeld()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();

        // The heartbeat starts once the consumer knows the world, held or not.
        await UntilAsync(() => rig.Game.Heartbeats >= 2);
        Assert.True(rig.Game.Held);

        Assert.True(await rig.StepAsync());
        var running = rig.Game.Heartbeats;
        await UntilAsync(() => rig.Game.Heartbeats >= running + 2);
        Assert.False(rig.Game.Held);

        rig.Game.HostileHazard();
        var held = rig.Game.Heartbeats;
        await UntilAsync(() => rig.Game.Heartbeats >= held + 2);
        Assert.True(rig.Game.Held);
    }

    [Fact]
    public async Task LosingAuthorityUnderARunHoldsTheClockOnceFromTheHeartbeatLoop()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        rig.Session.Disable();
        await UntilAsync(() => rig.Game.Held);
        Assert.Equal(PbClock.HoldReason.Review, rig.Game.Reason);
        var hold = Assert.Single(rig.Game.HoldRequests);
        Assert.Equal("no_authority", hold.Detail);

        // Authority is gone: the loop stops beating and holds nothing more.
        var beats = rig.Game.Heartbeats;
        await Task.Delay(100, Ct);
        Assert.Equal(beats, rig.Game.Heartbeats);
        Assert.Single(rig.Game.HoldRequests);
    }

    [Fact]
    public async Task LosingAuthorityBeforeTheDriverEverRanHoldsNothing()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();

        rig.Session.Disable();
        await Task.Delay(100, Ct);
        Assert.Empty(rig.Game.HoldRequests);
        Assert.Equal(0, rig.Game.Runs);
    }

    [Fact]
    public async Task StoppingHoldsTheClockForReviewAndDropsAuthority()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        await rig.StopAsync();
        Assert.True(rig.Game.Held);
        Assert.Equal(PbClock.HoldReason.Review, rig.Game.Reason);
        Assert.False(rig.Session.State().Enabled);
        Assert.Equal("stop", Assert.Single(rig.Game.HoldRequests).Detail);
    }

    [Fact]
    public async Task ACombatEventHoldIsMarkedForTheDefensePlannerOnceBeforeRunResumes()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        // An admitted fight plan owns the combat: the driver's combat-stop metrics are active. The fight itself is the
        // rounds journal's state, which the unit rig does not build, so the metrics are set to what an admitted combat Run leaves.
        var metrics = (CombatStopMetrics)typeof(ClockRunDriver).GetField("_combatStops", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Driver)!;
        metrics.Admitted(null, rig.Game.Tick, rig.Time.Now(), true);
        Assert.True(metrics.Active);

        rig.Game.Hazard(PbClock.HoldReason.CombatEvent, new NativeEvents.CombatEvidence(PbClock.CombatEvent.Downed, "colonist-1", "downed"));
        await rig.DeliveredAsync();

        // The stop is the fight's next decision: the defense planner is marked and the clock stays held.
        var marks = rig.Scheduler.Queue.Seq;
        Assert.True(await rig.StepAsync());
        Assert.Equal(marks + 1, rig.Scheduler.Queue.Seq);
        Assert.True(rig.Game.Held);
        Assert.Equal(1, rig.Game.Runs);

        // The same stop is marked once; the next step has planned against the held world and Run resumes.
        Assert.True(await rig.StepAsync());
        Assert.Equal(marks + 1, rig.Scheduler.Queue.Seq);
        Assert.False(rig.Game.Held);
        Assert.Equal(2, rig.Game.Runs);
    }

    [Fact]
    public async Task ACombatEventHoldWithNoOpenFightResumesAtOnce()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        rig.Game.Hazard(PbClock.HoldReason.CombatEvent, new NativeEvents.CombatEvidence(PbClock.CombatEvent.Downed, null, "downed"));
        await rig.DeliveredAsync();

        var marks = rig.Scheduler.Queue.Seq;
        Assert.True(await rig.StepAsync());
        Assert.Equal(marks, rig.Scheduler.Queue.Seq);
        Assert.False(rig.Game.Held);
    }

    [Fact]
    public async Task ALongRunWithNoHoldsStaysRunningAndSendsRunOnce()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        for (var i = 0; i < 300; i++)
        {
            rig.Game.Advance(250);
            Assert.False(await rig.StepAsync());
        }

        Assert.False(rig.Game.Held);
        Assert.True(rig.Scheduler.ClockRunning);
        Assert.Equal(1, rig.Game.Runs);
        Assert.Equal(0, rig.Game.Holds);
        Assert.Single(rig.Game.RunRequests);
        Assert.Empty(rig.Game.HoldRequests);
        Assert.True(rig.Session.State().Enabled);
        await UntilAsync(() => rig.Game.Heartbeats > 0);
    }

    [Fact]
    public void AnOpenFightArmsOnlyTheDecisionEvents()
    {
        Assert.Equivalent(
            new[] { PbClock.CombatEvent.HostileArrived, PbClock.CombatEvent.Downed, PbClock.CombatEvent.SeriousInjury },
            ClockRunWork.ArmedStops(true));
        Assert.Empty(ClockRunWork.ArmedStops(false));
    }

    [Fact]
    public void TheFallbackSpeedLowersTheTargetOnlyWhileTheGateIsTripped()
    {
        Assert.Equal(PbClock.Speed.Ultrafast, ClockCombatStops.SpeedFor(PbClock.Speed.Ultrafast, PbClock.Speed.Normal, false));
        Assert.Equal(PbClock.Speed.Normal, ClockCombatStops.SpeedFor(PbClock.Speed.Ultrafast, PbClock.Speed.Normal, true));
        Assert.Equal(PbClock.Speed.Normal, ClockCombatStops.SpeedFor(PbClock.Speed.Normal, PbClock.Speed.Fast, true));
    }

    private static PbClock.Status HeldAt(DateTimeOffset at) =>
        new() { State = PbClock.ClockState.Held, Reason = PbClock.HoldReason.CombatEvent, CombatEvent = PbClock.CombatEvent.Downed, HeldAtUnixMs = at.ToUnixTimeMilliseconds() };

    [Fact]
    public void TheResumeLatencyGateTripsOnASlowCombatResumeAndClearsWhenTheCombatEnds()
    {
        var metrics = new CombatStopMetrics(TimeSpan.FromSeconds(1));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.False(metrics.Tripped);
        metrics.Admitted(null, 100, t0, true);
        Assert.False(metrics.Tripped);

        // A resume inside the budget leaves the gate open.
        metrics.Admitted(HeldAt(t0), 110, t0 + TimeSpan.FromMilliseconds(400), true);
        Assert.False(metrics.Tripped);

        // One over the budget trips it for the rest of the combat.
        metrics.Admitted(HeldAt(t0), 120, t0 + TimeSpan.FromMilliseconds(1200), true);
        Assert.True(metrics.Tripped);
        metrics.Admitted(HeldAt(t0), 130, t0 + TimeSpan.FromMilliseconds(50), true);
        Assert.True(metrics.Tripped);

        // The colony run that ends the combat clears it.
        metrics.Admitted(HeldAt(t0), 140, t0 + TimeSpan.FromMilliseconds(50), false);
        Assert.False(metrics.Tripped);
        Assert.False(metrics.Active);
    }

    [Fact]
    public void AnotherLoadedWorldStartsTheCombatStopMetricsAfresh()
    {
        var metrics = new CombatStopMetrics(TimeSpan.FromSeconds(1));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        metrics.Admitted(null, 100, t0, true);
        metrics.Admitted(HeldAt(t0), 120, t0 + TimeSpan.FromMilliseconds(1200), true);
        Assert.True(metrics.Tripped);

        // A sweep parks each seed mid-fight and loads the next lab: no colony run ever closed the combat.
        metrics.Reset();
        Assert.False(metrics.Tripped);
        Assert.False(metrics.Active);
    }

    [Fact]
    public async Task ASlowCombatResumeWithNoOpenFightKeepsTheTargetSpeed()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        var metrics = (CombatStopMetrics)typeof(ClockRunDriver).GetField("_combatStops", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Driver)!;
        metrics.Admitted(null, rig.Game.Tick, rig.Time.Now(), true);
        rig.Game.Hazard(PbClock.HoldReason.CombatEvent, new NativeEvents.CombatEvidence(PbClock.CombatEvent.Downed, null, "downed"));
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);

        // The fake stamps its holds in 1970, so the resume is far over any budget; with no fight open the fallback
        // does not apply (and the colony Run closes the combat) and the Run keeps the target speed.
        Assert.False(metrics.Tripped);
        Assert.Equal(PbClock.Speed.Ultrafast, rig.Game.TargetSpeed);
    }

    [Fact]
    public async Task APushedInvalidationWakesItsFamilyAndSections()
    {
        var wake = new WakeSignal();
        using var rig = await Rig.StartAsync(new RigOptions { Wake = wake });
        await rig.DeliveredAsync();
        _ = wake.TakeInvalidated();

        rig.Game.Invalidate(PbClock.FactFamily.Research);
        await rig.DeliveredAsync();

        var reason = wake.TakeInvalidated();
        Assert.Equal(StepCause.Wake, reason.Cause);
        Assert.Equal(new[] { FactFamily.Research }, reason.Families);
        Assert.NotEmpty(reason.Sections);
    }

    [Fact]
    public async Task AnEventFromAnotherWorldIsIgnored()
    {
        var wake = new WakeSignal();
        using var rig = await Rig.StartAsync(new RigOptions { Wake = wake });
        await rig.DeliveredAsync();
        _ = wake.TakeInvalidated();
        var before = rig.Driver.EventsSeen;

        // The other world's event is pushed first: once this world's has arrived, the other one has been read too.
        rig.Game.Invalidate(PbClock.FactFamily.Research, new PbCommon.Identity { ColonyId = Colony, LoadToken = "load-2", MapId = Map });
        rig.Game.Invalidate(PbClock.FactFamily.Pawns);
        await UntilAsync(() => rig.Driver.EventsSeen > before);

        Assert.Equal(before + 1, rig.Driver.EventsSeen);
        Assert.Equal(new[] { FactFamily.Pawns }, wake.TakeInvalidated().Families);
    }

    [Fact]
    public async Task AFullQueueCountsTheLossAndTheNextStepPlansFull()
    {
        var wake = new WakeSignal();
        using var rig = await Rig.StartAsync(new RigOptions { Wake = wake });
        // One delivered event lets the consumer know the world, so only its own batch read waits on the gate below.
        rig.Game.Invalidate(PbClock.FactFamily.Colony);
        await rig.DeliveredAsync();
        _ = wake.TakeInvalidated();
        var before = rig.Driver.EventsSeen;

        // The consumer holds one event while its context read waits; native fills the queue and drops one more.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Game.ContextGate = gate;
        rig.Game.Invalidate(PbClock.FactFamily.Colony);
        await UntilAsync(() => rig.Game.BlockedContextReads > 0);
        for (var i = 0; i <= ColonyEventChannel.Capacity; i++)
        {
            rig.Game.Invalidate(PbClock.FactFamily.Colony);
        }
        Assert.Equal(1, rig.Game.ColonyEventsLost);
        rig.Game.ContextGate = null;
        gate.SetResult();

        // Every queued event and the one loss are delivered; the loss makes the next step's reason Full, once.
        await UntilAsync(() => rig.Driver.EventsSeen >= before + 1 + ColonyEventChannel.Capacity + 1);
        Assert.Equal(StepCause.Full, wake.TakeInvalidated().Cause);
        Assert.Equal(StepCause.Wake, wake.TakeInvalidated().Cause);
    }

    [Fact]
    public async Task AHeldClockWithEventsSinceTheStepReadResumesOnTheCurrentSerial()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        rig.Game.HostileHazard();
        await rig.DeliveredAsync();
        var stepRead = rig.Game.HoldSerial;

        // An invalidation arrives after the step read the held clock. It is not a hold, so the serial the step read is
        // current and the Run takes: the hold serial is the one stale-read guard (#3518, two steps per hold before it).
        rig.Game.Invalidate(PbClock.FactFamily.Colony);
        await rig.DeliveredAsync();
        Assert.True(await rig.Driver.AfterStepAsync(new ClockSchedulerResult { HoldSerial = stepRead }, Ct));
        Assert.False(rig.Game.Held);
        Assert.Equal(2, rig.Game.Runs);
    }

    [Fact]
    public async Task TheColonyEventChannelDropsAndCountsPastItsCapacityAndOpensEmpty()
    {
        var @event = new NativeEvents.HostilesCleared(new NativeEvents.ColonyEventOrigin(Colony, Load, Map, StartTick, 0), "");
        var channel = new ColonyEventChannel();
        var reader = channel.Open();
        for (var i = 0; i <= ColonyEventChannel.Capacity; i++)
        {
            channel.Publish(@event);
        }
        Assert.Equal(1, channel.Lost);

        // A reopen (a restarted or reloaded brain) begins with no backlog and no loss.
        reader = channel.Open();
        Assert.False(reader.TryRead(out _));
        Assert.Equal(0, channel.Lost);
        channel.Publish(@event);
        Assert.True(await reader.WaitToReadAsync(Ct));
        Assert.True(reader.TryRead(out _));
    }

    [Fact]
    public async Task AnOpenCompletesThePreviousReadersWaitAndNativeKeepsNothingOfIt()
    {
        // An unloaded brain's read waits with no write coming: native's queue holds its continuation.
        var channel = new ColonyEventChannel();
        var (reader, waiter, woke) = PendingWait(channel, CancellationToken.None);
        Collect();
        Assert.True(waiter.IsAlive);

        // The next brain's open completes that wait (shutdown, not data) and native keeps neither the waiter nor the old queue.
        var fresh = channel.Open();
        Assert.False(await woke.WaitAsync(Ct));
        Assert.True(await Released(waiter));
        Assert.True(await Released(reader));

        // A write after the open reaches only the new reader.
        channel.Publish(new NativeEvents.HostilesCleared(new NativeEvents.ColonyEventOrigin(Colony, Load, Map, StartTick, 0), ""));
        Assert.True(fresh.TryRead(out _));
        Assert.Equal(0, channel.Lost);
    }

    [Fact]
    public async Task AStoppedBrainsCancelledWaitLeavesTheQueueWithoutAWriteOrAnOpen()
    {
        // The brain stops by cancelling its read; System.Threading.Channels drops the waiter then, so stop needs no close.
        var channel = new ColonyEventChannel();
        using var stop = new CancellationTokenSource();
        var (_, waiter, woke) = PendingWait(channel, stop.Token);
        await stop.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => woke.WaitAsync(Ct));
        Assert.True(await Released(waiter));
    }

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    // Collects until the target is gone. The waiter's continuation may still be unwinding on a pool thread after it set
    // Woke, holding the waiter on its stack, so one collect can race it under load; the deadline is a hang guard.
    private static async Task<bool> Released(WeakReference target)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            Collect();
            if (!target.IsAlive)
            {
                return true;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            await Task.Delay(10, Ct);
        }
    }

    // Opens the channel and starts one read wait whose continuation target is only reachable through the channel.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Reader, WeakReference Waiter, Task<bool> Woke) PendingWait(ColonyEventChannel channel, CancellationToken ct)
    {
        var reader = channel.Open();
        var waiter = new ReadWaiter(reader.WaitToReadAsync(ct));
        return (new WeakReference(reader), new WeakReference(waiter), waiter.Woke.Task);
    }

    private sealed class ReadWaiter
    {
        private readonly System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<bool>.ConfiguredValueTaskAwaiter _awaiter;

        public ReadWaiter(ValueTask<bool> wait)
        {
            _awaiter = wait.ConfigureAwait(false).GetAwaiter();
            _awaiter.OnCompleted(Continue);
        }

        public TaskCompletionSource<bool> Woke { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Continue()
        {
            try
            {
                Woke.SetResult(_awaiter.GetResult());
            }
            catch (Exception e)
            {
                Woke.SetException(e);
            }
        }
    }
}
