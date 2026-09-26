#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// Persistent, main-thread play guard. The MCP call only changes or reads
    /// state; TickManagerUpdate owns monitoring and pausing after it returns.
    public sealed class HomeSupervisedPlayTools
    {
        private const string ToolName = "home/supervised_play";

        [Tool(ToolName, Title = "Supervised nonblocking play",
            Description = "Start, pause, renew or inspect a persistent in-game safety watcher. Start returns immediately; the watcher pauses independently on danger or lease expiry.")]
        [ToolResponse("unknownArguments", "array", "Every argument key the caller sent that this tool does not declare, sorted case-sensitively. Empty means the call was clean.", Always = true)]
        [ToolResponse("unknownArgumentsWarning", "string", "Present when unknown arguments were supplied or raw argument inspection was unavailable.", Nullable = true)]
        public async Task<object?> SupervisedPlay(
            IRimBridgeContext ctx,
            CancellationToken cancellationToken,
            [ToolParameter(Description = "start, pause, speed, status, heartbeat, or events.")] string op,
            [ToolParameter(Description = "Caller identity used with epoch to reject stale control.", DefaultValue = "agent")] string owner = "agent",
            [ToolParameter(Description = "Epoch returned by start; required for heartbeat and pause.", DefaultValue = 0L)] long epoch = 0L,
            [ToolParameter(Description = "Renewable watchdog lease, clamped to 1000..30000 ms.", DefaultValue = 15000)] int leaseMs = 15000,
            [ToolParameter(Description = "Normal, Fast, Superfast or Ultrafast.", DefaultValue = "Superfast")] string speed = "Superfast",
            [ToolParameter(Description = "colony stops on any new/worsened injury; combat records ordinary wounds and stops only at configured health thresholds.", DefaultValue = "colony")] string mode = "colony",
            [ToolParameter(Description = "Combat mode: stop when summary health drops this fraction from start, clamped 0.01..1.", DefaultValue = 0.15f)] float healthDropFraction = 0.15f,
            [ToolParameter(Description = "Combat mode: stop when summary health crosses under this fraction within the epoch, clamped 0.01..1.", DefaultValue = 0.5f)] float minHealthFraction = 0.5f,
            [ToolParameter(Description = "Accepted and ignored: alerts never stop play.", DefaultValue = "")] string ignoredAlertLabels = "",
            [ToolParameter(Description = "Pause for hostiles within this many cells of a colonist; distant cave occupants alone do not stop play.", DefaultValue = 40f)] float hostileWithin = 40f,
            [ToolParameter(Description = "Stable IDs of explicitly acknowledged hostiles or nearby hunting predators, comma-separated.", DefaultValue = "")] string ignoredHostileIds = "",
            [ToolParameter(Description = "Stable IDs of explicitly acknowledged downed colonists, comma-separated.", DefaultValue = "")] string ignoredDownedColonistIds = "",
            [ToolParameter(Description = "Stable IDs of colonists whose non-severe injuries are acknowledged; they never stop the clock in this epoch, comma-separated.", DefaultValue = "")] string ignoredInjuredColonistIds = "",
            [ToolParameter(Description = "After a colonist_injury stop, that colonist non-severe injuries do not stop the clock again for this many real milliseconds, across restarts. Clamped 0..1800000; 0 disables.", DefaultValue = 180000)] int injuryStopCooldownMs = 180000,
            [ToolParameter(Description = "For events, return rows strictly after this cursor.", DefaultValue = 0L)] long afterCursor = 0L,
            [ToolParameter(Description = "For events, maximum rows, clamped to 1..128.", DefaultValue = 64)] int limit = 64,
            [ToolParameter(Description = "Start only: pause after exactly this many ordinary game ticks, independently of controller polling and heartbeat renewal. 0 leaves tick duration unbounded; otherwise 1..1800000.", DefaultValue = 0)] int maxTicks = 0,
            [ToolParameter(Description = "Tracked surgical patient IDs. Only permits downing while alive, anesthetized, in bed, not bleeding or dangerously ill, and above half health. Rechecked every safety sweep; no injury or death guard is disabled.", DefaultValue = "")] string surgicalRecoveryIds = "",
            [ToolParameter(Description = "Disposable acceptance only: enable native boost for a bounded Ultrafast epoch, with safety probes at most 30 game ticks apart. Restores the prior boost on stop; does not suppress native forced slowdown.", DefaultValue = false)] bool testAcceleration = false,
            [ToolParameter(Description = "Observed stable resting patient IDs, requiring maxTicks 1..600. Native sweeps require alive, downed, in bed, above half health, no bleeding, tending need, anesthesia or life-threatening condition. Injury and death still stop play.", DefaultValue = "")] string medicalRestIds = "")
        {
            return BridgeCommon.WithUnknownArguments(
                await SupervisedPlayCore(ctx, cancellationToken, op, owner, epoch,
                    leaseMs, speed, mode, healthDropFraction, minHealthFraction,
                    ignoredAlertLabels, hostileWithin, ignoredHostileIds, ignoredDownedColonistIds,
                    ignoredInjuredColonistIds, injuryStopCooldownMs,
                    afterCursor, limit, maxTicks, surgicalRecoveryIds, testAcceleration, medicalRestIds).ConfigureAwait(false),
                ctx, typeof(HomeSupervisedPlayTools), ToolName);
        }

        private async Task<object> SupervisedPlayCore(
            IRimBridgeContext ctx, CancellationToken cancellationToken,
            string op, string owner, long epoch, int leaseMs, string speed,
            string mode, float healthDropFraction, float minHealthFraction,
            string ignoredAlertLabels, float hostileWithin, string ignoredHostileIds,
            string ignoredDownedColonistIds, string ignoredInjuredColonistIds,
            int injuryStopCooldownMs, long afterCursor, int limit, int maxTicks, string surgicalRecoveryIds, bool testAcceleration, string medicalRestIds)
        {
            if (ctx == null || ctx.MainThread == null)
                return Fail("No main-thread dispatcher is available.");
            var action = (op ?? string.Empty).Trim().ToLowerInvariant();
            if (action == "status") return Supervisor.Status();
            if (action == "events") return Supervisor.Events(afterCursor, limit);
            if (action == "heartbeat") return Supervisor.Heartbeat(owner, epoch, leaseMs);
            if (action == "speed")
            {
                TimeSpeed changed;
                if (!Enum.TryParse(speed ?? string.Empty, true, out changed) || changed == TimeSpeed.Paused)
                    return Fail("Unknown play speed; use Normal, Fast, Superfast or Ultrafast.");
                return await ctx.MainThread.InvokeAsync(() => Supervisor.Speed(owner, epoch, changed),
                    cancellationToken).ConfigureAwait(false);
            }
            if (action == "start")
            {
                if (maxTicks < 0 || maxTicks > 1800000)
                    return Fail("maxTicks must be between 0 and 1800000.");
                if (!string.IsNullOrWhiteSpace(medicalRestIds) && (maxTicks < 1 || maxTicks > 600))
                    return Fail("Medical rest monitoring requires maxTicks between 1 and 600.");
                Supervisor.EnsurePatched();
                TimeSpeed requested;
                if (!Enum.TryParse(speed ?? string.Empty, true, out requested)
                    || requested == TimeSpeed.Paused)
                    return Fail("Unknown play speed; use Normal, Fast, Superfast or Ultrafast.");
                if (testAcceleration && (requested != TimeSpeed.Ultrafast || maxTicks == 0))
                    return Fail("Test acceleration requires Ultrafast and a positive native tick budget.");
                return await ctx.MainThread.InvokeAsync(() => Supervisor.Start(owner, requested,
                    leaseMs, mode, healthDropFraction, minHealthFraction, hostileWithin,
                    ignoredHostileIds, ignoredDownedColonistIds,
                    ignoredInjuredColonistIds, injuryStopCooldownMs, maxTicks, surgicalRecoveryIds, testAcceleration, medicalRestIds),
                    cancellationToken).ConfigureAwait(false);
            }
            if (action == "pause")
                return await ctx.MainThread.InvokeAsync(() => Supervisor.Pause(owner, epoch),
                    cancellationToken).ConfigureAwait(false);
            return Fail("Unknown op '" + op + "'. Use start, pause, speed, status, heartbeat or events.");
        }

        private static object Fail(string message) { return new Dictionary<string, object?> {
            { "success", false }, { "tool", ToolName }, { "message", message } }; }
    }

    internal static partial class Supervisor
    {
        private const int Capacity = 128;
        private static readonly FieldInfo BoostField = typeof(TickManager).GetField("UltraSpeedBoost", BindingFlags.Static | BindingFlags.NonPublic);
        // Wall-clock grace for a force pause with no window behind it.
        // An autosave takes about a second; 20 s is generous and still
        // far short of anything a person would sit through.
        private const int ForcePauseGraceMs = 20000;
        private static readonly object Gate = new object();
        private static ClockEventJournal? Journal;
        private static ClockEventJournal EnsureJournal()
        {
            if (Journal != null) return Journal;
            var journal = Journal = new ClockEventJournal();
            _cursor = journal.Newest;
            _epoch = Math.Max(_epoch, _cursor);
            return journal;
        }
        // Journal evidence for home/runtime_health: the newest retained cursor
        // and every retained row that reads as damaged. Read-only; a journal
        // that has not been opened yet is reported as such, not opened here.
        internal static object JournalHealth()
        {
            lock (Gate)
            {
                var journal = Journal;
                // waiters is how many clock_read_events long polls the host is
                // holding right now (#617): a caller establishes a held poll by
                // observing it rather than by elapsed time, and the observation
                // itself travels on a concurrent call.
                var waiters = WaitersHeldLocked();
                if (journal == null) return new { initialized = false, waiters };
                return new
                {
                    initialized = true,
                    newestCursor = journal.Newest.ToString(CultureInfo.InvariantCulture),
                    corruptRows = journal.Corrupt.Select(c => new { cursor = c.Key.ToString(CultureInfo.InvariantCulture), error = c.Value }).ToArray(),
                    waiters
                };
            }
        }
        // For a disposable fixture only: the retained file of one published row.
        internal static string RetainedJournalRowPath(long cursor)
        {
            lock (Gate) return EnsureJournal().RetainedPath(cursor);
        }
        private static State? _state;
        private static State ActiveState => _state ?? throw new InvalidOperationException("No supervised clock epoch.");
        private static long _epoch;
        private static long _cursor;
        private static int _patched;
        private static string? _patchError;
        // Injury stops are remembered across epochs on purpose. Restarting the
        // clock re-baselines injuries, so without this a wound that keeps
        // arriving reads as brand new every epoch and stops play forever.
        private static readonly Dictionary<int, InjuryStop> InjuryStops = new Dictionary<int, InjuryStop>();
        private static object? _injuryStopsSession;
        // Conscious-hostile count at the last probe of the previous epoch. Like
        // the injury-stop memory this survives Start on purpose: a guard stop
        // and restart across the last kill must still raise the reminder.
        private static int _priorEpochConsciousHostiles;

        internal static bool IsActive { get { lock (Gate) return _state != null && _state.Active; } }

        internal static void EnsurePatched()
        {
            if (Interlocked.CompareExchange(ref _patched, 1, 0) != 0)
            {
                if (_patchError != null) throw new InvalidOperationException(_patchError);
                return;
            }
            try
            {
                var target = AccessTools.Method(typeof(TickManager), "TickManagerUpdate");
                if (target == null) throw new MissingMethodException("TickManager.TickManagerUpdate");
                new Harmony("homebridge.supervised-play").Patch(target,
                    postfix: new HarmonyMethod(typeof(Supervisor), nameof(OnFrame)));
                var info = Harmony.GetPatchInfo(target);
                if (info == null || !info.Owners.Contains("homebridge.supervised-play"))
                    throw new InvalidOperationException("Harmony did not report the supervised-play patch after installation.");
                var tick = AccessTools.Method(typeof(TickManager), "DoSingleTick");
                if (tick == null) throw new MissingMethodException("TickManager.DoSingleTick");
                new Harmony("homebridge.supervised-play").Patch(tick,
                    postfix: new HarmonyMethod(typeof(Supervisor), nameof(OnTick)));
                var tickInfo = Harmony.GetPatchInfo(tick);
                if (tickInfo == null || !tickInfo.Owners.Contains("homebridge.supervised-play"))
                    throw new InvalidOperationException("Native tick boundary patch was not installed.");
                EnsureCeilingPatched();
                EnsureHazardHooks();
                NativeControlAuthority.GenerationChanged += OnAuthorityChanged;
                _patchError = null;
            }
            catch (Exception ex)
            {
                _patchError = ex.GetType().Name + ": " + ex.Message;
                Interlocked.Exchange(ref _patched, 0);
                throw;
            }
        }

        internal static object Start(string owner, TimeSpeed speed, int leaseMs,
            string mode, float healthDropFraction, float minHealthFraction, float hostileWithin,
            string ignoredHostiles, string ignoredDowned, string ignoredInjured,
            int injuryStopCooldownMs, int maxTicks, string surgicalRecoveryIds = "", bool testAcceleration = false, string medicalRestIds = "",
            int blindTickBudget = 0, int maxTicksPerSecond = 0)
        {
            lock (Gate)
            {
                EnsureJournal();
                if (_state != null && _state.Active)
                    return Failure("A supervisor is already active; pause it with its owner and epoch first.");
                if (HomePlayUntilEventTools.ShortGuardRunning)
                    return Failure("play_until_event is active; wait for that short guard to return before starting supervision.");
                if (!HomePlayUntilEventTools.CoreWatchersAvailable)
                    return Failure("Alert or transient-message reflection watcher is unavailable; refusing blind play.");
                if (Current.Game == null || Find.TickManager == null || Find.CurrentMap == null)
                    return Failure("No playable map is loaded.");
                if (testAcceleration && (BoostField == null || BoostField.FieldType != typeof(bool)))
                    return Failure("Native boost support is unavailable.");
                try { LetterPauseHook.EnsurePatched(); }
                catch (Exception error) { return Failure("Pause source tracking unavailable: " + error.Message); }
                if (LongEventHandler.AnyEventNowOrWaiting)
                    return Retryable("A long event (autosave, map generation) is running or queued; nothing is "
                        + "open to dismiss. An autosave clears in about a second -- the caller should retry, "
                        + "not report a refusal.", "long_event");
                // Only a window a human can close is a refusal. A merely paused
                // clock is not: the speed set below unpauses it.
                if (ForcePausingWindows().Count > 0)
                    return Failure(ForcePauseDetail());
                var profile = (mode ?? "colony").Trim().ToLowerInvariant();
                if (profile != "colony" && profile != "combat")
                    return Failure("Unknown mode; use colony or combat.");
                var s = new State(Current.Game, Find.CurrentMap) {
                    Active = true, Epoch = ++_epoch, Owner = owner ?? "agent", RequestedSpeed = speed,
                    Mode = profile, HostileWithin = ClampFloat(hostileWithin, 1f, 250f),
                    HealthDropFraction = ClampFloat(healthDropFraction, 0.01f, 1f),
                    MinHealthFraction = ClampFloat(minHealthFraction, 0.01f, 1f),
                    LeaseExpiresMs = NowMs() + Clamp(leaseMs, 1000, 30000),
                    StartTick = Find.TickManager.TicksGame,
                    TickDeadline = maxTicks == 0 ? (long?)null : (long)Find.TickManager.TicksGame + maxTicks,
                    LastTick = Find.TickManager.TicksGame,
                    LastProbeTick = Find.TickManager.TicksGame, LastProbeMs = NowMs(), LastDigestTick = Find.TickManager.TicksGame,
                    TestAcceleration = testAcceleration,
                    BlindTickBudget = Clamp(blindTickBudget, 0, 1800000), MaxTicksPerSecond = Clamp(maxTicksPerSecond, 0, 60000),
                    LastReadTick = Find.TickManager.TicksGame, AckedCursor = _cursor,
                    IgnoredHostiles = PawnIds(ignoredHostiles),
                    IgnoredDowned = PawnIds(ignoredDowned),
                    SurgicalRecovery = PawnIds(surgicalRecoveryIds),
                    MedicalRest = PawnIds(medicalRestIds),
                    IgnoredInjured = PawnIds(ignoredInjured),
                    InjuryStopCooldownMs = Clamp(injuryStopCooldownMs, 0, 1800000)
                };
                AttachTypedEpoch(s);
                // Pawn IDs belong to a save; another colony must not inherit
                // this one's injury-stop memory.
                if (!ReferenceEquals(_injuryStopsSession, Current.Game))
                { InjuryStops.Clear(); _priorEpochConsciousHostiles = 0; _injuryStopsSession = Current.Game; }
                // Alerts are baselined like letters and messages, by the stable
                // key, so a count suffix moving cannot look like a new alert.
                foreach (var a in HomePlayUntilEventTools.ActiveAlertKeys(AlertPriority.High, null))
                    if (s.AlertKeys.Add(a.Key)) s.BaselineAlerts.Add(AlertRow(a));
                foreach (var l in HomePlayUntilEventTools.Letters()) s.Letters.Add(HomePlayUntilEventTools.SafeLetterId(l));
                foreach (var m in HomePlayUntilEventTools.LiveMessages()) s.Messages.Add(HomePlayUntilEventTools.SafeMessageId(m));
                foreach (var p in HomePlayUntilEventTools.SpawnedPawns(Find.CurrentMap).Where(HomePlayUntilEventTools.SafeIsColonist))
                {
                    s.Injuries[p.thingIDNumber] = InjurySnapshot.Capture(p);
                    var owed = InjuryCooldownRemainingMs(p.thingIDNumber, s.InjuryStopCooldownMs);
                    var acked = s.IgnoredInjured.Contains(p.thingIDNumber);
                    if (owed <= 0 && !acked) continue;
                    s.SuppressedInjuries.Add(new Dictionary<string, object?> {
                        { "pawnId", p.thingIDNumber },
                        { "pawnName", HomePlayUntilEventTools.SafeName(p) },
                        { "reason", owed > 0 ? "cooldown" : "acknowledged" },
                        { "secondsRemaining", (int)((owed + 999) / 1000) } });
                }
                _state = s;
                ClockPauseAccounting.Started(s.Session);
                ClockProbeAccounting.Started(s.Session);
                ResetHazardHooks();
                DigestDirty = false;
                var digestBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                PublishFactChanges(s);
                var probeBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                s.Timing.DigestElapsed += probeBegan - digestBegan; s.Timing.Digests++;
                ClockProbeAccounting.Digested(probeBegan - digestBegan);
                var hit = Probe(s);
                var probeTook = System.Diagnostics.Stopwatch.GetTimestamp() - probeBegan;
                s.Timing.ProbeElapsed += probeTook;
                ClockProbeAccounting.Probed(probeTook);
                if (hit != null)
                {
                    // Starting a guard is itself a request for safety. If the
                    // initial probe finds danger, do not leave a running game
                    // unattended merely because no speed change was needed.
                    Stop(s, hit.Kind, hit.Detail, true, hit.Payload);
                    return Snapshot(s, s.PauseVerified == true);
                }
                if (testAcceleration)
                {
                    s.PriorBoost = (bool)BoostField.GetValue(null);
                    s.BoostOwned = true;
                    BoostField.SetValue(null, true);
                }
                Find.TickManager.CurTimeSpeed = speed;
                if (Find.TickManager.CurTimeSpeed != speed)
                {
                    Stop(s, "start_refused", "The requested speed did not take.", true, null);
                    return Snapshot(s, s.PauseVerified == true);
                }
                Add("started", "Supervised play started.", s, null);
                return Snapshot(s, true);
            }
        }

        internal static object Heartbeat(string owner, long epoch, int leaseMs)
        {
            lock (Gate)
            {
                var s = _state;
                if (s != null && s.Typed != null) return Failure("Canonical clock epochs require the typed renewal capability.");
                if (!Owns(s, owner, epoch)) return Failure("Owner/epoch mismatch or no active supervisor.");
                s.LeaseExpiresMs = LeaseNow(s) + Clamp(leaseMs, 1000, 30000);
                return Snapshot(s, true);
            }
        }

        internal static object Speed(string owner, long epoch, TimeSpeed speed, int? maxTicksPerSecond = null)
        {
            lock (Gate)
            {
                var s = _state;
                if (s != null && s.Typed != null && !typedSpeedCall) return Failure("Canonical clock epochs require the typed speed capability.");
                if (!Owns(s, owner, epoch)) return Failure("Owner/epoch mismatch or no active supervisor.");
                // Update expectation and game speed in the same main-thread
                // critical section, so our own change cannot look external.
                if (s.TestAcceleration && speed != TimeSpeed.Ultrafast)
                    return Failure("Pause the accelerated epoch before changing its speed.");
                var old = s.RequestedSpeed;
                s.RequestedSpeed = speed;
                Find.TickManager.CurTimeSpeed = speed;
                if (Find.TickManager.CurTimeSpeed != speed)
                {
                    s.RequestedSpeed = old;
                    return Failure("The requested speed did not take; supervision remains active.");
                }
                if (maxTicksPerSecond.HasValue) s.MaxTicksPerSecond = Clamp(maxTicksPerSecond.Value, 0, 60000);
                Add("speed_changed", "Supervisor owner changed speed to " + speed + (s.MaxTicksPerSecond > 0 ? " under " + s.MaxTicksPerSecond + " ticks/s" : "") + ".", s,
                    new Dictionary<string, object?> { { "speed", speed.ToString() }, { "maxTicksPerSecond", (long)s.MaxTicksPerSecond },
                        { "regulatedTicksPerSecond", (long)s.RegulatedTicksPerSecond }, { "blindTicks", (long)BlindTicks(s) } });
                return Snapshot(s, true);
            }
        }

        internal static object Pause(string owner, long epoch)
        {
            lock (Gate)
            {
                var s = _state;
                if (s != null && s.Typed != null) return Failure("Canonical clock epochs require the typed owned cleanup capability.");
                if (!Owns(s, owner, epoch)) return Failure("Owner/epoch mismatch or no active supervisor.");
                Stop(s, "requested_pause", "Paused by the supervisor owner.", true, null);
                return Snapshot(s, s.PauseVerified == true);
            }
        }

        internal static object Status()
        {
            lock (Gate) return Snapshot(_state, true);
        }

        internal static object Events(long after, int limit)
        {
            lock (Gate)
            {
                var take = Clamp(limit, 1, Capacity);
                var journal = EnsureJournal();
                var oldest = 1L;
                var gap = false;
                var rows = journal.Read(after, take);
                return new Dictionary<string, object?> {
                    { "success", true }, { "events", rows }, { "gap", gap },
                    { "lostCount", gap ? oldest - after - 1 : 0 },
                    { "oldestCursor", oldest }, { "newestCursor", _cursor },
                    { "nextCursor", rows.Count > 0 ? Convert.ToInt64(rows[rows.Count - 1]["cursor"]) : after }
                };
            }
        }

        // TickManagerUpdate checks Paused after each DoSingleTick. Pausing here
        // stops its frame batch at the boundary, including accelerated frames.
        internal static void OnTick()
        {
            lock (Gate)
            {
                var s = _state;
                if (s == null || !s.Active) return;
                var began = System.Diagnostics.Stopwatch.GetTimestamp();
                try { TickBody(s); }
                finally { s.Timing.HookTicks++; s.Timing.FrameTicks++; s.Timing.HookElapsed += System.Diagnostics.Stopwatch.GetTimestamp() - began; }
            }
        }
        private static void TickBody(State s)
        {
            {
                // The frame can contain many accelerated ticks. Check ownership,
                // lease and tick-bounded hazards before admitting another tick.
                if (s.TestAcceleration) OnUpdate();
                if (!s.Active) return;
                if (!ReferenceEquals(Current.Game, s.Session) || !ReferenceEquals(Find.CurrentMap, s.Map))
                { Stop(s, "session_changed", "Loaded game changed.", false, null); return; }
                var tm = Find.TickManager;
                if (tm == null) { Stop(s, "unavailable", "Tick manager disappeared.", false, null); return; }
                if (tm.TicksGame < s.LastTick)
                { Stop(s, "session_changed", "Game clock rewound.", true, null); return; }
                s.LastTick = tm.TicksGame;
                CaptureTypedContext(s);
                if (StopInvalidTypedAuthority(s)) return;
                var watchBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                var latched = CheckWatches(s);
                s.Timing.WatchElapsed += System.Diagnostics.Stopwatch.GetTimestamp() - watchBegan;
                if (latched) return;
                // Preserve player and letter attribution if the final tick also
                // changed the clock. The frame watcher handles those stops.
                if (tm.CurTimeSpeed != s.RequestedSpeed) return;
                // The tick-paced hazard probe (#626): at every production speed
                // consecutive probes are at most ProbeIntervalTicks apart, however
                // many ticks the frame carries. The accelerated path ran it above.
                if (!s.TestAcceleration)
                {
                    if (!RunProbeIfDue(s, tm)) return;
                    RunDigestIfDue(s, tm);
                }
                if (!s.TickDeadline.HasValue) return;
                var deadline = s.TickDeadline.Value;
                if (tm.TicksGame >= deadline)
                    Stop(s, "tick_budget", "Native execution tick budget reached.", true,
                        new Dictionary<string, object?> { { "startTick", s.StartTick },
                            { "tickDeadline", deadline }, { "tick", tm.TicksGame } });
            }
        }

        // The TickManagerUpdate postfix: one call per frame. Frame counts feed
        // the epoch timing summary; the monitoring itself is OnUpdate.
        internal static void OnFrame()
        {
            lock (Gate)
            {
                MarkFrame();
                var s = _state;
                if (s != null && s.Active)
                {
                    s.Timing.Frames++;
                    if (s.Timing.FrameTicks > s.Timing.MaxFrameTicks) s.Timing.MaxFrameTicks = s.Timing.FrameTicks;
                    s.Timing.FrameTicks = 0;
                }
            }
            OnUpdate();
        }

        internal static void OnUpdate()
        {
            lock (Gate)
            {
                var s = _state;
                if (s == null || !s.Active) return;
                try
                {
                    // A stale watcher belongs to the old session. Retire it,
                    // but never pause the newly loaded colony.
                    if (!ReferenceEquals(Current.Game, s.Session) || !ReferenceEquals(Find.CurrentMap, s.Map)) { Stop(s, "session_changed", "Loaded game changed.", false, null); return; }
                    var tm = Find.TickManager;
                    if (tm == null) { Stop(s, "unavailable", "Tick manager disappeared.", false, null); return; }
                    if (tm.TicksGame < s.LastTick) { Stop(s, "session_changed", "Game clock rewound.", true, null); return; }
                    s.LastTick = tm.TicksGame;
                    CaptureTypedContext(s);
                    if (s.PendingKind != null)
                    {
                        Stop(s, s.PendingKind, s.PendingDetail, true, s.PendingPayload);
                        return;
                    }
                    if (StopInvalidTypedAuthority(s)) return;
                    // A force pause is checked BEFORE the paused/speed tests: it is
                    // the more specific diagnosis, and a long event can hold the
                    // clock without ever touching CurTimeSpeed.
                    if (tm.ForcePaused) { HandleForcePause(s); return; }
                    if (s.ForcePauseSinceMs != 0 && !ResumeAfterForcePause(s)) return;
                    if (tm.CurTimeSpeed == TimeSpeed.Paused) {
                        var letter = LetterPauseHook.Consume();
                        Stop(s, letter == null ? "external_pause" : "letter_pause", ExternalPauseDetail(), false,
                            letter == null ? null : new Dictionary<string, object?> { { "letterId", letter }, { "source", "LetterStack.ReceiveLetter" } });
                        return;
                    }
                    if (tm.CurTimeSpeed != s.RequestedSpeed) { Stop(s, "external_speed_changed", "Speed changed outside the supervisor.", true,
                        new Dictionary<string, object?> { { "expectedSpeed", s.RequestedSpeed.ToString() },
                            { "actualSpeed", tm.CurTimeSpeed.ToString() } }); return; }
                    if (LeaseNow(s) >= s.LeaseExpiresMs) { Stop(s, "lease_expired", "Heartbeat lease expired.", true, null); return; }
                    Regulate(s);
                    if (!RunProbeIfDue(s, tm)) return;
                    RunDigestIfDue(s, tm);
                }
                catch (Exception ex) { Stop(s, "watcher_error", ex.GetType().Name + ": " + ex.Message, true, null); }
            }
        }

        /// The hazard probe when a gate is due (SupervisedPlayHazards.cs: a
        /// hook requested one, ProbeIntervalTicks or ProbeIntervalMs
        /// elapsed). Records the tick gap overall and per hazard class, then
        /// stops on a hit. -> false when play stopped.
        private static bool RunProbeIfDue(State s, TickManager tm)
        {
            var now = NowMs();
            if (!ProbeDue(s.ProbeRequested, tm.TicksGame - s.LastProbeTick, now - s.LastProbeMs)) return true;
            var gap = tm.TicksGame - s.LastProbeTick;
            s.MaxProbeTickGap = Math.Max(s.MaxProbeTickGap, gap);
            ClockProbeAccounting.ProbeGap(gap);
            // A polled class could have gone the whole probe gap undetected; a
            // hooked class at most the ticks from its hook to this probe.
            foreach (var name in HazardClassNames())
                if (HazardBoundTicks(name) == ProbeIntervalTicks) ClockProbeAccounting.HazardGap(name, gap);
            if (s.ProbeRequested)
            {
                foreach (var request in s.ProbeRequests)
                    if (HazardBoundTicks(request.Key) == HookedBoundTicks) ClockProbeAccounting.HazardGap(request.Key, Math.Max(0, tm.TicksGame - request.Value));
                s.ProbeRequests.Clear(); s.ProbeRequested = false;
            }
            s.LastProbeTick = tm.TicksGame;
            s.ProbeCount++;
            s.LastProbeMs = now;
            var probeBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            var hit = Probe(s);
            var probeTook = System.Diagnostics.Stopwatch.GetTimestamp() - probeBegan;
            s.Timing.ProbeElapsed += probeTook;
            ClockProbeAccounting.Probed(probeTook);
            if (hit == null) return true;
            Stop(s, hit.Kind, hit.Detail, true, hit.Payload);
            return false;
        }

        /// The fact-change digests on their own cadence (#626): a game hook
        /// marked one dirty (SupervisedPlayHooks.cs) or DigestIntervalTicks
        /// elapsed. Never part of the probe.
        private static void RunDigestIfDue(State s, TickManager tm)
        {
            if (!DigestDue(DigestDirty, tm.TicksGame - s.LastDigestTick)) return;
            DigestDirty = false;
            s.LastDigestTick = tm.TicksGame;
            var digestBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            PublishFactChanges(s);
            var took = System.Diagnostics.Stopwatch.GetTimestamp() - digestBegan;
            s.Timing.DigestElapsed += took; s.Timing.Digests++;
            ClockProbeAccounting.Digested(took);
        }

        /// A force pause with NO force-pausing window is a long event (the
        /// daily autosave is the common one) or a one-frame transient, and it
        /// clears itself. Decompiled 1.6: `TickManager.ForcePaused` ORs
        /// `WindowStack.WindowsForcePause` with `LongEventHandler.ForcePause`,
        /// which is simply `AnyEventNowOrWaiting`; `Autosaver.AutosaverTick`
        /// queues `DoAutosave` through `QueueLongEvent`, and `Root_Play.Update`
        /// still runs `UpdatePlay` (and so this patch) while that event is
        /// merely QUEUED. With autosaveIntervalDays=1 that fired every in-game
        /// day and killed the supervisor a dozen times a session.
        ///
        /// So: wait it out, say so once, and stop only if it outlives the
        /// grace. A force-pausing WINDOW still stops immediately and is named.
        private static void HandleForcePause(State s)
        {
            var windows = ForcePausingWindows();
            if (windows.Count > 0)
            {
                var naming = ColonyNamingTools.Pending() != null;
                var dialog = ChoiceDialogTools.Pending();
                // A choice dialog the game opened by itself is answered through
                // Operations.AnswerDialog once the controller has read its
                // options (#156); it is named so the stop is actionable headless.
                Stop(s, naming ? "colony_naming" : dialog != null ? "dialog_pause" : "force_paused",
                    naming ? "RimWorld requests initial faction and settlement names."
                        : dialog != null ? "Game is force-paused by " + dialog.GetType().FullName + " (" + (ChoiceDialogTools.Title(dialog) ?? "") + "); read the colony facts dialog section and answer it."
                        : ForcePauseDetail(), true, null);
                return;
            }
            var now = NowMs();
            if (s.ForcePauseSinceMs == 0)
            {
                s.ForcePauseSinceMs = now;
                s.ForcePauseKind = LongEventHandler.AnyEventNowOrWaiting
                    ? "long_event" : "transient_force_pause";
                Add(s.ForcePauseKind,
                    (s.ForcePauseKind == "long_event"
                        ? "A long event (an autosave, most likely) is holding the clock"
                        : "The clock is force-paused with no window behind it")
                    + "; waiting up to " + (ForcePauseGraceMs / 1000)
                    + " s for it to clear. Play is NOT stopped and nothing needs dismissing. "
                    + ClockState() + ".", s,
                    new Dictionary<string, object?> {
                        { "longEvent", LongEventHandler.AnyEventNowOrWaiting },
                        { "graceMs", ForcePauseGraceMs },
                        { "requestedSpeed", s.RequestedSpeed.ToString() } });
            }
            if (now - s.ForcePauseSinceMs < ForcePauseGraceMs) return;
            Stop(s, "force_paused", ForcePauseDetail() + " It did not clear in "
                + (ForcePauseGraceMs / 1000) + " s, which is far longer than an autosave, "
                + "so this is no longer treated as transient.", true, null);
        }

        /// The force pause cleared. Put the requested speed back and carry on.
        /// -> false when the speed would not take, in which case play stopped.
        private static bool ResumeAfterForcePause(State s)
        {
            var waited = NowMs() - s.ForcePauseSinceMs;
            var kind = s.ForcePauseKind ?? "transient_force_pause";
            s.ForcePauseSinceMs = 0; s.ForcePauseKind = null;
            var tm = Find.TickManager;
            var restored = tm != null && tm.CurTimeSpeed == s.RequestedSpeed;
            if (!restored)
            {
                Stop(s, "external_pause", "Clock changed during a force pause; explicit player resume required.", false, null);
                return false;
            }
            Add("force_pause_cleared", "The " + (kind == "long_event" ? "long event" : "transient force pause")
                + " cleared after " + (waited / 1000) + "." + ((waited % 1000) / 100) + " s; speed "
                + (restored ? "restored to " + s.RequestedSpeed : "could NOT be restored to " + s.RequestedSpeed)
                + ". Play was never stopped.", s,
                new Dictionary<string, object?> { { "waitedMs", waited },
                    { "forcePauseKind", kind }, { "speedRestored", restored } });
            if (restored) return true;
            Stop(s, "start_refused", "The requested speed did not take after a force pause cleared.", true, null);
            return false;
        }

        /// Read by a human on stream. An unexplained pause is not evidence of a
        /// person: nothing in this process can see a keypress.
        private static string ExternalPauseDetail()
        {
            var auto = RecentAutoPauseLetters();
            if (auto.Count > 0)
                return "Game was paused by VANILLA, not by a person: " + string.Join("; ", auto.ToArray())
                    + " arrived and Prefs.AutomaticPauseMode = " + SafeAutoPauseMode()
                    + ", so LetterStack.ReceiveLetter called TickManager.Pause(). Deal with the letter, "
                    + "then restart supervised play. " + ClockState() + ".";
            return "Game was paused outside the supervisor (" + ClockState()
                + "), and no letter that vanilla auto-pauses for arrived in the last second "
                + "(Prefs.AutomaticPauseMode = " + SafeAutoPauseMode() + "). This is NOT evidence of "
                + "human input -- nothing here can see a keypress, and a dialog, a mod or a game event "
                + "pauses exactly the same way. Wait for explicit player resume.";
        }

        /// Letters on the stack that vanilla would have paused the game for,
        /// arrived within the last game-second. `LetterStack.ReceiveLetter`
        /// pauses when `Prefs.AutomaticPauseMode >= let.def.pauseMode`;
        /// `Letter.arrivalTick` is stamped in that same method.
        private static List<string> RecentAutoPauseLetters()
        {
            var result = new List<string>();
            try
            {
                var tm = Find.TickManager;
                if (tm == null) return result;
                foreach (var l in HomePlayUntilEventTools.Letters())
                {
                    if (l == null || l.def == null) continue;
                    if ((int)Prefs.AutomaticPauseMode < (int)l.def.pauseMode) continue;
                    if (tm.TicksGame - l.arrivalTick > 60) continue;
                    result.Add(BridgeCommon.SafeString(() => l.Label.ToString())
                        + " (" + l.def.defName + ", pauseMode " + l.def.pauseMode + ")");
                }
            }
            catch { }
            return result;
        }

        private static string SafeAutoPauseMode()
        {
            try { return Prefs.AutomaticPauseMode.ToString(); } catch { return "unreadable"; }
        }

        // Letter defNames and MessageTypeDef names that inform without stopping.
        // Everything else -- ThreatBig, ThreatSmall, Death/PawnDeath, choice
        // letters, anything a mod adds -- still stops the clock.
        private static readonly HashSet<string> NonStoppingLetterDefs = new HashSet<string>(StringComparer.Ordinal) {
            "NeutralEvent", "PositiveEvent", "NegativeEvent" };
        private static readonly HashSet<string> NonStoppingMessageTypes = new HashSet<string>(StringComparer.Ordinal) {
            "NeutralEvent", "PositiveEvent", "HistoricalEvent", "NegativeEvent", "NegativeHealthEvent", "SituationResolved" };

        /// Facts that change under a running epoch without a controller write
        /// or a stop, and that the controller's cross-step FactCache would
        /// otherwise keep serving: a research project finishing changes the
        /// research and definitions families (recipes and buildables it
        /// unlocks); a faction relation or goodwill move changes the world
        /// family; a game condition starting or ending changes the colony
        /// family. The first probe of an epoch only baselines; each later
        /// change appends one observation_invalidated row naming the stale
        /// families, coalesced per probe. A zone edit (cells added or removed,
        /// a zone created or deleted) appends its own row narrowed to the
        /// changed zones' ids and the one rectangle their old and new cells
        /// span (#359), so a controller store keeps the rest of the colony
        /// family; past the id bound the row names the family alone.
        private static void PublishFactChanges(State s)
        {
            var research = ResearchDigest();
            var world = WorldDigest();
            var conditions = ConditionDigest(s.Map);
            var families = new List<string>();
            var reasons = new List<string>();
            if (s.ResearchDigest != null && s.ResearchDigest != research) { families.Add("research"); families.Add("definitions"); reasons.Add("research " + research); }
            if (s.WorldDigest != null && s.WorldDigest != world) { families.Add("world"); reasons.Add("faction relations changed"); }
            if (s.ConditionDigest != null && s.ConditionDigest != conditions) { families.Add("colony"); reasons.Add("game conditions " + (conditions == "" ? "cleared" : conditions)); }
            s.ResearchDigest = research; s.WorldDigest = world; s.ConditionDigest = conditions;
            if (families.Count != 0)
                Add("observation_invalidated", "Observed facts changed: " + string.Join("; ", reasons) + ".", s,
                    new Dictionary<string, object?> { { "families", families }, { "reason", string.Join("; ", reasons) } });
            PublishZoneChanges(s);
        }
        private static void PublishZoneChanges(State s)
        {
            var zones = ZoneDigests(s.Map);
            var before = s.ZoneDigests;
            s.ZoneDigests = zones;
            if (before == null) return;
            var changed = new List<string>();
            ZoneDigest? span = null;
            foreach (var pair in zones)
            {
                ZoneDigest old;
                if (before.TryGetValue(pair.Key, out old) && old.Count == pair.Value.Count && old.Hash == pair.Value.Hash) continue;
                changed.Add(pair.Key);
                span = ZoneDigest.Union(span, pair.Value);
                if (before.TryGetValue(pair.Key, out old)) span = ZoneDigest.Union(span, old);
            }
            foreach (var pair in before)
            {
                if (zones.ContainsKey(pair.Key)) continue;
                changed.Add(pair.Key);
                span = ZoneDigest.Union(span, pair.Value);
            }
            if (changed.Count == 0) return;
            changed.Sort(StringComparer.Ordinal);
            var reason = "zones edited: " + string.Join(", ", changed);
            var payload = new Dictionary<string, object?> { { "families", new List<string> { "colony" } }, { "reason", reason } };
            if (changed.Count <= InvalidationEntitiesMax && span != null)
            {
                payload["entityIds"] = changed;
                payload["cells"] = new Dictionary<string, object?> { { "minX", span.MinX }, { "minZ", span.MinZ }, { "maxX", span.MaxX }, { "maxZ", span.MaxZ } };
            }
            Add("observation_invalidated", "Observed facts changed: " + reason + ".", s, payload);
        }
        /// One zone's cell set as a count, an order-independent hash and its
        /// bounds; equal count and hash read as unchanged.
        private sealed class ZoneDigest
        {
            public int Count; public int Hash; public int MinX, MinZ, MaxX, MaxZ;
            public static ZoneDigest? Union(ZoneDigest? a, ZoneDigest b)
            {
                if (b.Count == 0) return a;
                if (a == null) return new ZoneDigest { Count = b.Count, MinX = b.MinX, MinZ = b.MinZ, MaxX = b.MaxX, MaxZ = b.MaxZ };
                return new ZoneDigest { Count = a.Count + b.Count, MinX = Math.Min(a.MinX, b.MinX), MinZ = Math.Min(a.MinZ, b.MinZ), MaxX = Math.Max(a.MaxX, b.MaxX), MaxZ = Math.Max(a.MaxZ, b.MaxZ) };
            }
        }
        private static Dictionary<string, ZoneDigest> ZoneDigests(Map map)
        {
            var result = new Dictionary<string, ZoneDigest>(StringComparer.Ordinal);
            List<Zone> zones;
            try { zones = map?.zoneManager?.AllZones ?? new List<Zone>(); } catch { return result; }
            foreach (var zone in zones)
            {
                if (zone == null) continue;
                string id;
                try { id = zone.GetUniqueLoadID(); } catch { continue; }
                if (!ProtoBoundary.IsIdentifier(id)) continue;
                var digest = new ZoneDigest { MinX = int.MaxValue, MinZ = int.MaxValue, MaxX = int.MinValue, MaxZ = int.MinValue };
                foreach (var cell in zone.Cells)
                {
                    digest.Count++;
                    unchecked { digest.Hash += cell.x * 73856093 ^ cell.z * 19349663; }
                    if (cell.x < digest.MinX) digest.MinX = cell.x;
                    if (cell.z < digest.MinZ) digest.MinZ = cell.z;
                    if (cell.x > digest.MaxX) digest.MaxX = cell.x;
                    if (cell.z > digest.MaxZ) digest.MaxZ = cell.z;
                }
                result[id] = digest;
            }
            return result;
        }
        private static string ResearchDigest()
        {
            var manager = Find.ResearchManager;
            if (manager == null) return "";
            var finished = 0;
            foreach (var project in DefDatabase<ResearchProjectDef>.AllDefsListForReading) if (project.IsFinished) finished++;
            var current = manager.GetProject();
            return finished + ":" + (current != null ? current.defName : "");
        }
        // Game conditions (toxic fallout, blight, a cold snap) arrive as
        // non-stopping NegativeEvent letters, so the set affecting the map is
        // digested the same way: a change invalidates the colony family whose
        // environment census carries it.
        private static string ConditionDigest(Map map)
        {
            var conditions = new List<GameCondition>();
            try { map?.gameConditionManager?.GetAllGameConditionsAffectingMap(map, conditions); } catch { return ""; }
            return string.Join(",", conditions.Select(c => c.def.defName + "#" + c.uniqueID).OrderBy(n => n, StringComparer.Ordinal));
        }
        private static string WorldDigest()
        {
            var factions = Find.FactionManager;
            if (factions == null) return "";
            var parts = new List<string>();
            foreach (var faction in factions.AllFactionsListForReading)
            {
                if (faction.IsPlayer) continue;
                parts.Add(faction.loadID + "=" + faction.PlayerRelationKind + "/" + faction.PlayerGoodwill + "/" + (faction.defeated ? "d" : "a"));
            }
            return string.Join(",", parts);
        }
        private static Hit? Probe(State s)
        {
            var newLetters = new List<Dictionary<string, object?>>();
            foreach (var l in HomePlayUntilEventTools.Letters())
            {
                var id = HomePlayUntilEventTools.SafeLetterId(l);
                if (!s.Letters.Add(id)) continue;
                var label = l.Label.ToString();
                // Ordinary announcements -- neutral, positive AND negative (a
                // short circuit, a dead crop, a departed visitor) -- inform
                // Hands without interrupting time; `negative` marks the ones
                // the relay wakes a parked Hands for. Threat, death, decision
                // and unknown letter classes stop. (2026-09-07: each negative
                // announcement had been costing a manual restart on stream.)
                if (l.GetType() == typeof(StandardLetter) && !l.ShouldAutomaticallyOpenLetter && l.def != null
                    && NonStoppingLetterDefs.Contains(l.def.defName))
                {
                    Add("notification_new", label, s, new Dictionary<string, object?> {
                        { "id", id }, { "label", label }, { "letterDef", l.def.defName },
                        { "negative", l.def.defName == "NegativeEvent" } });
                    continue;
                }
                newLetters.Add(new Dictionary<string, object?> { { "id", id }, { "label", label },
                    { "letterDef", l.def != null ? l.def.defName : null }, { "arrivalTick", SafeArrivalTick(l) } });
            }
            var newMessages = new List<Dictionary<string, object?>>();
            foreach (var m in HomePlayUntilEventTools.LiveMessages())
            {
                var id = HomePlayUntilEventTools.SafeMessageId(m);
                if (!s.Messages.Add(id)) continue;
                var type = HomePlayUntilEventTools.SafeMessageType(m);
                var text = HomePlayUntilEventTools.SafeMessageText(m) ?? "Transient message";
                var payload = new Dictionary<string, object?> { { "id", id }, { "messageType", type },
                    { "text", text }, { "startingTick", HomePlayUntilEventTools.SafeMessageTick(m) } };
                // Same rule for the transient top-left messages: only
                // ThreatBig, ThreatSmall, PawnDeath and an unknown type stop.
                if (type != null && NonStoppingMessageTypes.Contains(type))
                {
                    payload["negative"] = type == "NegativeEvent" || type == "NegativeHealthEvent";
                    Add("notification_new", text, s, payload);
                    continue;
                }
                if (type == null || !(new[] { "RejectInput", "CautionInput", "SilentInput", "TaskCompletion" }).Contains(type))
                    newMessages.Add(payload);
            }
            if (newLetters.Count > 0 || newMessages.Count > 0)
            {
                var names = newLetters.Select(x => Convert.ToString(x["label"]))
                    .Concat(newMessages.Select(x => Convert.ToString(x["text"]))).ToList();
                var payload = new Dictionary<string, object?> { { "letters", newLetters },
                        { "messages", newMessages }, { "letterCount", newLetters.Count },
                        { "messageCount", newMessages.Count } };
                // The newest arrival is the hazard's occurrence (#626): the
                // older ones were already stopping play had they been seen.
                var arrived = newLetters.Select(x => x["arrivalTick"]).Concat(newMessages.Select(x => x["startingTick"]))
                    .Where(t => t != null).Select(t => Convert.ToInt64(t)).Where(t => t >= 0).ToList();
                if (arrived.Count > 0) payload["occurrenceTick"] = arrived.Max();
                return new Hit("notification_batch", names.Count + " new notification(s): " + string.Join("; ", names), payload);
            }
            // An alert never stops play; a new one is a message. The key set only
            // grows within an epoch, so an alert that flaps cannot re-report.
            foreach (var a in HomePlayUntilEventTools.ActiveAlertKeys(AlertPriority.High, null))
                if (s.AlertKeys.Add(a.Key)) Add("alert_new", a.Value, s, AlertRow(a));
            var pawns = HomePlayUntilEventTools.SpawnedPawns(Find.CurrentMap);
            var colonists = pawns.Where(HomePlayUntilEventTools.SafeIsColonist).ToList();
            foreach (var identity in s.MedicalRest)
            {
                var patient = colonists.FirstOrDefault(p => p.thingIDNumber == identity);
                if (patient == null || !MedicalRestSafety.Eligible(patient))
                    return new Hit("medical_rest_changed", "Resting patient requires a fresh medical review",
                        new Dictionary<string, object?> { { "pawnId", identity } });
            }
            var tick = Find.TickManager != null ? Find.TickManager.TicksGame : s.LastTick;
            CheckHostilesCleared(s, pawns);
            foreach (var p in pawns)
            {
                string why;
                if (!HomePlayUntilEventTools.SafeIsColonist(p)
                    && !HomePlayUntilEventTools.SafeDowned(p) && !HomePlayUntilEventTools.SafeDead(p)
                    && HomePlayUntilEventTools.IsHostile(p, out why)
                    && !s.IgnoredHostiles.Contains(p.thingIDNumber)
                    && colonists.Any(c => Distance(p, c) <= s.HostileWithin))
                    return PawnHit("hostile", p, why, SpawnedTick(p));
                if (HomePlayUntilEventTools.SafeIsColonist(p)
                    && (HomePlayUntilEventTools.SafeDowned(p) || HomePlayUntilEventTools.SafeDead(p))
                    && !s.IgnoredDowned.Contains(p.thingIDNumber)
                    && !SafeSurgicalRecovery(s, p)
                    && !(s.MedicalRest.Contains(p.thingIDNumber) && MedicalRestSafety.Eligible(p)))
                    return PawnHit("colonist_downed", p, HomePlayUntilEventTools.SafeDead(p) ? "dead" : "downed", DownedTick(p));
                if (ThreateningPredatorHunt(p)
                    && !s.IgnoredHostiles.Contains(p.thingIDNumber)
                    && colonists.Any(c => Distance(p, c) <= 40))
                    return PawnHit("predator_hunt", p, "PredatorHunt targeting colony property or unreadable prey within 40 cells", JobStartTick(p));
                if (HomePlayUntilEventTools.SafeIsColonist(p))
                {
                    if (p.CurJobDef == JobDefOf.Hunt)
                    {
                        var prey = p.CurJob.targetA.Thing as Pawn;
                        if (prey != null && !prey.Dead && !HuntingSafety.RouteSafe(p, prey))
                            return PawnHit("hunting_route_unsafe", p,
                                "Prey has an unsafe death effect, or the hunter's route is unavailable or near a predator", JobStartTick(p));
                    }
                    var after = InjurySnapshot.Capture(p);
                    InjurySnapshot before;
                    if (!s.Injuries.TryGetValue(p.thingIDNumber, out before))
                    {
                        // A newly joined/spawned colonist gets a baseline on
                        // first sight; subsequent worsening is guarded.
                        s.Injuries[p.thingIDNumber] = after;
                        continue;
                    }
                    // Both thresholds are crossings from the epoch's start
                    // baseline. A colonist already under minHealthFraction
                    // when the epoch started is not news: the stop that put
                    // them there was already reported, and re-stopping every
                    // window at zero ticks would pin the clock while the
                    // raider who hurt them still stands (issue #154).
                    if (before != null
                        && s.Mode == "combat"
                        && HealthThresholdCrossed(s, before, after))
                    {
                        var health = new Dictionary<string, object?> { { "pawnId", p.thingIDNumber },
                                { "pawnName", HomePlayUntilEventTools.SafeName(p) },
                                { "position", Position(p) }, { "healthAtStart", before.Health },
                                { "healthNow", after.Health }, { "minHealthFraction", s.MinHealthFraction },
                                { "healthDropFraction", s.HealthDropFraction } };
                        if (after.NewestWoundAgeTicks != int.MaxValue) health["occurrenceTick"] = tick - after.NewestWoundAgeTicks;
                        return new Hit("colonist_health", HomePlayUntilEventTools.SafeName(p)
                            + " crossed a combat health threshold.", health);
                    }
                    if (before != null
                        && (after.Count > before.Count
                            || after.Severity > before.Severity + 0.01f
                            || after.BleedRate > before.BleedRate + 0.001f
                            || after.BloodLoss > before.BloodLoss + 0.001f))
                    {
                        var payload = new Dictionary<string, object?> { { "pawnId", p.thingIDNumber },
                                { "pawnName", HomePlayUntilEventTools.SafeName(p) },
                                { "position", Position(p) }, { "injuryCountBefore", before.Count },
                                { "injuryCountAfter", after.Count }, { "severityBefore", before.Severity },
                                { "severityAfter", after.Severity }, { "bleedRateBefore", before.BleedRate },
                                { "bleedRateAfter", after.BleedRate }, { "bloodLossBefore", before.BloodLoss },
                                { "bloodLossAfter", after.BloodLoss }, { "healthAtStart", before.Health },
                                { "healthNow", after.Health } };
                        var threshold = HealthThresholdCrossed(s, before, after);
                        // Colony mode stops on any worsening -- except for a
                        // colonist who already caused a colonist_injury stop
                        // inside the cooldown, or who was acknowledged. A wolf
                        // scratching a pawn every few seconds must not turn each
                        // restart into another ten-second epoch. Threshold
                        // severity, downs and deaths still stop.
                        // A known wound bleeding on (blood loss or severity
                        // creeping) is not news: the tend alert already stands
                        // and a down still stops. Only a NEW wound or NEW
                        // bleeding is a colony-mode stop. Live 2026-09-06: two
                        // bleeding pawns re-fired this every few seconds with
                        // "injuries 3 -> 3, bleed 0.80 -> 0.80".
                        var newWound = after.Count > before.Count
                            || after.BleedRate > before.BleedRate + 0.001f;
                        var owedMs = InjuryCooldownRemainingMs(p.thingIDNumber, s.InjuryStopCooldownMs);
                        var acknowledged = s.IgnoredInjured.Contains(p.thingIDNumber);
                        var suppressed = !threshold && (owedMs > 0 || acknowledged);
                        payload["suppressed"] = suppressed;
                        payload["suppressedBy"] = suppressed ? (acknowledged ? "acknowledged" : "cooldown") : null;
                        payload["cooldownRemainingMs"] = owedMs;
                        payload["newWound"] = newWound;
                        if (after.Count > before.Count && after.NewestWoundAgeTicks != int.MaxValue && Find.TickManager != null)
                            payload["occurrenceTick"] = Find.TickManager.TicksGame - after.NewestWoundAgeTicks;
                        if (((s.Mode == "colony" && newWound) || threshold) && !suppressed)
                        {
                            RecordInjuryStop(p);
                            return new Hit("colonist_injury", InjuryDetail(s, p, before, after), payload);
                        }
                        Add("injury_observed", HomePlayUntilEventTools.SafeName(p)
                            + (suppressed
                                ? (acknowledged
                                    ? " took another injury; play continues under the configured injury acknowledgement."
                                    : " took another injury; play continues (within the "
                                        + (s.InjuryStopCooldownMs / 1000) + " s injury-stop cooldown).")
                                : (newWound
                                    ? " took a sub-threshold combat injury; play continues."
                                    : " has a known wound worsening (blood loss/severity creep); play continues.")), s, payload);
                        // Both modes deliberately coalesce ordinary damage into
                        // durable observations instead of pause storms.
                        after.Health = before.Health; // retain start-of-session health baseline
                        s.Injuries[p.thingIDNumber] = after;
                    }
                }
            }
            return null;
        }

        /// True when a colonist's summary health crossed under the policy's
        /// floor, or fell by the policy's drop fraction, since the epoch's
        /// start baseline. Health already under the floor at the baseline
        /// only trips on a further drop.
        private static bool HealthThresholdCrossed(State s, InjurySnapshot before, InjurySnapshot after)
        {
            return (before.Health > s.MinHealthFraction && after.Health <= s.MinHealthFraction)
                || before.Health - after.Health >= s.HealthDropFraction;
        }

        /// Non-stopping reminder for the moment a fight ends: the last hostile
        /// who could still act just went down or died. Ignored hostiles count --
        /// acknowledging a raider does not make them not a raider.
        private static void CheckHostilesCleared(State s, List<Pawn> pawns)
        {
            var conscious = 0;
            var downed = new List<Pawn>();
            var colonistsNear = pawns.Where(HomePlayUntilEventTools.SafeIsColonist).ToList();
            foreach (var p in pawns)
            {
                string why;
                if (p == null || HomePlayUntilEventTools.SafeIsColonist(p)) continue;
                if (HomePlayUntilEventTools.SafeDead(p)) continue;
                var hostile = HomePlayUntilEventTools.IsHostile(p, out why);
                if (HomePlayUntilEventTools.SafeDowned(p))
                {
                    // A downed manhunter loses its mental state and a wild animal
                    // has no faction, so IsHostile can read false on the ground.
                    // Anything downed, non-player and near a colonist gets back up.
                    if (hostile || (p.Faction != Faction.OfPlayer && p.RaceProps != null
                        && p.RaceProps.Animal && colonistsNear.Any(c => Distance(p, c) <= 30)))
                        downed.Add(p);
                    continue;
                }
                if (hostile) conscious++;
            }
            // First probe of an epoch has no in-epoch predecessor, so it reads
            // the previous epoch's last count instead.
            var before = s.ConsciousHostiles ?? _priorEpochConsciousHostiles;
            var first = s.ConsciousHostiles == null;
            s.ConsciousHostiles = conscious;
            _priorEpochConsciousHostiles = conscious;
            if (conscious > 0) { s.HostilesCleared = false; return; }
            if (before <= 0 || s.HostilesCleared) return;
            var drafted = pawns.Where(p => p != null && HomePlayUntilEventTools.SafeIsColonist(p)
                && !HomePlayUntilEventTools.SafeDowned(p) && !HomePlayUntilEventTools.SafeDead(p)
                && SafeDrafted(p)).ToList();
            // Across a restart, only fire when there is something left to do.
            if (first && downed.Count == 0 && drafted.Count == 0) return;
            s.HostilesCleared = true;
            var parts = new List<string>();
            if (downed.Count > 0)
                parts.Add(downed.Count + " downed (" + string.Join("; ", downed
                    .Select(p => HomePlayUntilEventTools.SafeName(p) + " at "
                        + p.Position.x + "," + p.Position.z).ToArray())
                    + ")");
            if (drafted.Count > 0)
                parts.Add(drafted.Count + " colonist" + (drafted.Count == 1 ? "" : "s")
                    + " still drafted (" + string.Join(", ", drafted
                        .Select(HomePlayUntilEventTools.SafeName).ToArray()) + ")");
            Add("hostiles_cleared", "No conscious hostiles remain: "
                + (parts.Count > 0 ? string.Join("; ", parts.ToArray())
                    : "nothing downed and nobody drafted") + ".", s,
                new Dictionary<string, object?> {
                    { "downedHostiles", downed.Select(p => (object)new Dictionary<string, object?> {
                        { "thingId", p.thingIDNumber }, { "name", HomePlayUntilEventTools.SafeName(p) },
                        { "x", p.Position.x }, { "z", p.Position.z } }).ToList() },
                    { "draftedColonists", drafted.Select(p => (object)new Dictionary<string, object?> {
                        { "thingId", p.thingIDNumber }, { "name", HomePlayUntilEventTools.SafeName(p) } }).ToList() },
                    { "consciousHostilesBefore", before },
                    { "acrossRestart", first } });
        }

        private static bool SafeDrafted(Pawn pawn) { try { return pawn.Drafted; } catch { return false; } }

        /// Milliseconds of injury-stop cooldown still owed to this pawn, or 0.
        private static long InjuryCooldownRemainingMs(int pawnId, long cooldownMs)
        {
            InjuryStop stop;
            if (cooldownMs <= 0 || !InjuryStops.TryGetValue(pawnId, out stop)) return 0;
            var remaining = stop.AtMs + cooldownMs - NowMs();
            return remaining > 0 ? remaining : 0;
        }

        private static void RecordInjuryStop(Pawn pawn)
        {
            var now = NowMs();
            foreach (var id in InjuryStops.Where(kv => now - kv.Value.AtMs > 3600000L)
                         .Select(kv => kv.Key).ToList())
                InjuryStops.Remove(id);
            InjuryStops[pawn.thingIDNumber] = new InjuryStop {
                AtMs = now, Tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0,
                Name = HomePlayUntilEventTools.SafeName(pawn) };
        }

        /// Observations only: the controller decides how to respond.
        private static string InjuryDetail(State s, Pawn p, InjurySnapshot before, InjurySnapshot after)
        {
            var name = HomePlayUntilEventTools.SafeName(p);
            return name + " was injured (injuries " + before.Count + " -> " + after.Count
                + ", bleed " + Num(before.BleedRate) + " -> " + Num(after.BleedRate)
                + "/day, blood loss " + Num(before.BloodLoss) + " -> " + Num(after.BloodLoss)
                + ", health " + Num(after.Health) + "). Game paused for review.";
        }

        private static string Num(float value) { return value.ToString("0.00", CultureInfo.InvariantCulture); }

        private static bool SafeSurgicalRecovery(State state, Pawn pawn)
        {
            if (!state.SurgicalRecovery.Contains(pawn.thingIDNumber)) return false;
            try {
                return !pawn.Dead && pawn.InBed() && pawn.health.summaryHealth.SummaryHealthPercent > 0.5f
                    && pawn.health.hediffSet.BleedRateTotal == 0f
                    && pawn.health.hediffSet.HasHediff(HediffDefOf.Anesthetic)
                    && !pawn.health.hediffSet.hediffs.Any(h => h.IsCurrentlyLifeThreatening);
            } catch { return false; }
        }

        private static Hit PawnHit(string kind, Pawn pawn, string reason, int? occurrenceTick = null)
        {
            var payload = new Dictionary<string, object?> { { "pawnId", pawn.thingIDNumber },
                    { "pawnName", HomePlayUntilEventTools.SafeName(pawn) }, { "position", Position(pawn) },
                    { "reason", reason } };
            // The tick the hazard arose where the game records one (#626), for
            // the stop's detect_ticks; absent when it does not.
            if (occurrenceTick.HasValue && occurrenceTick.Value >= 0) payload["occurrenceTick"] = occurrenceTick.Value;
            return new Hit(kind, HomePlayUntilEventTools.SafeName(pawn) + " (" + reason + ")", payload);
        }
        private static int SafeArrivalTick(Letter letter) { try { return letter.arrivalTick; } catch { return -1; } }
        private static int? JobStartTick(Pawn pawn) { try { var job = pawn.CurJob; return job != null && job.startTick >= 0 ? job.startTick : (int?)null; } catch { return null; } }
        /// One alert row; the key is type|priority|normalized-label.
        private static Dictionary<string, object?> AlertRow(KeyValuePair<string, string> a)
        {
            var parts = a.Key.Split('|');
            return new Dictionary<string, object?> { { "alertKey", a.Key }, { "label", a.Value },
                { "priority", parts.Length > 1 ? parts[1] : null } };
        }

        private static object Position(Pawn pawn) { return new Dictionary<string, object?> {
            { "x", pawn.Position.x }, { "z", pawn.Position.z } }; }

        private static List<string> ForcePausingWindows()
        {
            var windows = Find.WindowStack == null ? null : Find.WindowStack.Windows;
            return windows == null ? new List<string>() : windows
                .Where(w => w != null && w.forcePause).Select(w => w.GetType().FullName).ToList();
        }

        private static string ClockState()
        {
            var tm = Find.TickManager;
            if (tm == null) return "no tick manager";
            return "TimeSpeed." + tm.CurTimeSpeed + ", ForcePaused=" + tm.ForcePaused
                + (LongEventHandler.AnyEventNowOrWaiting ? ", a long event is running or queued" : "")
                + (Find.WindowStack != null && Find.WindowStack.WindowsForcePause ? ", WindowStack.WindowsForcePause" : "");
        }

        private static string ForcePauseDetail()
        {
            var names = ForcePausingWindows();
            if (names.Count == 0)
                return "no force-pausing window found; clock was paused by " + ClockState()
                    + ". There is no dialog to dismiss: do not go looking for one. A long event (the daily "
                    + "autosave) does this and is waited out for " + (ForcePauseGraceMs / 1000)
                    + " s without stopping play, so reaching this stop means it outlasted that. Restart "
                    + "supervised play.";
            return "Game is force-paused by " + string.Join(", ", names)
                + ". Read python ui.py for the visible title and buttons; close or answer it before restarting.";
        }

        private static bool PredatorHunting(Pawn p) { try { return p.CurJob != null && p.CurJob.def != null && p.CurJob.def.defName == "PredatorHunt"; } catch { return false; } }
        private static bool ThreateningPredatorHunt(Pawn p)
        {
            if (!PredatorHunting(p) || p.Faction == Faction.OfPlayer) return false;
            Pawn? prey;
            var ours = HomeStatusTools.PreyBelongsToPlayer(p, out prey);
            // Share the status reader's native ownership test. Re-evaluate each
            // probe, so a predator changing targets never gets a lasting exemption.
            return ours || prey == null;
        }
        private static int Distance(Pawn a, Pawn b) { return Math.Max(Math.Abs(a.Position.x - b.Position.x), Math.Abs(a.Position.z - b.Position.z)); }
        private static bool Owns([NotNullWhen(true)] State? s, string? owner, long epoch) { return s != null && s.Active && s.Epoch == epoch && string.Equals(s.Owner, owner ?? "agent", StringComparison.Ordinal); }
        private static void Stop(State? s, string kind, string? detail, bool pause,
            Dictionary<string, object?>? payload)
        {
            if (s == null || !ReferenceEquals(s, _state) || !s.Active) return;
            // The tick the stop was first raised at; a failed pause retries
            // per frame and keeps it, so the journaled stop tick can be later.
            if (s.PendingKind == null && Find.TickManager != null) s.DetectedTick = Find.TickManager.TicksGame;
            if (s.Typed != null) s.Typed.PauseRequested = pause;
            if (pause && Find.TickManager != null && Find.TickManager.CurTimeSpeed != TimeSpeed.Paused) Find.TickManager.Pause();
            s.PausedAtStop = Find.TickManager != null && Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
            s.PauseVerified = !pause || s.PausedAtStop;
            if (s.Typed != null) s.Typed.StopPauseVerified = ReferenceEquals(Current.Game, s.Session)
                && ReferenceEquals(Find.CurrentMap, s.Map) && s.PausedAtStop;
            if (pause && !s.PausedAtStop)
            {
                // Stay armed and retry on the next frame. Going inactive here
                // would turn a failed pause into unguarded play.
                s.PendingKind = kind; s.PendingDetail = detail; s.PendingPayload = payload;
                if (!s.PauseFailureReported)
                {
                    s.PauseFailureReported = true;
                    Add("pause_failed", "Pause did not take while handling " + kind + ": " + detail,
                        s, payload);
                }
                return;
            }
            s.PendingKind = null; s.PendingDetail = null; s.PendingPayload = null;
            RestoreBoost(s);
            s.StopAtMs = NowMs();
            payload = payload ?? new Dictionary<string, object?>();
            payload["detectedTick"] = s.DetectedTick;
            s.Active = false; s.StopReason = kind; s.StopDetail = detail; ClockPauseAccounting.Stopped(); Add(kind, detail, s, payload);
            LogTiming(s, kind);
            if (kind == "lease_expired") RevokeDisconnected(s);
        }
        private static void RestoreBoost(State s)
        {
            if (!s.BoostOwned) return;
            BoostField.SetValue(null, s.PriorBoost);
            s.BoostOwned = false;
        }
        private static void Add(string kind, string? detail, State s, Dictionary<string, object?>? payload)
        {
            var journal = EnsureJournal();
            var identity = (s.Session as Game)?.GetComponent<ColonyIdentity>();
            var row = new Dictionary<string, object?> { { "cursor", _cursor + 1 }, { "epoch", s.Epoch },
                { "kind", kind }, { "detail", detail }, { "event", payload },
                { "colonyId", identity?.ColonyId }, { "loadToken", identity?.LoadToken }, { "mapId", s.Map.uniqueID },
                { "tick", Find.TickManager != null ? Find.TickManager.TicksGame : s.LastTick }, { "atMs", NowMs() } };
            try { AttachTypedEvent(row, kind, detail, s, payload); AppendRow(journal, row); NoteUnackedRow(s, kind, _cursor, (int)row["tick"]!); }
            catch
            {
                var sameContext = ReferenceEquals(Current.Game, s.Session) && ReferenceEquals(Find.CurrentMap, s.Map);
                if (sameContext && Find.TickManager != null)
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                RestoreBoost(s);
                s.Active = false; s.StopReason = "event_journal_error"; ClockPauseAccounting.Stopped();
                if (s.Typed != null)
                {
                    s.Typed.PauseRequested = sameContext;
                    s.PausedAtStop = sameContext && Find.TickManager != null && Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
                    s.PauseVerified = s.PausedAtStop;
                    s.Typed.StopPauseVerified = s.PausedAtStop;
                    s.StopAtMs = NowMs(); s.StopDetail = "Canonical event publication failed.";
                    if (sameContext && !s.PausedAtStop) { s.Active = true; s.PendingKind = "event_journal_error"; s.PendingDetail = s.StopDetail; }
                }
                throw;
            }
        }
        // The only journal writer. Appending can re-enter through the authority
        // seam (a context read may revoke, which publishes), so a publication
        // that arrives mid-append is queued and written right after this row
        // instead of racing it for the same cursor.
        private static bool _appending;
        private static readonly List<Action> DeferredPublications = new List<Action>();
        private static void AppendRow(ClockEventJournal journal, Dictionary<string, object?> row)
        {
            if (_appending) throw new InvalidOperationException("Reentrant clock journal append");
            _appending = true;
            try { journal.Append(row); _cursor = journal.Newest; SignalWaiters(journal.Newest); }
            finally { _appending = false; }
            while (DeferredPublications.Count != 0)
            {
                var next = DeferredPublications[0]; DeferredPublications.RemoveAt(0);
                next();
            }
        }
        // Epoch-less producers (authority changes observed while no epoch is
        // running). The row carries no owner; the current map supplies its
        // context. Nothing is armed, so a failed publication only logs.
        private static void Publish(string kind, string? detail, Dictionary<string, object?>? payload)
        {
            lock (Gate)
            {
                if (_appending) { DeferredPublications.Add(() => Publish(kind, detail, payload)); return; }
                try
                {
                    var journal = EnsureJournal();
                    if (Current.Game == null || Find.CurrentMap == null) return;
                    var identity = Current.Game.GetComponent<ColonyIdentity>();
                    var row = new Dictionary<string, object?> { { "cursor", _cursor + 1 }, { "epoch", 0L },
                        { "kind", kind }, { "detail", detail }, { "event", payload },
                        { "colonyId", identity?.ColonyId }, { "loadToken", identity?.LoadToken }, { "mapId", Find.CurrentMap.uniqueID },
                        { "tick", Find.TickManager != null ? Find.TickManager.TicksGame : 0 }, { "atMs", NowMs() } };
                    if (!AttachOwnerlessEvent(row, kind, detail, payload)) return;
                    AppendRow(journal, row);
                }
                catch (Exception error) { Log.Warning("RimGovernor clock publication of " + kind + " failed: " + error.GetType().Name); }
            }
        }
        private static object Snapshot(State? s, bool success)
        {
            EnsureJournal();
            return new Dictionary<string, object?> { { "success", success }, { "active", s != null && s.Active },
                { "durableEvents", true },
                { "epoch", s != null ? s.Epoch : 0 }, { "owner", s != null ? s.Owner : null },
                { "requestedSpeed", s != null ? s.RequestedSpeed.ToString() : null },
                { "mode", s != null ? s.Mode : null },
                { "hostileWithin", s != null ? s.HostileWithin : 0 },
                { "healthDropFraction", s != null ? s.HealthDropFraction : 0 },
                { "minHealthFraction", s != null ? s.MinHealthFraction : 0 },
                { "lastTick", s != null ? s.LastTick : 0 },
                { "nativeTickBoundary", true },
                { "nativeTestAcceleration", BoostField != null && BoostField.FieldType == typeof(bool) },
                { "testAcceleration", s != null && s.TestAcceleration },
                { "boostOwned", s != null && s.BoostOwned },
                { "maxProbeTickGap", s != null ? s.MaxProbeTickGap : 0 },
                { "probeCount", s != null ? s.ProbeCount : 0 },
                { "probeElapsedMs", s != null ? Ms(s.Timing.ProbeElapsed) : 0 }, { "digestElapsedMs", s != null ? Ms(s.Timing.DigestElapsed) : 0 },
                { "digestCount", s != null ? s.Timing.Digests : 0 },
                { "stopAtMs", s != null ? (object?)s.StopAtMs : null },
                { "pausedMs", ClockPauseAccounting.PausedMs(Current.Game) }, { "runningMs", ClockPauseAccounting.RunningMs(Current.Game) },
                { "probeTickLimit", ProbeIntervalTicks },
                { "blindTickBudget", s != null ? s.BlindTickBudget : 0 }, { "maxTicksPerSecond", s != null ? s.MaxTicksPerSecond : 0 },
                { "regulatedTicksPerSecond", s != null ? s.RegulatedTicksPerSecond : 0 }, { "blindTicks", s != null ? BlindTicks(s) : 0 },
                { "maxBlindTicks", s != null ? s.MaxBlindTicks : 0 }, { "regulatorThrottles", s != null ? s.RegulatorThrottles : 0 },
                { "startTick", s != null ? (object)s.StartTick : null },
                { "tickDeadline", s != null ? (object?)s.TickDeadline : null },
                { "injuryStopCooldownMs", s != null ? s.InjuryStopCooldownMs : 0 },
                { "suppressedInjuryPawns", s != null ? (object)s.SuppressedInjuries : null },
                { "baselineAlerts", s != null ? (object)s.BaselineAlerts : null },
                { "paused", s != null ? (object)s.PausedAtStop : null },
                { "forcePauseWaitingMs", s != null && s.ForcePauseSinceMs != 0 ? (object)(NowMs() - s.ForcePauseSinceMs) : null },
                { "forcePauseKind", s != null ? s.ForcePauseKind : null },
                { "pauseVerified", s != null ? (object?)s.PauseVerified : null },
                { "sessionChanged", s != null && s.StopReason == "session_changed" },
                { "leaseExpiresAtMs", s != null ? NowMs() + Math.Max(0, s.LeaseExpiresMs - LeaseNow(s)) : 0 }, { "leaseRemainingMs", s != null ? Math.Max(0, s.LeaseExpiresMs - LeaseNow(s)) : 0 },
                { "stopReason", s != null ? s.StopReason : null }, { "stopDetail", s != null ? s.StopDetail : null },
                { "newestCursor", _cursor }, { "patchError", _patchError } };
        }
        private static object Failure(string message) { return new Dictionary<string, object?> { { "success", false }, { "message", message }, { "retryable", false }, { "refusal", null }, { "newestCursor", _cursor } }; }
        /// A refusal the caller should retry rather than report: the condition
        /// clears on its own within a second or two.
        private static object Retryable(string message, string refusal) { return new Dictionary<string, object?> { { "success", false }, { "message", message }, { "retryable", true }, { "refusal", refusal }, { "newestCursor", _cursor } }; }
        private static HashSet<string> Csv(string value) { return new HashSet<string>((value ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase); }
        private static HashSet<int> PawnIds(string value) { var r = new HashSet<int>(); foreach (var x in Csv(value)) { int n; var digits = new string(x.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()); if (int.TryParse(digits, out n)) r.Add(n); } return r; }
        private static int Clamp(int x, int lo, int hi) { return Math.Max(lo, Math.Min(hi, x)); }
        private static float ClampFloat(float x, float lo, float hi) { return Math.Max(lo, Math.Min(hi, x)); }
        private static long NowMs() { return (DateTime.UtcNow.Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond; }

        // Where an epoch's wall time went, per epoch, for the acceptance
        // speed work (#265): game ticks and frames, the tick-boundary hook,
        // the watch checks inside it, the periodic hazard probe and, apart
        // from it (#626), the fact-change digests. Logged once per stop, only
        // under the acceptance test-acceleration launch flag.
        private sealed class EpochTiming
        {
            public long HookTicks, HookElapsed, WatchElapsed, ProbeElapsed, DigestElapsed;
            public int Digests;
            public int Frames, FrameTicks, MaxFrameTicks;
            public long StartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        private static double Ms(long stopwatchTicks) { return stopwatchTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        private static void LogTiming(State s, string kind)
        {
            if (!TestAccelerationLaunch) return;
            var t = s.Timing;
            var wall = Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t.StartedAt);
            var ticks = s.LastTick - s.StartTick;
            Log.Message(string.Format(CultureInfo.InvariantCulture,
                "RimGovernor clock epoch {0} timing: stop={1} speed={2} boost={3} ticks={4} wallMs={5:F0} tps={6:F0} frames={7} maxTicksPerFrame={8} hookTicks={9} hookMs={10:F1} watchMs={11:F1} probes={12} probeMs={13:F1} maxProbeTickGap={14} blindBudget={15} maxBlind={16} throttles={17} digests={18} digestMs={19:F1}",
                s.Epoch, kind, s.RequestedSpeed, s.TestAcceleration, ticks, wall, wall > 0 ? ticks * 1000.0 / wall : 0,
                t.Frames, t.MaxFrameTicks, t.HookTicks, Ms(t.HookElapsed), Ms(t.WatchElapsed), s.ProbeCount, Ms(t.ProbeElapsed), s.MaxProbeTickGap,
                s.BlindTickBudget, s.MaxBlindTicks, s.RegulatorThrottles, t.Digests, Ms(t.DigestElapsed)));
        }

        private sealed class Hit
        {
            public readonly string Kind; public readonly string Detail;
            public readonly Dictionary<string, object?> Payload;
            public Hit(string kind, string detail, Dictionary<string, object?> payload)
            { Kind = kind; Detail = detail; Payload = payload; }
        }

        private sealed class InjurySnapshot
        {
            public int Count; public float Severity; public float BleedRate; public float BloodLoss; public float Health;
            // Age in ticks of the youngest injury, for the stop's occurrence tick.
            public int NewestWoundAgeTicks = int.MaxValue;
            public static InjurySnapshot Capture(Pawn pawn)
            {
                var result = new InjurySnapshot();
                try
                {
                    result.Health = pawn.health.summaryHealth.SummaryHealthPercent;
                    var hediffs = pawn.health.hediffSet.hediffs;
                    foreach (var h in hediffs)
                    {
                        var injury = h as Hediff_Injury;
                        if (injury != null) { result.Count++; result.Severity += injury.Severity; result.BleedRate += injury.BleedRate; result.NewestWoundAgeTicks = Math.Min(result.NewestWoundAgeTicks, Math.Max(0, injury.ageTicks)); }
                        if (h.def == HediffDefOf.BloodLoss) result.BloodLoss = Math.Max(result.BloodLoss, h.Severity);
                    }
                }
                catch { }
                return result;
            }
        }

        private sealed class State
        {
            public TypedEpoch? Typed;
            public State(object session, Map map) { Session = session; Map = map; }
            public bool Active; public long Epoch; public string? Owner; public readonly object Session; public readonly Map Map; public TimeSpeed RequestedSpeed;
            public string? Mode; public float HostileWithin; public float HealthDropFraction; public float MinHealthFraction;
            public long LeaseExpiresMs; public long LastProbeMs; public int LastTick; public bool PausedAtStop;
            public int StartTick; public long? TickDeadline;
            public bool TestAcceleration; public bool PriorBoost; public bool BoostOwned;
            // Blind-tick regulator (SupervisedPlayRegulator.cs): the budget and
            // owner ceiling from the start, the ceiling the regulator holds
            // (0 = none), the controller's last read tick and acknowledged
            // cursor, and the rows it has not acknowledged (cursor, tick).
            public int BlindTickBudget; public int MaxTicksPerSecond; public int RegulatedTicksPerSecond;
            public int LastReadTick; public long AckedCursor; public readonly List<KeyValuePair<long, int>> Unacked = new List<KeyValuePair<long, int>>();
            public long LastRampMs; public int MaxBlindTicks; public int RegulatorThrottles;
            public int LastProbeTick; public int MaxProbeTickGap; public int ProbeCount;
            // The digest cadence (#626) and the probe requests direct game
            // hooks raised (hazard class -> tick), served at the next probe.
            public int LastDigestTick; public bool ProbeRequested;
            public readonly List<KeyValuePair<string, int>> ProbeRequests = new List<KeyValuePair<string, int>>();
            public readonly EpochTiming Timing = new EpochTiming();
            public long? StopAtMs;
            public int DetectedTick;
            public bool? PauseVerified; public bool PauseFailureReported;
            public string? StopReason; public string? StopDetail;
            public string? PendingKind; public string? PendingDetail;
            // null until the epoch's first probe baselined them; see PublishFactChanges.
            public string? ResearchDigest; public string? WorldDigest; public string? ConditionDigest;
            public Dictionary<string, ZoneDigest>? ZoneDigests;
            // Wall-clock ms at which a windowless force pause began, 0 when none.
            public long ForcePauseSinceMs; public string? ForcePauseKind;
            public Dictionary<string, object?>? PendingPayload;
            public readonly HashSet<string> Letters = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Messages = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<int, InjurySnapshot> Injuries = new Dictionary<int, InjurySnapshot>();
            // Grows only: an alert that clears and returns is not news again.
            public readonly HashSet<string> AlertKeys = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<Dictionary<string, object?>> BaselineAlerts = new List<Dictionary<string, object?>>();
            // null until the first probe of this epoch has counted.
            public int? ConsciousHostiles; public bool HostilesCleared;
            public HashSet<int> IgnoredHostiles = new HashSet<int>(); public HashSet<int> IgnoredDowned = new HashSet<int>(); public HashSet<int> SurgicalRecovery = new HashSet<int>(); public HashSet<int> MedicalRest = new HashSet<int>();
            public HashSet<int> IgnoredInjured = new HashSet<int>(); public int InjuryStopCooldownMs;
            public readonly List<Dictionary<string, object?>> SuppressedInjuries = new List<Dictionary<string, object?>>();
        }

        private sealed class InjuryStop
        {
            public long AtMs; public int Tick; public string? Name;
        }
    }
}
