using System.Reflection;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using JournalStore = RimGovernor.Brain.Store.Store;
using NativeEvents = RimGovernor.Brain.Bridge;
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
public sealed class PlannerRunDriverTests
{
    private const string Colony = "colony-1";
    private const string Load = "load-1";
    private const int Map = 1;
    private const ulong StartGeneration = 6;
    private const long StartTick = 100;
    private const string RootPlanId = "root/colony-1/load-1/1";

    private static readonly RimGovernor.Brain.GameFacts.Common.Identity WorldIdentity = new() { ColonyId = Colony, LoadToken = Load, MapId = Map };

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
                nameof(IGameApi.RunClockAsync) => Answer(() => Game.Run((RimGovernor.Brain.GameOrders.Clock.RunRequest)args![0]!)),
                nameof(IGameApi.HoldClockAsync) => Answer(() => Game.Hold((RimGovernor.Brain.GameOrders.Clock.HoldRequest)args![0]!)),
                nameof(IGameApi.HeartbeatClockAsync) => Answer(Game.Heartbeat),
                _ => throw new NotSupportedException(targetMethod?.Name + " is outside the clock rig"),
            };

        /// <summary>The game's answer as the facade's task: a refusal is a faulted task, as the facade throws it.</summary>
        private static Task<T> Answer<T>(Func<T> answer)
        {
            try
            {
                return Task.FromResult(answer());
            }
            catch (NativeFailureException e)
            {
                return Task.FromException<T>(e);
            }
        }
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
        private int _contextTimeouts;
        private int _timedOutContextReads;
        private ulong _generation = StartGeneration;
        private bool _active;
        private bool _held = true;
        private RimGovernor.Brain.GameFacts.Clock.HoldReason _reason = RimGovernor.Brain.GameFacts.Clock.HoldReason.NeverRun;
        private long _heldAtMs;
        private RimGovernor.Brain.GameFacts.Clock.Speed _speed;
        private long _tick = StartTick;
        private long _holdAt = long.MaxValue;
        private int _runs;
        private ulong _holdSerial;
        private RimGovernor.Brain.GameFacts.Clock.Speed _requestedSpeed;
        private ulong _requestSerial;
        private readonly List<ulong> _staleSerials = [];
        private int _holds;
        private int _heartbeats;
        private readonly List<RimGovernor.Brain.GameOrders.Clock.RunRequest> _runRequests = [];
        private readonly List<RimGovernor.Brain.GameOrders.Clock.HoldRequest> _holdRequests = [];

        /// <summary>Set to refuse the next Run with this failure (a game-forced pause, a generation that moved).</summary>
        public RimGovernor.Brain.GameFacts.Common.FailureCode? RefuseRun { get; set; }

        /// <summary>Runs inside Hold before the game looks at its state: a hazard that fires first.</summary>
        public Action? BeforeHold { get; set; }

        /// <summary>Runs inside Run after the clock resumed: a hazard that fires as the Run lands.</summary>
        public Action? AfterResume { get; set; }

        public int Runs => Read(() => _runs);

        /// <summary>Hold requests that took effect: a Hold of an already held clock does not count.</summary>
        public int Holds => Read(() => _holds);

        public int Heartbeats => Read(() => _heartbeats);

        public bool Held => Read(() => _held);

        public RimGovernor.Brain.GameFacts.Clock.HoldReason Reason => Read(() => _reason);

        public RimGovernor.Brain.GameFacts.Clock.Speed TargetSpeed => Read(() => _speed);

        public long Tick => Read(() => _tick);

        /// <summary>Bumped on every hold entry, the brain's own included, as native's is.</summary>
        public ulong HoldSerial => Read(() => _holdSerial);

        /// <summary>The planned serials of the resumes the hold-serial guard refused.</summary>
        public IReadOnlyList<ulong> StaleSerials => Read(() => (IReadOnlyList<ulong>)[.. _staleSerials]);

        /// <summary>The events pushed since the consumer opened the queue, the dropped ones included.</summary>
        public long Pushed => Read(() => _pushed);

        public IReadOnlyList<RimGovernor.Brain.GameOrders.Clock.RunRequest> RunRequests => Read(() => (IReadOnlyList<RimGovernor.Brain.GameOrders.Clock.RunRequest>)[.. _runRequests]);

        public IReadOnlyList<RimGovernor.Brain.GameOrders.Clock.HoldRequest> HoldRequests => Read(() => (IReadOnlyList<RimGovernor.Brain.GameOrders.Clock.HoldRequest>)[.. _holdRequests]);

        /// <summary>Set to hold every bare context read (the consumer's per-batch read) until it completes.</summary>
        public TaskCompletionSource? ContextGate { get; set; }

        /// <summary>The bare context reads waiting on <see cref="ContextGate"/>.</summary>
        public int BlockedContextReads => Volatile.Read(ref _blockedContextReads);

        /// <summary>How many of the next bare context reads time out, as a read the call bound cancelled does.</summary>
        public int ContextTimeouts { get => Volatile.Read(ref _contextTimeouts); set => Volatile.Write(ref _contextTimeouts, value); }

        /// <summary>The bare context reads that timed out.</summary>
        public int TimedOutContextReads => Volatile.Read(ref _timedOutContextReads);

        /// <summary>
        /// The player asks for a speed in game, as native's RequestSpeed: the speed and a bumped serial go on Status,
        /// and SpeedRequested wakes the brain unless <paramref name="publish"/> is false (a lost event).
        /// </summary>
        public void RequestSpeed(RimGovernor.Brain.GameFacts.Clock.Speed speed, bool publish = true)
        {
            lock (_mu)
            {
                _requestedSpeed = speed;
                _requestSerial++;
                if (publish)
                {
                    Append(o => new NativeEvents.SpeedRequested(o, "", speed));
                }
            }
        }

        /// <summary>Pushes an invalidation of <paramref name="family"/>, observed in <paramref name="world"/> (this one by default).</summary>
        public void Invalidate(RimGovernor.Brain.GameFacts.Clock.FactFamily family, RimGovernor.Brain.GameFacts.Common.Identity? world = null)
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

        /// <summary>Moves the game clock; a running clock that reaches the review tick its Run named holds for Review, as native's tick body does.</summary>
        public void Advance(long ticks)
        {
            lock (_mu)
            {
                _tick += ticks;
                if (!_held && _tick >= _holdAt)
                {
                    TakeHold(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, new NativeEvents.PauseEvidence(true, true, null, null));
                }
            }
        }

        private RimGovernor.Brain.GameFacts.Common.ObservationContext Context() => new() { Identity = WorldIdentity, Tick = _tick, NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(_generation) };

        private void Append(Func<NativeEvents.ColonyEventOrigin, NativeEvents.ColonyEvent> build, RimGovernor.Brain.GameFacts.Common.Identity? world = null)
        {
            _appended++;
            _pushed++;
            var identity = world ?? WorldIdentity;
            _channel.Publish(build(new NativeEvents.ColonyEventOrigin(identity.ColonyId, identity.LoadToken, identity.MapId, _tick, _tick)));
        }

        private RimGovernor.Brain.GameFacts.Clock.Status Status() => new()
        {
            Context = Context(),
            State = _held ? RimGovernor.Brain.GameFacts.Clock.ClockState.Held : RimGovernor.Brain.GameFacts.Clock.ClockState.Running,
            Hold = !_held ? new RimGovernor.Brain.GameFacts.Clock.HoldState.RunningCase()
                : _reason == RimGovernor.Brain.GameFacts.Clock.HoldReason.NeverRun ? new RimGovernor.Brain.GameFacts.Clock.HoldState.NeverRunCase()
                : new RimGovernor.Brain.GameFacts.Clock.HoldState.HeldCase(_reason, _heldAtMs,
                    _reason == RimGovernor.Brain.GameFacts.Clock.HoldReason.CombatEvent ? new RimGovernor.Brain.GameFacts.Clock.HoldTrigger.CombatCase(RimGovernor.Brain.GameFacts.Clock.CombatEvent.Downed) : new RimGovernor.Brain.GameFacts.Clock.HoldTrigger.OrdinaryCase()),
            LastRun = _speed == 0 ? new RimGovernor.Brain.GameFacts.Clock.RunHistory.NeverRunCase() : new RimGovernor.Brain.GameFacts.Clock.RunHistory.RanCase(_speed),
            Pacing = _held ? new RimGovernor.Brain.GameFacts.Clock.Pacing.StoppedCase() : new RimGovernor.Brain.GameFacts.Clock.Pacing.RunningCase(60, RimGovernor.Brain.GameFacts.Clock.PacingReason.Fixed, 0),
            EffectiveTicksPerSecond = _held ? 0 : 60,
            NativeTickBoundary = true,
            HoldSerial = _holdSerial,
            PlayerRequest = _requestSerial == 0 ? new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase() : new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.RequestedCase(_requestedSpeed, _requestSerial),
            ActualPaused = _held,
        };

        /// <summary>
        /// The native Run: a stale generation, a refusal or a resume planned before the latest hold entry changes
        /// nothing; otherwise a held clock resumes and the speed is the request's.
        /// </summary>
        public RimGovernor.Brain.GameFacts.Clock.Status Run(RimGovernor.Brain.GameOrders.Clock.RunRequest request)
        {
            lock (_mu)
            {
                if (RefuseRun is { } refusal)
                {
                    RefuseRun = null;
                    throw Failure(refusal);
                }
                if (request.ExpectedGeneration != _generation)
                {
                    throw Failure(RimGovernor.Brain.GameFacts.Common.FailureCode.StaleGeneration);
                }
                if (_held && request.PlannedHoldSerial != _holdSerial)
                {
                    _staleSerials.Add(request.PlannedHoldSerial);
                    throw Failure(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable);
                }
                _runs++;
                _runRequests.Add(request);
                _speed = request.Speed;
                _holdAt = (long)Math.Min(request.HoldAtTick, long.MaxValue);
                if (_held)
                {
                    var from = _reason;
                    Append(o => new NativeEvents.Resumed(o, "", _speed, from));
                    _held = false;
                    AfterResume?.Invoke();
                }
                return Status();
            }
        }

        /// <summary>The native Hold: a clock already held keeps its first reason.</summary>
        public RimGovernor.Brain.GameFacts.Clock.Status Hold(RimGovernor.Brain.GameOrders.Clock.HoldRequest request)
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
                    TakeHold(request.Reason, new NativeEvents.PauseEvidence(true, true, null, null));
                }
                return Status();
            }
        }

        /// <summary>The supervisor holds the clock itself on a hazard, as a native watcher does; a held clock keeps its first reason.</summary>
        public void Hazard(RimGovernor.Brain.GameFacts.Clock.HoldReason reason, NativeEvents.ColonyHoldEvidence evidence)
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
            Hazard(RimGovernor.Brain.GameFacts.Clock.HoldReason.Hostile, new NativeEvents.PawnEvidence(new NativeEvents.EventPawn("raider-1", "Raider", 0, 0, "hostile within range")));

        private void TakeHold(RimGovernor.Brain.GameFacts.Clock.HoldReason reason, NativeEvents.ColonyHoldEvidence evidence)
        {
            _held = true;
            _holdSerial++;
            _reason = reason;
            _heldAtMs = 1_000_000 + _appended;
            var heldAt = _heldAtMs;
            Append(o => new NativeEvents.HoldEntered(o, "", reason, evidence, o.Tick, null, heldAt, new NativeEvents.RunSegment(0, 0, 0, 0, 0)));
        }

        public RimGovernor.Brain.GameFacts.Clock.HeartbeatAck Heartbeat()
        {
            lock (_mu)
            {
                _heartbeats++;
                return new RimGovernor.Brain.GameFacts.Clock.HeartbeatAck();
            }
        }

        private NativeFailureException Failure(RimGovernor.Brain.GameFacts.Common.FailureCode code) =>
            NativeFaults.Refused(new RimGovernor.Brain.GameFacts.Common.Failure { RefusalClass = RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified, Code = code, Detail = "refused by the fake native", ObservedContext = Context() });

        private bool TakeContextTimeout()
        {
            int left;
            do
            {
                left = Volatile.Read(ref _contextTimeouts);
                if (left <= 0)
                {
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _contextTimeouts, left - 1, left) != left);
            return true;
        }

        public async Task<RimGovernor.Brain.GameFacts.Observations.BundleSnapshot?> ReadStepAsync(StepRequest request, CancellationToken ct)
        {
            if (!request.ClockStatus && TakeContextTimeout())
            {
                Interlocked.Increment(ref _timedOutContextReads);
                throw new TaskCanceledException("the fake context read timed out");
            }
            if (!request.ClockStatus && ContextGate is { } gate)
            {
                Interlocked.Increment(ref _blockedContextReads);
                await gate.Task.WaitAsync(ct);
            }
            lock (_mu)
            {
                return new RimGovernor.Brain.GameFacts.Observations.BundleSnapshot { Context = Context(), ClockStatus = request.ClockStatus ? Status() : null };
            }
        }

        public Task<EmergencyObservation> ReadEmergencyAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct)
        {
            lock (_mu)
            {
                var facts = new EmergencyFacts();
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

        public Task<RimGovernor.Brain.GameFacts.Authority.Status> ReadAuthorityAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct)
        {
            lock (_mu)
            {
                RimGovernor.Brain.GameFacts.Authority.StatusState state = _active
                    ? new RimGovernor.Brain.GameFacts.Authority.ActiveAuthority { Mode = RimGovernor.Brain.GameFacts.Authority.Mode.Auto }
                    : new RimGovernor.Brain.GameFacts.Authority.InactiveAuthority { Reason = RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual };
                return Task.FromResult(new RimGovernor.Brain.GameFacts.Authority.Status { Context = Context(), State = state });
            }
        }

        public Task<RimGovernor.Brain.GameFacts.Authority.ControlReply> SetModeAsync(RimGovernor.Brain.GameOrders.Authority.SetMode request, CancellationToken ct)
        {
            lock (_mu)
            {
                _generation++;
                _active = true;
                return Task.FromResult(new RimGovernor.Brain.GameFacts.Authority.ControlReply
                {
                    Outcome = new RimGovernor.Brain.GameFacts.Authority.ControlReplyOutcome.GrantedCase(new RimGovernor.Brain.GameFacts.Authority.Granted { Context = Context(), Authority = new RimGovernor.Brain.GameFacts.Authority.ActiveAuthority { Mode = RimGovernor.Brain.GameFacts.Authority.Mode.Auto } }),
                });
            }
        }

        public Task<RimGovernor.Brain.GameFacts.Authority.ControlReply> RevokeAsync(RimGovernor.Brain.GameOrders.Authority.Revoke request, CancellationToken ct)
        {
            lock (_mu)
            {
                _generation++;
                _active = false;
                return Task.FromResult(new RimGovernor.Brain.GameFacts.Authority.ControlReply
                {
                    Outcome = new RimGovernor.Brain.GameFacts.Authority.ControlReplyOutcome.RevokedCase(new RimGovernor.Brain.GameFacts.Authority.Revoked { Context = Context(), Authority = new RimGovernor.Brain.GameFacts.Authority.InactiveAuthority { Reason = request.Reason } }),
                });
            }
        }
    }

    private sealed record RigOptions
    {
        public Func<RimGovernor.Brain.GameFacts.Common.Identity, long, CancellationToken, Task>? Autosave { get; init; }

        public long AutosaveEveryTicks { get; init; }

        /// <summary>Gives the root plan a coupled action whose prerequisite has not completed yet.</summary>
        public bool CoupledPlan { get; init; }

        /// <summary>The signal the driver's event consumer wakes; none by default.</summary>
        public WakeSignal? Wake { get; init; }

        /// <summary>Pins the run speed at Hyperfast, as test acceleration does: the player's requests set <see cref="Rig.Target"/> alone.</summary>
        public bool Accelerated { get; init; }
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

        public PlannerScheduler Scheduler { get; private set; } = null!;

        public PlannerRunDriver Driver { get; private set; } = null!;

        public ClockTargetSpeed Target { get; } = new(RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast);

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
            var facade = DispatchProxy.Create<IGameApi, ClockFacade>();
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
            rig.Scheduler = new PlannerScheduler(rig.Player, DispatchProxy.Create<IPlannerStepNative, Unreached>(), new PlannerSchedulerConfig { MaxAge = TimeSpan.FromSeconds(30), Worker = true }, rig.Time);
            var config = new ClockRunConfig
            {
                TargetSpeed = options.Accelerated ? new ClockTargetSpeed(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast) : rig.Target,
                PlayerSpeed = rig.Target,
                Policy = new RimGovernor.Brain.GameOrders.Clock.WatchPolicy
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
                },
                Autosave = options.Autosave,
                AutosaveEveryTicks = options.AutosaveEveryTicks,
                // The driver beats often so a test waits milliseconds for a heartbeat.
                HeartbeatInterval = TimeSpan.FromMilliseconds(10),
                CallTimeout = timeout,
            };
            rig.Driver = PlannerRunDriver.Create(rig.Player, rig.Session, rig.Scheduler, rig.Game, writer, config, rig.Time, options.Wake, lifetime);
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
        public Task<bool> StepAsync() => Driver.AfterStepAsync(new PlannerSchedulerResult { HoldSerial = Game.HoldSerial }, Ct);

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

        // A clock never run is held for NeverRun: the first step Runs it at the target speed under the base colony watch,
        // naming the review tick one cadence past the tick it read (#3556).
        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
        var run = Assert.Single(rig.Game.RunRequests);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast, run.Speed);
        Assert.Equal(RimGovernor.Brain.GameOrders.Clock.WatchMode.Colony, run.Policy!.Mode);
        Assert.Empty(run.Policy.AcknowledgedHostileIds);
        Assert.Equal((ulong)(StartTick + ClockRunConfig.DefaultReviewEveryTicks), run.HoldAtTick);
        Assert.True(rig.Scheduler.ClockRunning);

        // Run is idempotent and doubles as the speed change: the same watch and speed are not sent again.
        Assert.False(await rig.StepAsync());
        Assert.Equal(1, rig.Game.Runs);

        rig.Target.Set(RimGovernor.Brain.GameFacts.Clock.Speed.Fast);
        Assert.True(await rig.StepAsync());
        Assert.Equal(2, rig.Game.Runs);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Fast, rig.Game.TargetSpeed);
        Assert.False(await rig.StepAsync());
        Assert.Equal(2, rig.Game.Runs);

        // Hyperfast and Ultrafast share the game's Ultrafast but are distinct speeds: each change is a Run.
        rig.Target.Set(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast);
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, rig.Game.TargetSpeed);
        rig.Target.Set(RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast);
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast, rig.Game.TargetSpeed);
        Assert.Equal(4, rig.Game.Runs);
        Assert.Equal(0, rig.Game.Holds);
    }

    [Fact]
    public async Task APlayerSpeedRequestWithANewSerialSetsTheTargetAndTheNextRunCarriesIt()
    {
        using var rig = await Rig.StartAsync();
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast, rig.Game.TargetSpeed);

        // The request reaches Status even when its wake is lost: the next read adopts it and the Run changes the speed.
        rig.Game.RequestSpeed(RimGovernor.Brain.GameFacts.Clock.Speed.Fast, publish: false);
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Fast, rig.Target.Speed);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Fast, rig.Game.TargetSpeed);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Fast, rig.Game.RunRequests[^1].Speed);
        Assert.Equal(2, rig.Game.Runs);
    }

    [Fact]
    public async Task TheSameSpeedRequestSerialAgainChangesNothing()
    {
        using var rig = await Rig.StartAsync();
        rig.Game.RequestSpeed(RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, publish: false);

        // The driver's first read adopts the request already pending.
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, rig.Game.TargetSpeed);

        // A target the player API set since stands: the serial it adopted is not adopted again.
        rig.Target.Set(RimGovernor.Brain.GameFacts.Clock.Speed.Normal);
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Normal, rig.Game.TargetSpeed);
        Assert.False(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Normal, rig.Target.Speed);
        Assert.Equal(2, rig.Game.Runs);

        // The same speed asked again is a new serial, adopted again.
        rig.Game.RequestSpeed(RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, publish: false);
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Superfast, rig.Game.TargetSpeed);
    }

    [Fact]
    public async Task ASpeedRequestUnderTestAccelerationKeepsHyperfast()
    {
        using var rig = await Rig.StartAsync(new RigOptions { Accelerated = true });
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, rig.Game.TargetSpeed);

        // The request sets the player's target, as the player API's route does, but the pinned run speed is unmoved.
        rig.Game.RequestSpeed(RimGovernor.Brain.GameFacts.Clock.Speed.Normal, publish: false);
        Assert.False(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Normal, rig.Target.Speed);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, rig.Game.TargetSpeed);
        Assert.Equal(1, rig.Game.Runs);
    }

    [Fact]
    public async Task ASpeedRequestedEventWakesTheStepLoop()
    {
        var wake = new WakeSignal();
        using var rig = await Rig.StartAsync(new RigOptions { Wake = wake });
        Assert.True(await rig.StepAsync());
        await rig.DeliveredAsync();
        _ = wake.TryTakeStep();
        _ = wake.TakeInvalidated();

        // Under the running clock the request wakes a step: a quiet one with no hold, ending in the Run at the new speed.
        rig.Game.RequestSpeed(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast);
        await rig.DeliveredAsync();
        Assert.True(wake.TryTakeStep());
        var reason = wake.TakeInvalidated();
        Assert.Equal(StepCause.Review, reason.Cause);
        Assert.False(reason.Stopped);
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, rig.Game.TargetSpeed);
    }

    [Fact]
    public async Task ADeferredStepNeverTouchesTheClock()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();

        Assert.False(await rig.Driver.AfterStepAsync(new PlannerSchedulerResult { Deferred = true }, Ct));
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
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Hostile, rig.Game.Reason);

        // The step read the hold's serial, so it planned against the held world and Run resumes the clock.
        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);
        Assert.Equal(2, rig.Game.Runs);
        Assert.Equal(rig.Game.HoldSerial, rig.Game.RunRequests[^1].PlannedHoldSerial);
    }

    [Fact]
    public async Task AColonyLostHoldIsFiledAndNeverRun()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        rig.Game.Hazard(RimGovernor.Brain.GameFacts.Clock.HoldReason.ColonyLost, new NativeEvents.PauseEvidence(true, true, null, null));
        await rig.DeliveredAsync();

        // A lost colony is terminal (#2819): no step resumes it, and nothing is a fault.
        Assert.False(await rig.StepAsync());
        Assert.False(await rig.StepAsync());
        Assert.True(rig.Game.Held);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.ColonyLost, rig.Game.Reason);
        Assert.Equal(1, rig.Game.Runs);

        var note = new PlannerRunDriver.RunNote { Action = "lost", Reason = "colony_lost", HeldFor = RimGovernor.Brain.GameFacts.Clock.HoldReason.ColonyLost };
        var row = PlannerRunDriver.RunRow(new PlannerSchedulerResult(), note, null, TimeSpan.Zero);
        Assert.NotNull(row);
        Assert.Equal("lost", row.Verdict);
        Assert.Equal("colony_lost", row.Reason);
        Assert.Equal("HOLD_REASON_COLONY_LOST", row.Attrs["held_for"]);
    }

    [Fact]
    public async Task ARefusedRunIsRetriedAtTheNextStepNotAFault()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();

        rig.Game.RefuseRun = RimGovernor.Brain.GameFacts.Common.FailureCode.NativeFailure;
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

        await Assert.ThrowsAsync<HeldException>(() => rig.Driver.AfterStepAsync(new PlannerSchedulerResult { HoldSerial = stepRead }, Ct));
        Assert.Equal([stepRead], rig.Game.StaleSerials);
        Assert.Equal(1, rig.Game.Runs);
    }

    [Fact]
    public async Task ARunRefusedForAHoldTheStepDidNotSeeReplansFromAFreshReadNotAFault()
    {
        using var rig = await Rig.StartAsync();
        var stepRead = await HeldAfterTheStepReadAsync(rig);

        var refused = await Assert.ThrowsAsync<HeldException>(() => rig.Driver.AfterStepAsync(new PlannerSchedulerResult { HoldSerial = stepRead }, Ct));
        Assert.Contains("clock run refused", refused.Message, StringComparison.Ordinal);
        Assert.True(rig.Game.Held);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Hostile, rig.Game.Reason);
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
        // That step marks where the save interval starts, and a short run is not due.
        Assert.True(await rig.StepAsync());
        Assert.Empty(saved);
        rig.Game.Advance(100);
        Assert.False(await rig.StepAsync());
        Assert.Equal(0, rig.Game.Holds);

        // Due under a running clock, before the review tick: the brain holds for the save.
        rig.Game.Advance(400);
        Assert.True(await rig.StepAsync());
        Assert.Equal(1, rig.Game.Holds);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Autosave, rig.Game.Reason);
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

        // Due at the review tick: the review hold is already paused, so the save runs there without a second hold
        // (#3556), and no save ever happens at a running step.
        rig.Game.Advance(600);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, rig.Game.Reason);
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        Assert.Equal(2, saved.Count);
        Assert.Equal(1, rig.Game.Holds);
        Assert.All(heldWhenSaving, held => Assert.True(held));
        Assert.False(rig.Game.Held);
        Assert.Equal(3, rig.Game.Runs);
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
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.CoupledOrder, rig.Game.Reason);
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        Assert.Empty(saved);

        // The first running step with nothing newly coupled marks where the interval starts.
        Assert.False(await rig.StepAsync());

        // A hazard that fires as the autosave hold lands keeps its reason; the due save still runs inside that hold.
        rig.Game.Advance(500);
        rig.Game.BeforeHold = rig.Game.HostileHazard;
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Hostile, rig.Game.Reason);
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
        rig.Game.Advance(400);
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Autosave, rig.Game.Reason);
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
        await rig.Session.HoldClockAsync(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, "checkpoint", Ct);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, rig.Game.Reason);
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
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.CoupledOrder, rig.Game.Reason);
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
        await rig.Session.HoldClockAsync(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, "checkpoint", Ct);

        // The brain's hold was a success that changed nothing: the hazard's reason stands and no second hold took.
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Hostile, rig.Game.Reason);
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
        rig.Game.Advance(400);

        // A step that read the hold serial before the hazard is refused. The hazard fires inside the next step, so the
        // serial read before that step predates it, whenever the consumer delivers it.
        var earlySerial = rig.Game.HoldSerial;
        rig.Game.BeforeHold = rig.Game.HostileHazard;
        Assert.True(await rig.StepAsync());
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Hostile, rig.Game.Reason);
        Assert.Equal(0, rig.Game.Holds);

        await rig.DeliveredAsync();
        await Assert.ThrowsAsync<HeldException>(() => rig.Driver.AfterStepAsync(new PlannerSchedulerResult { HoldSerial = earlySerial }, Ct));
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
            rig.Game.Hazard(RimGovernor.Brain.GameFacts.Clock.HoldReason.ColonistDowned, new NativeEvents.PawnEvidence(new NativeEvents.EventPawn("colonist-1", "Ann", 0, 0, "downed")));
        };
        Assert.True(await rig.StepAsync());
        Assert.True(rig.Game.Held);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.ColonistDowned, rig.Game.Reason);
        Assert.False(rig.Scheduler.ClockRunning);
        Assert.Equal(2, rig.Game.Runs);

        // The new hold leaves the clock held until a step plans against it.
        await rig.DeliveredAsync();
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.ColonistDowned, rig.Game.Reason);

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
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, rig.Game.Reason);
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
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, rig.Game.Reason);
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
        var metrics = (CombatStopMetrics)typeof(PlannerRunDriver).GetField("_combatStops", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Driver)!;
        metrics.Admitted(null, rig.Game.Tick, rig.Time.Now(), true);
        Assert.True(metrics.Active);

        rig.Game.Hazard(RimGovernor.Brain.GameFacts.Clock.HoldReason.CombatEvent, new NativeEvents.CombatEvidence(RimGovernor.Brain.GameFacts.Clock.CombatEvent.Downed, "colonist-1", "downed"));
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

        rig.Game.Hazard(RimGovernor.Brain.GameFacts.Clock.HoldReason.CombatEvent, new NativeEvents.CombatEvidence(RimGovernor.Brain.GameFacts.Clock.CombatEvent.Downed, null, "downed"));
        await rig.DeliveredAsync();

        var marks = rig.Scheduler.Queue.Seq;
        Assert.True(await rig.StepAsync());
        Assert.Equal(marks, rig.Scheduler.Queue.Seq);
        Assert.False(rig.Game.Held);
    }

    [Fact]
    public async Task EveryReviewHoldResumesWithARunNamingTheNextReviewTick()
    {
        using var rig = await Rig.StartAsync();
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());

        // The cadence is the clock's (#3556): a step before the review tick changes nothing, native holds for Review at
        // the tick the Run named, and the step from that hold Runs again naming the next one. The brain holds nothing.
        for (var i = 0; i < 20; i++)
        {
            rig.Game.Advance(250);
            Assert.False(await rig.StepAsync());
            Assert.False(rig.Game.Held);

            rig.Game.Advance(350);
            Assert.True(rig.Game.Held);
            Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, rig.Game.Reason);
            await rig.DeliveredAsync();
            Assert.True(await rig.StepAsync());
            Assert.False(rig.Game.Held);
            Assert.Equal(i + 2, rig.Game.Runs);
            Assert.Equal((ulong)(rig.Game.Tick + ClockRunConfig.DefaultReviewEveryTicks), rig.Game.RunRequests[^1].HoldAtTick);
        }

        Assert.True(rig.Scheduler.ClockRunning);
        Assert.Equal(0, rig.Game.Holds);
        Assert.Empty(rig.Game.HoldRequests);
        Assert.True(rig.Session.State().Enabled);
        await UntilAsync(() => rig.Game.Heartbeats > 0);
    }

    [Fact]
    public void AnOpenHuntsReviewHoldIsItsNextDecisionOnce()
    {
        // A quiet hunt arms no decision event: each review hold of an open hunt re-runs the defense planner, once per hold.
        Assert.True(ClockCombatStops.HuntReview(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, fightOpen: true, prey: 1, defenseRan: false, heldAt: 7, marked: 6));
        Assert.False(ClockCombatStops.HuntReview(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, fightOpen: true, prey: 1, defenseRan: false, heldAt: 7, marked: 7));
        // The step already ran it, the fight is no hunt, no fight is open, or the hold is another's.
        Assert.False(ClockCombatStops.HuntReview(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, fightOpen: true, prey: 1, defenseRan: true, heldAt: 7, marked: 6));
        Assert.False(ClockCombatStops.HuntReview(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, fightOpen: true, prey: 0, defenseRan: false, heldAt: 7, marked: 6));
        Assert.False(ClockCombatStops.HuntReview(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, fightOpen: false, prey: 1, defenseRan: false, heldAt: 7, marked: 6));
        Assert.False(ClockCombatStops.HuntReview(RimGovernor.Brain.GameFacts.Clock.HoldReason.Autosave, fightOpen: true, prey: 1, defenseRan: false, heldAt: 7, marked: 6));
    }

    [Fact]
    public void AnOpenFightArmsOnlyTheDecisionEvents()
    {
        Assert.Equivalent(
            new[] { RimGovernor.Brain.GameFacts.Clock.CombatEvent.HostileArrived, RimGovernor.Brain.GameFacts.Clock.CombatEvent.Downed, RimGovernor.Brain.GameFacts.Clock.CombatEvent.SeriousInjury },
            ClockRunWork.ArmedStops(true));
        Assert.Empty(ClockRunWork.ArmedStops(false));
    }

    [Fact]
    public void TheFallbackSpeedLowersTheTargetOnlyWhileTheGateIsTripped()
    {
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast, ClockCombatStops.SpeedFor(RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast, RimGovernor.Brain.GameFacts.Clock.Speed.Normal, false));
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Normal, ClockCombatStops.SpeedFor(RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast, RimGovernor.Brain.GameFacts.Clock.Speed.Normal, true));
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, ClockCombatStops.SpeedFor(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, RimGovernor.Brain.GameFacts.Clock.Speed.Normal, false));
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Normal, ClockCombatStops.SpeedFor(RimGovernor.Brain.GameFacts.Clock.Speed.Hyperfast, RimGovernor.Brain.GameFacts.Clock.Speed.Normal, true));
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Normal, ClockCombatStops.SpeedFor(RimGovernor.Brain.GameFacts.Clock.Speed.Normal, RimGovernor.Brain.GameFacts.Clock.Speed.Fast, true));
    }

    private static RimGovernor.Brain.GameFacts.Clock.Status HeldAt(DateTimeOffset at) =>
        new()
        {
            Context = new() { Identity = WorldIdentity, Tick = 0, NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase() },
            State = RimGovernor.Brain.GameFacts.Clock.ClockState.Held, Hold = new RimGovernor.Brain.GameFacts.Clock.HoldState.HeldCase(RimGovernor.Brain.GameFacts.Clock.HoldReason.CombatEvent, at.ToUnixTimeMilliseconds(), new RimGovernor.Brain.GameFacts.Clock.HoldTrigger.CombatCase(RimGovernor.Brain.GameFacts.Clock.CombatEvent.Downed)),
            LastRun = new RimGovernor.Brain.GameFacts.Clock.RunHistory.RanCase(RimGovernor.Brain.GameFacts.Clock.Speed.Normal), Pacing = new RimGovernor.Brain.GameFacts.Clock.Pacing.StoppedCase(),
            PlayerRequest = new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.NoneCase(), HoldSerial = 1, NativeTickBoundary = true, ActualPaused = true, EffectiveTicksPerSecond = 0,
        };

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

        var metrics = (CombatStopMetrics)typeof(PlannerRunDriver).GetField("_combatStops", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Driver)!;
        metrics.Admitted(null, rig.Game.Tick, rig.Time.Now(), true);
        rig.Game.Hazard(RimGovernor.Brain.GameFacts.Clock.HoldReason.CombatEvent, new NativeEvents.CombatEvidence(RimGovernor.Brain.GameFacts.Clock.CombatEvent.Downed, null, "downed"));
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        Assert.True(await rig.StepAsync());
        Assert.False(rig.Game.Held);

        // The fake stamps its holds in 1970, so the resume is far over any budget; with no fight open the fallback
        // does not apply (and the colony Run closes the combat) and the Run keeps the target speed.
        Assert.False(metrics.Tripped);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.Speed.Ultrafast, rig.Game.TargetSpeed);
    }

    [Fact]
    public async Task APushedInvalidationWakesItsFamilyAndSections()
    {
        var wake = new WakeSignal();
        using var rig = await Rig.StartAsync(new RigOptions { Wake = wake });
        await rig.DeliveredAsync();
        _ = wake.TakeInvalidated();

        rig.Game.Invalidate(RimGovernor.Brain.GameFacts.Clock.FactFamily.Research);
        await rig.DeliveredAsync();

        var reason = wake.TakeInvalidated();
        Assert.Equal(StepCause.Wake, reason.Cause);
        Assert.Equal(new[] { FactFamily.Research }, reason.Families);
        Assert.NotEmpty(reason.Sections);
    }

    [Fact]
    public async Task OnlyAHoldWakesTheStepLoopAndTheReviewTickIsItsOwnCause()
    {
        var wake = new WakeSignal();
        using var rig = await Rig.StartAsync(new RigOptions { Wake = wake });
        await rig.DeliveredAsync();
        Assert.True(await rig.StepAsync());
        await rig.DeliveredAsync();
        _ = wake.TakeInvalidated();

        // An invalidation under the running clock marks its family for the next review and hints the Worker; it never
        // wakes a step (#3556).
        rig.Game.Invalidate(RimGovernor.Brain.GameFacts.Clock.FactFamily.Research);
        await rig.DeliveredAsync();
        Assert.False(wake.TryTakeStep());

        // The review tick holds the clock: the step wakes with cause Review, the hold's stamp and the evidence so far.
        rig.Game.Advance(ClockRunConfig.DefaultReviewEveryTicks);
        Assert.Equal(RimGovernor.Brain.GameFacts.Clock.HoldReason.Review, rig.Game.Reason);
        await rig.DeliveredAsync();
        Assert.True(wake.TryTakeStep());
        var review = wake.TakeInvalidated();
        Assert.Equal(StepCause.Review, review.Cause);
        Assert.True(review.Stopped);
        Assert.NotNull(review.StopAt);
        Assert.Equal(new[] { FactFamily.Research }, review.Families);
        Assert.False(wake.TryTakeStep());

        // A hazard hold is an early review of everything: cause Wake, so the queue marks every planner.
        Assert.True(await rig.StepAsync());
        await rig.DeliveredAsync();
        _ = wake.TakeInvalidated();
        rig.Game.HostileHazard();
        await rig.DeliveredAsync();
        Assert.True(wake.TryTakeStep());
        var hazard = wake.TakeInvalidated();
        Assert.Equal(StepCause.Wake, hazard.Cause);
        Assert.True(hazard.Stopped);
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
        rig.Game.Invalidate(RimGovernor.Brain.GameFacts.Clock.FactFamily.Research, new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = Colony, LoadToken = "load-2", MapId = Map });
        rig.Game.Invalidate(RimGovernor.Brain.GameFacts.Clock.FactFamily.Pawns);
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
        rig.Game.Invalidate(RimGovernor.Brain.GameFacts.Clock.FactFamily.Colony);
        await rig.DeliveredAsync();
        _ = wake.TakeInvalidated();
        var before = rig.Driver.EventsSeen;

        // The consumer holds one event while its context read waits; native fills the queue and drops one more.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Game.ContextGate = gate;
        rig.Game.Invalidate(RimGovernor.Brain.GameFacts.Clock.FactFamily.Colony);
        await UntilAsync(() => rig.Game.BlockedContextReads > 0);
        for (var i = 0; i <= ColonyEventChannel.Capacity; i++)
        {
            rig.Game.Invalidate(RimGovernor.Brain.GameFacts.Clock.FactFamily.Colony);
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
    public async Task AContextReadThatTimesOutDelaysTheBatchAndPlansNoFullStep()
    {
        var wake = new WakeSignal();
        using var rig = await Rig.StartAsync(new RigOptions { Wake = wake });
        await rig.DeliveredAsync();
        _ = wake.TakeInvalidated();
        var before = rig.Driver.EventsSeen;

        // The game thread is busy past the call bound for the batch's first two context reads: the batch is
        // held and read again, so its event arrives late, once, and wakes the step it names rather than a Full one.
        rig.Game.ContextTimeouts = 2;
        rig.Game.Invalidate(RimGovernor.Brain.GameFacts.Clock.FactFamily.Pawns);
        await UntilAsync(() => rig.Driver.EventsSeen > before);

        Assert.Equal(2, rig.Game.TimedOutContextReads);
        Assert.Equal(before + 1, rig.Driver.EventsSeen);
        var reason = wake.TakeInvalidated();
        Assert.Equal(StepCause.Wake, reason.Cause);
        Assert.Equal(new[] { FactFamily.Pawns }, reason.Families);
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
        rig.Game.Invalidate(RimGovernor.Brain.GameFacts.Clock.FactFamily.Colony);
        await rig.DeliveredAsync();
        Assert.True(await rig.Driver.AfterStepAsync(new PlannerSchedulerResult { HoldSerial = stepRead }, Ct));
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
