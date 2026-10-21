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
using RimGovernor.Brain.Bridge;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    internal static partial class Supervisor
    {
        private static readonly FieldInfo BoostField = typeof(TickManager).GetField("UltraSpeedBoost", BindingFlags.Static | BindingFlags.NonPublic)!;
        // Wall-clock grace for a force pause with no window behind it.
        // An autosave takes about a second; 20 s is generous and still
        // far short of anything a person would sit through.
        private const int ForcePauseGraceMs = 20000;
        private static readonly object Gate = new object();
        // The colony events pushed to the brain (ColonyEventChannel): bounded, written without blocking, losses counted.
        internal static readonly RimGovernor.Brain.Bridge.ColonyEventChannel ColonyEvents = new RimGovernor.Brain.Bridge.ColonyEventChannel();
        private static State? _state;
        // Bumped by every Stop that enters a hold; a resume must quote it (ClockHoldSerial.cs).
        private static readonly ClockHoldSerial HoldSerial = new ClockHoldSerial();
        // The player's in-game speed request, reported on Status (ClockSpeedRequest.cs).
        private static readonly ClockSpeedRequest SpeedRequest = new ClockSpeedRequest();
        // The one ceiling outlives a hold: a Run resumes under the heartbeat and
        // frame-budget state the previous run segment left, beaten at the Run.
        private static ClockCeiling? _ceiling;
        private static ClockCeiling SharedCeiling() => _ceiling ??= new ClockCeiling(NowMs());
        private static int _patched;
        private static string? _patchError;
        // Injury holds are remembered across run segments on purpose. Resuming the
        // clock re-baselines injuries, so without this a wound that keeps
        // arriving reads as brand new at every Run and holds play forever.
        private static readonly Dictionary<int, InjuryStop> InjuryStops = new Dictionary<int, InjuryStop>();
        private static object? _injuryStopsSession;
        // Conscious-hostile count at the last probe of the previous run segment. Like
        // the injury-stop memory this survives Run on purpose: a guard hold
        // and resume across the last kill must still raise the reminder.
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
                    prefix: new HarmonyMethod(typeof(Supervisor), nameof(OnFrameBegin)),
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
                EnsureTickRateClampPatched();
                EnsureHazardHooks();
                EnsureCombatHooks();
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

        // TickManagerUpdate checks Paused after each DoSingleTick. Pausing here
        // stops its frame batch at the boundary, including accelerated frames.
        internal static void OnTick()
        {
            GameThreadTrace.Mark("tick");
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
                // tick-bounded hazards before admitting another tick.
                if (s.Boosted) OnUpdate();
                if (!s.Active) return;
                if (!ReferenceEquals(Current.Game, s.Session) || !ReferenceEquals(Find.CurrentMap, s.Map))
                { Stop(s, Clock.HoldReason.SessionChanged, "Loaded game changed.", false); return; }
                var tm = Find.TickManager;
                if (tm == null) { Stop(s, Clock.HoldReason.Unavailable, "Tick manager disappeared.", false); return; }
                if (tm.TicksGame < s.LastTick)
                { Stop(s, Clock.HoldReason.SessionChanged, "Game clock rewound.", true); return; }
                s.LastTick = tm.TicksGame;
                CaptureTypedContext(s);
                if (StopInvalidTypedAuthority(s)) return;
                // An armed combat event stops at this tick's boundary.
                if (StopOnCombatEvent(s)) return;
                // The review tick the Run named: the brain's cadence hold, on the
                // tick boundary like a hazard (#3556). A tick already passed when
                // the Run landed holds here, at the first tick of the segment.
                if (tm.TicksGame >= s.HoldAtTick)
                { Stop(s, Clock.HoldReason.Review, "Review due at tick " + s.HoldAtTick + ".", true); return; }
                // Preserve player and letter attribution if the final tick also
                // changed the clock. The frame watcher handles those stops.
                if (tm.CurTimeSpeed != s.RequestedSpeed) return;
                // The tick-paced hazard probe: at every production speed
                // consecutive probes are at most ProbeIntervalTicks apart, however
                // many ticks the frame carries. The accelerated path ran it above.
                if (!s.Boosted)
                {
                    if (!RunProbeIfDue(s, tm)) return;
                    RunDigestIfDue(s, tm);
                }
            }
        }

        // The TickManagerUpdate postfix: one call per frame. Frame counts feed
        // the epoch timing summary; the monitoring itself is OnUpdate.
        internal static void OnFrame()
        {
            GameThreadTrace.Mark("frame postfix");
            lock (Gate)
            {
                MarkFrame();
                SampleEffective();
                var s = _state;
                if (s != null && s.Active)
                {
                    var tm = Find.TickManager;
                    if (tm != null)
                        {
                            var frameMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _frameBeganTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                            s.Ceiling.Frame(NowMs(), frameMs, tm.TicksThisFrame, (int)Math.Round(tm.TickRateMultiplier * NormalTicksPerSecond));
                            if (tm.TicksThisFrame > 0) NoteFrame(frameMs, frameMs > s.Ceiling.FrameBudgetMs);
                        }
                    s.Timing.Frames++;
                    if (s.Timing.FrameTicks > s.Timing.MaxFrameTicks) s.Timing.MaxFrameTicks = s.Timing.FrameTicks;
                    s.Timing.FrameTicks = 0;
                }
            }
            GameThreadTrace.Mark("frame update");
            OnUpdate();
            SnapshotStream.OnFrame();
            GameThreadTrace.Mark("frame postfix returned");
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
                    if (!ReferenceEquals(Current.Game, s.Session) || !ReferenceEquals(Find.CurrentMap, s.Map)) { Stop(s, Clock.HoldReason.SessionChanged, "Loaded game changed.", false); return; }
                    var tm = Find.TickManager;
                    if (tm == null) { Stop(s, Clock.HoldReason.Unavailable, "Tick manager disappeared.", false); return; }
                    if (tm.TicksGame < s.LastTick) { Stop(s, Clock.HoldReason.SessionChanged, "Game clock rewound.", true); return; }
                    s.LastTick = tm.TicksGame;
                    CaptureTypedContext(s);
                    if (s.Pending is { } pending)
                    {
                        Stop(s, pending, true);
                        return;
                    }
                    if (StopInvalidTypedAuthority(s)) return;
                    // A force pause is checked BEFORE the paused/speed tests: it is
                    // the more specific diagnosis, and a long event can hold the
                    // clock without ever touching CurTimeSpeed.
                    if (tm.ForcePaused) { HandleForcePause(s); return; }
                    if (s.ForcePauseSinceMs != 0 && !ResumeAfterForcePause(s)) return;
                    if (tm.CurTimeSpeed == TimeSpeed.Paused) {
                        // The letter LetterStack.ReceiveLetter paused for, when the pause hook attributed one.
                        var letter = LetterPauseHook.Consume();
                        Stop(s, letter == null ? new HoldCause(Clock.HoldReason.ExternalPause, ExternalPauseDetail())
                            : new HoldCause(Clock.HoldReason.LetterPause, ExternalPauseDetail(), PauseLetterId: letter), false);
                        return;
                    }
                    if (tm.CurTimeSpeed != s.RequestedSpeed) { Stop(s, Clock.HoldReason.ExternalSpeedChanged, "Speed changed outside the supervisor.", true); return; }
                    // An armed combat event is the stop, not the hazard probe's
                    // view of the same down: the down hook also requests a probe,
                    // and a Hyperfast tick runs this before TickBody's own
                    // combat check (#3551).
                    if (StopOnCombatEvent(s)) return;
                    if (!RunProbeIfDue(s, tm)) return;
                    RunDigestIfDue(s, tm);
                }
                catch (Exception ex) { Stop(s, Clock.HoldReason.WatcherError, ex.GetType().Name + ": " + ex.Message, true); }
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
            Stop(s, hit, true);
            return false;
        }

        /// The fact-change digests on their own cadence: a game hook
        /// marked one dirty (SupervisedPlayHooks.cs) or DigestIntervalTicks
        /// elapsed. Never part of the probe.
        private static void RunDigestIfDue(State s, TickManager tm)
        {
            RunStockpileFillIfDue(s, tm);
            if (!DigestDue(DigestDirty, tm.TicksGame - s.LastDigestTick)) return;
            DigestDirty = false;
            s.LastDigestTick = tm.TicksGame;
            var digestBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            PublishFactChanges(s);
            DismissProcessedLetters(s, tm);
            var took = System.Diagnostics.Stopwatch.GetTimestamp() - digestBegan;
            s.Timing.DigestElapsed += took; s.Timing.Digests++;
            ClockProbeAccounting.Digested(took);
        }

        /// Plain informational letters never leave the stack by themselves in
        /// vanilla (only a LetterWithTimeout with an active timeout does), so
        /// an autopiloted colony piled them up on the right edge. A letter is
        /// processed once this epoch's census published it (s.Letters) and a
        /// in-game hour has passed: its pause, if any, stopped a run
        /// the controller reviewed. Only exact StandardLetter is removed --
        /// choice letters (quests, joiners, ransoms) keep their own handling
        /// or run out through the game's timeout.
        internal const int ProcessedLetterAgeTicks = GenDate.TicksPerHour;
        private static void DismissProcessedLetters(State s, TickManager tm)
        {
            var stack = Find.LetterStack;
            if (stack == null) return;
            foreach (var l in GameWatchReads.Letters())
            {
                if (!DismissibleLetter(l.GetType() == typeof(StandardLetter), s.Letters.Contains(GameWatchReads.SafeLetterId(l)),
                    tm.TicksGame, SafeArrivalTick(l))) continue;
                stack.RemoveLetter(l);
            }
        }
        internal static bool DismissibleLetter(bool plain, bool published, int now, int arrival) =>
            plain && published && arrival >= 0 && now - arrival >= ProcessedLetterAgeTicks;

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
            if (windows.Count > 0 && ColonyNamingTools.Pending() != null)
            {
                // The naming prompt is the mod's to answer: confirm the game's
                // own suggestions now, in this frame, rather than stopping play
                // and waiting for the controller to notice and restart it.
                try { ColonyNamingTools.ConfirmSuggested(); } catch (Exception) { }
                windows = ForcePausingWindows();
                if (windows.Count == 0) return;
            }
            if (windows.Count > 0)
            {
                var naming = ColonyNamingTools.Pending() != null;
                var dialog = ChoiceDialogTools.Pending();
                // A choice dialog the game opened by itself is answered through
                // Operations.AnswerDialog once the controller has read its
                // options; it is named so the stop is actionable headless.
                Stop(s, naming ? Clock.HoldReason.ColonyNaming : dialog != null ? Clock.HoldReason.DialogPause : Clock.HoldReason.GameForced,
                    naming ? "RimWorld requests initial faction and settlement names."
                        : dialog != null ? "Game is force-paused by " + dialog.GetType().FullName + " (" + (ChoiceDialogTools.Title(dialog) ?? "") + "); read the colony facts dialog section and answer it."
                        : ForcePauseDetail(), true);
                return;
            }
            var now = NowMs();
            if (s.ForcePauseSinceMs == 0)
            {
                s.ForcePauseSinceMs = now;
                s.ForcePauseLongEvent = LongEventHandler.AnyEventNowOrWaiting;
                Publish(new ForcePauseWaiting(Origin(s),
                    (s.ForcePauseLongEvent
                        ? "A long event (an autosave, most likely) is holding the clock"
                        : "The clock is force-paused with no window behind it")
                    + "; waiting up to " + (ForcePauseGraceMs / 1000)
                    + " s for it to clear. Play is NOT stopped and nothing needs dismissing. "
                    + ClockState() + "."));
            }
            if (now - s.ForcePauseSinceMs < ForcePauseGraceMs) return;
            Stop(s, Clock.HoldReason.GameForced, ForcePauseDetail() + " It did not clear in "
                + (ForcePauseGraceMs / 1000) + " s, which is far longer than an autosave, "
                + "so this is no longer treated as transient.", true);
        }

        /// The force pause cleared. Put the requested speed back and carry on.
        /// -> false when the speed would not take, in which case play stopped.
        private static bool ResumeAfterForcePause(State s)
        {
            var waited = NowMs() - s.ForcePauseSinceMs;
            var longEvent = s.ForcePauseLongEvent;
            s.ForcePauseSinceMs = 0; s.ForcePauseLongEvent = false;
            var tm = Find.TickManager;
            if (tm == null || tm.CurTimeSpeed != s.RequestedSpeed)
            {
                Stop(s, Clock.HoldReason.ExternalPause, "Clock changed during a force pause; explicit player resume required.", false);
                return false;
            }
            Publish(new ForcePauseCleared(Origin(s), "The " + (longEvent ? "long event" : "transient force pause")
                + " cleared after " + (waited / 1000) + "." + ((waited % 1000) / 100) + " s; speed restored to " + s.Speed
                + ". Play was never stopped."));
            return true;
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
                foreach (var l in GameWatchReads.Letters())
                {
                    if (l == null || l.def == null) continue;
                    if ((int)Prefs.AutomaticPauseMode < (int)l.def.pauseMode) continue;
                    if (tm.TicksGame - l.arrivalTick > 60) continue;
                    result.Add(l.Label.ToString()
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
        /// or a stop, and that the controller's decoded fact store would
        /// otherwise keep serving: a research project finishing changes the
        /// research and definitions families (recipes and buildables it
        /// unlocks); a faction relation or goodwill move changes the world
        /// family; a game condition starting or ending changes the colony
        /// family. The first probe of an epoch only baselines; each later
        /// change appends one observation_invalidated row naming the stale
        /// families, coalesced per probe. A zone edit (cells added or removed,
        /// a zone created or deleted) appends its own row narrowed to the
        /// changed zones' ids and the one rectangle their old and new cells
        /// span, so a controller store keeps the rest of the colony
        /// family; past the id bound the row names the family alone.
        private static void PublishFactChanges(State s)
        {
            var research = ResearchDigest();
            var world = WorldDigest();
            var conditions = ConditionDigest(s.Map);
            var families = new List<Clock.FactFamily>();
            var reasons = new List<string>();
            if (s.ResearchDigest != null && s.ResearchDigest != research) { families.Add(Clock.FactFamily.Research); families.Add(Clock.FactFamily.Definitions); reasons.Add("research " + research); }
            if (s.WorldDigest != null && s.WorldDigest != world) { families.Add(Clock.FactFamily.World); reasons.Add("faction relations changed"); }
            if (s.ConditionDigest != null && s.ConditionDigest != conditions) { families.Add(Clock.FactFamily.Colony); reasons.Add("game conditions " + (conditions == "" ? "cleared" : conditions)); }
            s.ResearchDigest = research; s.WorldDigest = world; s.ConditionDigest = conditions;
            if (families.Count != 0) Invalidate(s, string.Join("; ", reasons), families);
            PublishZoneChanges(s);
            PublishHarvestChanges(s);
        }
        /// Publishes one ObservationInvalidated: the stale families, narrowed
        /// to the changed rows' ids and the one rectangle the change touched
        /// when the caller can attribute it.
        private static void Invalidate(State s, string reason, IReadOnlyList<Clock.FactFamily> families,
            IReadOnlyList<string>? entityIds = null, CellRectangle? cells = null) =>
            Publish(new ObservationInvalidated(Origin(s), "Observed facts changed: " + reason + ".", families, reason,
                entityIds ?? Array.Empty<string>(), cells));
        private static readonly Clock.FactFamily[] ColonyFamily = { Clock.FactFamily.Colony };
        /// A growing zone turning ripe (any sown plant harvestable now) or
        /// fully harvested appends one colony row narrowed to those zones
        ///, so the grower review runs without waiting for a stop.
        private static void PublishHarvestChanges(State s)
        {
            var ripe = RipeZones(s.Map);
            var before = s.RipeZones;
            s.RipeZones = ripe;
            if (before == null) return;
            var changed = ripe.Where(id => !before.Contains(id)).Concat(before.Where(id => !ripe.Contains(id))).ToList();
            if (changed.Count == 0) return;
            changed.Sort(StringComparer.Ordinal);
            var turned = changed.Where(ripe.Contains).ToList();
            var cleared = changed.Where(id => !ripe.Contains(id)).ToList();
            var parts = new List<string>();
            if (turned.Count != 0) parts.Add("zones harvestable: " + string.Join(", ", turned));
            if (cleared.Count != 0) parts.Add("zones harvested: " + string.Join(", ", cleared));
            Invalidate(s, string.Join("; ", parts), ColonyFamily, changed.Count <= InvalidationEntitiesMax ? changed : null);
        }
        private static HashSet<string> RipeZones(Map map)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            List<Zone> zones;
            try { zones = map?.zoneManager?.AllZones ?? new List<Zone>(); } catch { return result; }
            foreach (var zone in zones)
            {
                var growing = zone as Zone_Growing;
                if (growing == null) continue;
                string id;
                try { id = zone.GetUniqueLoadID(); } catch { continue; }
                if (!ProtoBoundary.IsIdentifier(id)) continue;
                try
                {
                    foreach (var cell in growing.Cells)
                    {
                        var plant = cell.GetPlant(map);
                        if (plant != null && plant.def == growing.GetPlantDefToGrow() && plant.HarvestableNow) { result.Add(id); break; }
                    }
                }
                catch { }
            }
            return result;
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
                ZoneDigest? old;
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
            if (changed.Count <= InvalidationEntitiesMax && span != null)
                Invalidate(s, reason, ColonyFamily, changed, new CellRectangle(span.MinX, span.MinZ, span.MaxX, span.MaxZ));
            else Invalidate(s, reason, ColonyFamily);
        }
        /// Stockpile fill on its own, shorter cadence (StockpileFillIntervalTicks):
        /// items arriving in or leaving a stockpile append one colony row
        /// narrowed to the changed zones' ids, so the controller's stockpile
        /// planner re-judges fill without waiting for the digest interval. The
        /// thresholds stay in the controller's policy; this only reports that
        /// a zone's used-cell count moved.
        private static void RunStockpileFillIfDue(State s, TickManager tm)
        {
            if (tm.TicksGame - s.LastFillTick < StockpileFillIntervalTicks) return;
            s.LastFillTick = tm.TicksGame;
            var fill = StockpileFill(s.Map);
            var before = s.StockpileFill;
            s.StockpileFill = fill;
            if (before == null) return;
            var changed = new List<string>();
            foreach (var pair in fill)
            {
                int old;
                if (!before.TryGetValue(pair.Key, out old) || old != pair.Value) changed.Add(pair.Key);
            }
            // A vanished zone is already a zone edit; only fill moves here.
            if (changed.Count == 0) return;
            changed.Sort(StringComparer.Ordinal);
            Invalidate(s, "stockpile fill: " + string.Join(", ", changed), ColonyFamily, changed.Count <= InvalidationEntitiesMax ? changed : null);
        }
        /// Used (not storage-empty) cells per stockpile zone, by zone id.
        private static Dictionary<string, int> StockpileFill(Map map)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (map == null) return result;
            List<Zone> zones;
            try { zones = map.zoneManager?.AllZones ?? new List<Zone>(); } catch { return result; }
            foreach (var zone in zones)
            {
                if (!(zone is Zone_Stockpile)) continue;
                string id;
                try { id = zone.GetUniqueLoadID(); } catch { continue; }
                if (!ProtoBoundary.IsIdentifier(id)) continue;
                var used = 0;
                try { foreach (var cell in zone.Cells) if (!NativeZoneCreation.StorageEmpty(cell, map)) used++; } catch { continue; }
                result[id] = used;
            }
            return result;
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
        /// Why the colony is lost (#2819), null while it is not: the game's
        /// own GameEnder is ending the game (set on the death or departure
        /// that left nobody, its game-over letter 400 ticks later), or no
        /// free colonist is left on the clock's map nor on any other map,
        /// caravan or travelling transporter. The letter itself never
        /// force-pauses; this hold is what stops the clock, and nothing opens
        /// the letter or creates new wanderers.
        internal static string? ColonyLostDetail(Map map)
        {
            if (Find.GameEnder is { gameEnding: true }) return "RimWorld is ending the game: everyone is dead or gone.";
            if (map.mapPawns.AnyFreeColonistSpawned || PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Count > 0) return null;
            return "No free colonist is left on the map, another map, a caravan or a travelling transporter.";
        }
        private static HoldCause? Probe(State s)
        {
            // A lost colony outranks every hazard: the last colonist's death
            // is a colonist_downed hit too, but there is nobody left to review.
            if (ColonyLostDetail(s.Map) is { } lost) return new HoldCause(Clock.HoldReason.ColonyLost, lost);
            var newLetters = new List<EventLetter>();
            var names = new List<string>();
            var arrived = new List<long>();
            foreach (var l in GameWatchReads.Letters())
            {
                var id = GameWatchReads.SafeLetterId(l);
                if (!s.Letters.Add(id)) continue;
                var label = l.Label.ToString();
                // Ordinary announcements -- neutral, positive AND negative (a
                // short circuit, a dead crop, a departed visitor) -- inform
                // Hands without interrupting time. Threat, death, decision
                // and unknown letter classes stop.
                if (l.GetType() == typeof(StandardLetter) && !l.ShouldAutomaticallyOpenLetter && l.def != null
                    && NonStoppingLetterDefs.Contains(l.def.defName))
                {
                    Publish(new LetterNotified(Origin(s), label, new EventLetter(id, label, l.def.defName)));
                    continue;
                }
                newLetters.Add(new EventLetter(id, label, l.def?.defName));
                names.Add(label);
                arrived.Add(SafeArrivalTick(l));
            }
            foreach (var m in GameWatchReads.LiveMessages())
            {
                var id = GameWatchReads.SafeMessageId(m);
                if (!s.Messages.Add(id)) continue;
                var type = GameWatchReads.SafeMessageType(m);
                var text = GameWatchReads.SafeMessageText(m) ?? "Transient message";
                // Same rule for the transient top-left messages: only
                // ThreatBig, ThreatSmall, PawnDeath and an unknown type stop.
                if (type != null && NonStoppingMessageTypes.Contains(type))
                {
                    Publish(new MessageNotified(Origin(s), text, id, text));
                    continue;
                }
                if (type == null || !(new[] { "RejectInput", "CautionInput", "SilentInput", "TaskCompletion" }).Contains(type))
                {
                    names.Add(text);
                    arrived.Add(GameWatchReads.SafeMessageTick(m));
                }
            }
            if (names.Count > 0)
            {
                // The newest arrival is the hazard's occurrence: the
                // older ones were already stopping play had they been seen.
                var known = arrived.Where(t => t >= 0).ToList();
                return new HoldCause(Clock.HoldReason.NotificationBatch, names.Count + " new notification(s): " + string.Join("; ", names),
                    new NotificationEvidence(newLetters), known.Count > 0 ? known.Max() : null);
            }
            // An alert never stops play; a new one is a message. The key set only
            // grows within an epoch, so an alert that flaps cannot re-report.
            foreach (var a in GameWatchReads.ActiveAlertKeys(AlertPriority.High, null))
                if (s.AlertKeys.Add(a.Key)) Publish(Alert(Origin(s), a));
            var pawns = GameWatchReads.SpawnedPawns(Find.CurrentMap);
            var colonists = pawns.Where(GameWatchReads.SafeIsColonist).ToList();
            // A resting patient who is no longer eligible needs a fresh
            // medical review, not a stopped clock: the watch is
            // discharged, the medical facts are invalidated and the planner
            // reviews under the running clock.
            foreach (var identity in s.MedicalRest.ToList())
            {
                var patient = colonists.FirstOrDefault(p => p.thingIDNumber == identity);
                if (patient != null && MedicalRestSafety.Eligible(patient, s.Policy!.SeriousSummaryHealthFloor.GetValueOrDefault())) continue;
                s.MedicalRest.Remove(identity);
                InvalidateMedicalFacts(s, identity, "resting patient " + identity + " requires a fresh medical review", true);
            }
            var tick = Find.TickManager != null ? Find.TickManager.TicksGame : s.LastTick;
            CheckHostilesCleared(s, pawns);
            foreach (var p in pawns)
            {
                string why;
                // A hostile in fog is undiscovered, as the emergency view
                // reads it: a sealed shrine's dormant guard stopped
                // every run at zero ticks, so the breach never ran.
                if (!GameWatchReads.SafeIsColonist(p)
                    && !GameWatchReads.SafeDowned(p) && !GameWatchReads.SafeDead(p)
                    && !(p.Spawned && p.Map != null && p.Position.Fogged(p.Map))
                    && GameWatchReads.IsHostile(p, out why)
                    && !s.IgnoredHostiles.Contains(p.thingIDNumber)
                    && colonists.Any(c => Distance(p, c) <= s.HostileWithin))
                    return PawnHit(Clock.HoldReason.Hostile, p, why, SpawnedTick(p));
                if (GameWatchReads.SafeIsColonist(p)
                    && (GameWatchReads.SafeDowned(p) || GameWatchReads.SafeDead(p))
                    && !s.IgnoredDowned.Contains(p.thingIDNumber)
                    && !SafeSurgicalRecovery(s, p)
                    && !(s.MedicalRest.Contains(p.thingIDNumber) && MedicalRestSafety.Eligible(p, s.Policy!.SeriousSummaryHealthFloor.GetValueOrDefault())))
                    return PawnHit(Clock.HoldReason.ColonistDowned, p, GameWatchReads.SafeDead(p) ? "dead" : "downed", DownedTick(p));
                // A colony-mode run holds when an unacknowledged colonist's
                // food reaches the brain's hunger category: the review that
                // answers it runs under a held clock, not a running one that
                // starves the pawn while it plans (#3435). A fight never holds
                // for hunger.
                if (s.Mode == "colony"
                    && GameWatchReads.SafeIsColonist(p)
                    && !GameWatchReads.SafeDowned(p) && !GameWatchReads.SafeDead(p)
                    && !s.IgnoredHungry.Contains(p.thingIDNumber)
                    && SafeHunger(p) is { } hunger && hunger >= s.HungerHold)
                    return PawnHit(Clock.HoldReason.ColonistNeed, p, hunger == HungerCategory.Starving ? "starving" : hunger == HungerCategory.UrgentlyHungry ? "urgently hungry" : "hungry");
                if (ThreateningPredatorHunt(p)
                    && !s.IgnoredHostiles.Contains(p.thingIDNumber)
                    && colonists.Any(c => Distance(p, c) <= 40))
                    return PawnHit(Clock.HoldReason.PredatorHunt, p, "PredatorHunt targeting colony property or unreadable prey within 40 cells", JobStartTick(p));
                if (GameWatchReads.SafeIsColonist(p))
                {
                    var after = InjurySnapshot.Capture(p);
                    InjurySnapshot? before;
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
                    // run at zero ticks would pin the clock while the
                    // raider who hurt them still stands.
                    // A combat epoch an open fight owns (SeriousInjury and
                    // Downed armed/#852) stops on those benign events
                    // instead: a colonist_health stop there is a hold that
                    // revokes authority mid-fight, the 2-3 s stop latency
                    // of #890.
                    var fightOwned = s.Mode == "combat" && CombatArmed(RimGovernor.Contracts.Wire.Clock.CombatEvent.SeriousInjury);
                    if (before != null
                        && s.Mode == "combat"
                        && !fightOwned
                        && HealthThresholdCrossed(s, before, after))
                    {
                        return new HoldCause(Clock.HoldReason.ColonistHealth, GameWatchReads.SafeName(p) + " crossed a combat health threshold.",
                            new HealthEvidence(PawnOf(p), s.MinHealthFraction, s.HealthDropFraction),
                            after.NewestWoundAgeTicks != int.MaxValue ? tick - after.NewestWoundAgeTicks : null);
                    }
                    if (before != null
                        && (after.Count > before.Count
                            || after.Severity > before.Severity + 0.01f
                            || after.BleedRate > before.BleedRate + 0.001f
                            || after.BloodLoss > before.BloodLoss + 0.001f))
                    {
                        var injury = new EventInjury(PawnOf(p), before.Severity, after.Severity);
                        var threshold = !fightOwned && HealthThresholdCrossed(s, before, after);
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
                        long? occurrence = after.Count > before.Count && after.NewestWoundAgeTicks != int.MaxValue && Find.TickManager != null
                            ? Find.TickManager.TicksGame - after.NewestWoundAgeTicks : null;
                        // A new wound stops the run only past the native
                        // severity floor: bleeding out inside the policy's
                        // injury_severity_floor_ticks, or a life-threatening
                        // hediff stage. Anything lighter is an event and a
                        // medical review under the running clock, which is
                        // what every stop bought anyway at the price of a
                        // stop-to-readmit pause. A combat threshold crossing
                        // still stops.
                        var severityFloorTicks = (int)s.Policy!.InjurySeverityFloorTicks.GetValueOrDefault();
                        var severe = InjurySeverityFloorReached(after.BleedOutTicks, after.LifeThreatening, severityFloorTicks);
                        var demoted = s.Mode == "colony" && newWound && !suppressed && !severe && !threshold;
                        if (((s.Mode == "colony" && newWound && severe) || threshold) && !suppressed)
                        {
                            RecordInjuryStop(p);
                            return new HoldCause(Clock.HoldReason.ColonistInjury, InjuryDetail(s, p, before, after), new InjuryEvidence(injury), occurrence);
                        }
                        Publish(new InjuryObserved(Origin(s), GameWatchReads.SafeName(p)
                            + (suppressed
                                ? (acknowledged
                                    ? " took another injury; play continues under the configured injury acknowledgement."
                                    : " took another injury; play continues (within the "
                                        + (s.InjuryStopCooldownMs / 1000) + " s injury-stop cooldown).")
                                : (demoted
                                    ? " took a new wound under the severity floor; play continues and the medical planner reviews it."
                                    : newWound
                                        ? " took a sub-threshold combat injury; play continues."
                                        : " has a known wound worsening (blood loss/severity creep); play continues.")), injury));
                        if (demoted) InvalidateMedicalFacts(s, p.thingIDNumber,
                            GameWatchReads.SafeName(p) + " took a new wound under the severity floor");
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
            var colonistsNear = pawns.Where(GameWatchReads.SafeIsColonist).ToList();
            foreach (var p in pawns)
            {
                string why;
                if (p == null || GameWatchReads.SafeIsColonist(p)) continue;
                if (GameWatchReads.SafeDead(p)) continue;
                var hostile = GameWatchReads.IsHostile(p, out why);
                if (GameWatchReads.SafeDowned(p))
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
            var drafted = pawns.Where(p => p != null && GameWatchReads.SafeIsColonist(p)
                && !GameWatchReads.SafeDowned(p) && !GameWatchReads.SafeDead(p)
                && SafeDrafted(p)).ToList();
            // Across a resume, only fire when there is something left to do.
            if (first && downed.Count == 0 && drafted.Count == 0) return;
            s.HostilesCleared = true;
            var parts = new List<string>();
            if (downed.Count > 0)
                parts.Add(downed.Count + " downed (" + string.Join("; ", downed
                    .Select(p => GameWatchReads.SafeName(p) + " at "
                        + p.Position.x + "," + p.Position.z).ToArray())
                    + ")");
            if (drafted.Count > 0)
                parts.Add(drafted.Count + " colonist" + (drafted.Count == 1 ? "" : "s")
                    + " still drafted (" + string.Join(", ", drafted
                        .Select(GameWatchReads.SafeName).ToArray()) + ")");
            Publish(new HostilesCleared(Origin(s), "No conscious hostiles remain: "
                + (parts.Count > 0 ? string.Join("; ", parts.ToArray())
                    : "nothing downed and nobody drafted") + "."));
        }

        private static bool SafeDrafted(Pawn pawn) { try { return pawn.Drafted; } catch { return false; } }

        /// Milliseconds of injury-stop cooldown still owed to this pawn, or 0.
        private static long InjuryCooldownRemainingMs(int pawnId, long cooldownMs)
        {
            InjuryStop? stop;
            if (cooldownMs <= 0 || !InjuryStops.TryGetValue(pawnId, out stop)) return 0;
            var remaining = stop.AtMs + cooldownMs - NowMs();
            return remaining > 0 ? remaining : 0;
        }

        /// Publishes the medical facts as stale so the medical planner
        /// reviews under the running clock, at most once per pawn per
        /// MedicalWakeIntervalTicks unless the caller forces it. This is the
        /// wake the demoted health tiers are worth: every reader of the pawn
        /// and emergency families replans, and no run is spent.
        private static void InvalidateMedicalFacts(State s, int pawnId, string reason, bool force = false)
        {
            var tick = Find.TickManager != null ? Find.TickManager.TicksGame : s.LastTick;
            int last;
            if (!force && s.MedicalWakes.TryGetValue(pawnId, out last) && tick - last < MedicalWakeIntervalTicks) return;
            s.MedicalWakes[pawnId] = tick;
            Invalidate(s, reason, new[] { Clock.FactFamily.Pawns, Clock.FactFamily.Emergency });
        }

        private static void RecordInjuryStop(Pawn pawn)
        {
            var now = NowMs();
            foreach (var id in InjuryStops.Where(kv => now - kv.Value.AtMs > 3600000L)
                         .Select(kv => kv.Key).ToList())
                InjuryStops.Remove(id);
            InjuryStops[pawn.thingIDNumber] = new InjuryStop {
                AtMs = now, Tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0,
                Name = GameWatchReads.SafeName(pawn) };
        }

        /// Observations only: the controller decides how to respond.
        private static string InjuryDetail(State s, Pawn p, InjurySnapshot before, InjurySnapshot after)
        {
            var name = GameWatchReads.SafeName(p);
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

        private static HoldCause PawnHit(Clock.HoldReason reason, Pawn pawn, string why, int? occurrenceTick = null) =>
            // The tick the hazard arose where the game records one, for
            // the stop's detect_ticks; absent when it does not.
            new HoldCause(reason, GameWatchReads.SafeName(pawn) + " (" + why + ")", new PawnEvidence(PawnOf(pawn, why)),
                occurrenceTick.HasValue && occurrenceTick.Value >= 0 ? occurrenceTick.Value : null);
        private static HungerCategory? SafeHunger(Pawn pawn) { try { return pawn.needs?.food?.CurCategory; } catch { return null; } }
        private static int SafeArrivalTick(Letter letter) { try { return letter.arrivalTick; } catch { return -1; } }
        private static int? JobStartTick(Pawn pawn) { try { var job = pawn.CurJob; return job != null && job.startTick >= 0 ? job.startTick : (int?)null; } catch { return null; } }
        /// One new alert; the key is type|priority|normalized-label.
        private static AlertRaised Alert(ColonyEventOrigin origin, KeyValuePair<string, string> a)
        {
            var parts = a.Key.Split('|');
            return new AlertRaised(origin, a.Value, a.Key, a.Value, parts.Length > 1 ? parts[1] : null);
        }

        /// A pawn an event names, at its current cell.
        private static EventPawn PawnOf(Pawn pawn, string? reason = null) =>
            new EventPawn(pawn.GetUniqueLoadID(), GameWatchReads.SafeName(pawn) ?? "", pawn.Position.x, pawn.Position.z, reason);

        private static List<string> ForcePausingWindows()
        {
            var windows = Find.WindowStack == null ? null : Find.WindowStack.Windows;
            return windows == null ? new List<string>() : windows
                .Where(w => w != null && w.forcePause).Select(w => w.GetType().FullName!).ToList();
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
            var ours = GameWatchReads.PreyBelongsToPlayer(p, out prey);
            // Share the status reader's native ownership test. Re-evaluate each
            // probe, so a predator changing targets never gets a lasting exemption.
            return ours || prey == null;
        }
        private static int Distance(Pawn a, Pawn b) { return Math.Max(Math.Abs(a.Position.x - b.Position.x), Math.Abs(a.Position.z - b.Position.z)); }
        /// Enters a hold: pauses (when asked) and publishes one HoldEntered. The
        /// native clock then stays held until the brain sends Run.
        private static void Stop(State? s, Clock.HoldReason reason, string detail, bool pause) =>
            Stop(s, new HoldCause(reason, detail), pause);
        private static void Stop(State? s, HoldCause cause, bool pause)
        {
            if (s == null || !ReferenceEquals(s, _state) || !s.Active) return;
            // The hold is entered when the stop is first raised (Status reports
            // a pending stop as held); a failed pause's per-frame retry is the
            // same hold and does not bump the serial again.
            if (s.Pending == null) HoldSerial.Entered();
            // The tick the stop was first raised at; a failed pause retries
            // per frame and keeps it, so the published stop tick can be later.
            if (s.Pending == null && Find.TickManager != null) s.DetectedTick = Find.TickManager.TicksGame;
            if (pause && Find.TickManager != null && Find.TickManager.CurTimeSpeed != TimeSpeed.Paused) Find.TickManager.Pause();
            s.PausedAtStop = Find.TickManager != null && Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
            s.PauseVerified = !pause || s.PausedAtStop;
            if (s.Typed != null) s.Typed.StopPauseVerified = ReferenceEquals(Current.Game, s.Session)
                && ReferenceEquals(Find.CurrentMap, s.Map) && s.PausedAtStop;
            if (pause && !s.PausedAtStop)
            {
                // Stay armed and retry on the next frame. Going inactive here
                // would turn a failed pause into unguarded play.
                s.Pending = cause;
                if (!s.PauseFailureReported)
                {
                    s.PauseFailureReported = true;
                    Publish(new PauseFailed(Origin(s), "Pause did not take while handling " + cause.Reason + ": " + cause.Detail, cause.Reason));
                }
                return;
            }
            s.Pending = null;
            RestoreBoost(s);
            var stopAt = NowMs();
            s.StopAtMs = stopAt;
            s.Active = false; s.StopReason = cause.Reason; s.StopDetail = cause.Detail; ClockPauseAccounting.Stopped();
            Publish(new HoldEntered(Origin(s), cause.Detail, cause.Reason, EvidenceOf(s, cause), s.DetectedTick, cause.OccurrenceTick, stopAt, RunSegmentOf(s, stopAt)));
            LogTiming(s, cause.Reason.ToString());
        }
        /// What a hold carries: the hazard's own evidence, else the pause
        /// itself while its game is loaded (the letter vanilla paused for, the
        /// force-pausing dialog), else nothing beyond its reason.
        /// The run this hold ends, from its Run to the stop: ticks, wall ms, the frames that carried ticks and the
        /// over-budget ones among them (the session account's growth since the Run), and the ceiling's rate now.
        private static RunSegment RunSegmentOf(State s, long stopAt)
        {
            var (frames, overBudget, _) = FrameAccount();
            return new RunSegment(Math.Max(0, s.DetectedTick - s.StartTick), Math.Max(0, stopAt - s.ResumeMs),
                frames >= s.FramesAtResume ? frames - s.FramesAtResume : frames,
                overBudget >= s.OverBudgetAtResume ? overBudget - s.OverBudgetAtResume : overBudget, s.Ceiling.TicksPerSecond);
        }
        private static ColonyHoldEvidence EvidenceOf(State s, HoldCause cause)
        {
            if (cause.Evidence != null) return cause.Evidence;
            if (!ReferenceEquals(Current.Game, s.Session) || !ReferenceEquals(Find.CurrentMap, s.Map)) return new UnobservedEvidence();
            EventLetter? letter = null;
            if (cause.PauseLetterId is { } id)
            {
                var stacked = GameWatchReads.Letters().FirstOrDefault(value => value.GetUniqueLoadID() == id);
                letter = new EventLetter(id, stacked?.Label.ToString(), stacked?.def?.defName);
            }
            var dialog = cause.Reason == Clock.HoldReason.DialogPause ? ChoiceDialogTools.Pending() : null;
            return new PauseEvidence(Find.TickManager != null && Find.TickManager.Paused, TypedOf(s).StopPauseVerified, letter,
                dialog != null ? ChoiceDialogTools.Title(dialog) : null);
        }
        private static void RestoreBoost(State s)
        {
            if (s.NeverForceNormalOwned)
            {
                DebugViewSettings.neverForceNormalSpeed = s.PriorNeverForceNormal;
                s.NeverForceNormalOwned = false;
            }
            s.Boost.Release(value => BoostField.SetValue(null, value));
        }
        private static HashSet<string> Csv(string value) { return new HashSet<string>((value ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase); }
        private static HashSet<int> PawnIds(string value) { var r = new HashSet<int>(); foreach (var x in Csv(value)) { int n; var digits = new string(x.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()); if (int.TryParse(digits, out n)) r.Add(n); } return r; }
        private static int Clamp(int x, int lo, int hi) { return Math.Max(lo, Math.Min(hi, x)); }
        private static float ClampFloat(float x, float lo, float hi) { return Math.Max(lo, Math.Min(hi, x)); }
        private static long NowMs() { return (DateTime.UtcNow.Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond; }

        // Where an epoch's wall time went, per epoch, for the acceptance
        // speed work: game ticks and frames, the tick-boundary hook,
        // the periodic hazard probe and, apart
        // from it, the fact-change digests. Logged once per stop, only
        // under the acceptance test-acceleration launch flag.
        private sealed class EpochTiming
        {
            public long HookTicks, HookElapsed, ProbeElapsed, DigestElapsed;
            public int Digests;
            public int Frames, FrameTicks, MaxFrameTicks;
            public long StartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        private static double Ms(long stopwatchTicks) { return stopwatchTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        private static void LogTiming(State s, string reason)
        {
            if (!TestAccelerationLaunch) return;
            var t = s.Timing;
            var wall = Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t.StartedAt);
            var ticks = s.LastTick - s.StartTick;
            ModLog.Info("clock", string.Format(CultureInfo.InvariantCulture,
                "RimGovernor clock run segment timing{0}: hold={1} speed={2} boost={3} ticks={4} wallMs={5:F0} tps={6:F0} frames={7} maxTicksPerFrame={8} hookTicks={9} hookMs={10:F1} probes={11} probeMs={12:F1} maxProbeTickGap={13} digests={14} digestMs={15:F1}",
                "", reason, s.Speed, s.Boost.Owned, ticks, wall, wall > 0 ? ticks * 1000.0 / wall : 0,
                t.Frames, t.MaxFrameTicks, t.HookTicks, Ms(t.HookElapsed), s.ProbeCount, Ms(t.ProbeElapsed), s.MaxProbeTickGap,
                t.Digests, Ms(t.DigestElapsed)));
            var prof = TickProfile.Take();
            if (prof.Length > 0) ModLog.Info("clock", "RimGovernor clock run segment tick profile:" + prof);
        }

        /// Why a hold is entered: its reason and detail, the hazard's evidence
        /// and the tick it arose when the probe has them, and the letter a
        /// letter pause names. A hold without evidence carries the pause itself.
        private sealed record HoldCause(Clock.HoldReason Reason, string Detail, ColonyHoldEvidence? Evidence = null,
            long? OccurrenceTick = null, string? PauseLetterId = null);

        private sealed class InjurySnapshot
        {
            public int Count; public float Severity; public float BleedRate; public float BloodLoss; public float Health;
            // Age in ticks of the youngest injury, for the stop's occurrence tick.
            public int NewestWoundAgeTicks = int.MaxValue;
            // The severity floor the stop tier reads: RimWorld's own
            // estimate of the ticks left before this pawn dies of blood loss
            // (int.MaxValue when they are not bleeding out) and whether any
            // hediff stage is life-threatening on its own (an infection, a
            // failing organ). Both are the game's judgement, not ours.
            public int BleedOutTicks = int.MaxValue;
            public bool LifeThreatening;
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
                        try { if (h.CurStage != null && h.CurStage.lifeThreatening) result.LifeThreatening = true; } catch { }
                    }
                    try { result.BleedOutTicks = HealthUtility.TicksUntilDeathDueToBloodLoss(pawn); } catch { }
                }
                catch { }
                return result;
            }
        }

        private sealed class State
        {
            public TypedEpoch? Typed;
            /// The brain-authored policy this epoch runs under; every hazard threshold reads from it.
            public RimGovernor.Contracts.Wire.Clock.WatchPolicy? Policy => Typed?.Policy;
            public State(object session, Map map) { Session = session; Map = map; }
            public bool Active; public readonly object Session; public readonly Map Map;
            // The wire speed the Run asked for (ClockRunSpeed.cs): Ultrafast and Hyperfast share
            // TimeSpeed.Ultrafast, so the game speed the segment expects is derived from it.
            public Clock.Speed Speed;
            public TimeSpeed RequestedSpeed => ClockRunSpeed.Native(Speed);
            // Hyperfast: the frame's many boosted ticks each run the full frame check (TickBody).
            public bool Boosted => Speed == Clock.Speed.Hyperfast;
            public string? Mode; public float HostileWithin; public float HealthDropFraction; public float MinHealthFraction;
            public long LastProbeMs; public int LastTick; public bool PausedAtStop;
            public int StartTick;
            // The game tick this segment holds at for Review (the Run's HoldAtTick); every Run moves it.
            public int HoldAtTick;
            // The run segment's start (wall ms and the session frame account at the Run), measured against at the hold.
            public long ResumeMs; public ulong FramesAtResume, OverBudgetAtResume;
            // What the game's TickRateMultiplier asked for before the ceiling, as of the last frame.
            public volatile float UnclampedMultiplier;
            public readonly BoostOwnership Boost = new BoostOwnership(); public bool PriorNeverForceNormal; public bool NeverForceNormalOwned;
            // The one ceiling (ClockCeiling.cs): frame budget plus brain heartbeat.
            public ClockCeiling Ceiling = SharedCeiling();
            public int LastProbeTick; public int MaxProbeTickGap; public int ProbeCount;
            // The digest cadence and the probe requests direct game
            // hooks raised (hazard class -> tick), served at the next probe.
            public int LastDigestTick; public bool ProbeRequested;
            public readonly List<KeyValuePair<string, int>> ProbeRequests = new List<KeyValuePair<string, int>>();
            public readonly EpochTiming Timing = new EpochTiming();
            public long? StopAtMs;
            public int DetectedTick;
            public bool? PauseVerified; public bool PauseFailureReported;
            public Clock.HoldReason? StopReason; public string? StopDetail;
            // A hold whose pause has not taken yet; retried each frame.
            public HoldCause? Pending;
            // null until the epoch's first probe baselined them; see PublishFactChanges.
            public string? ResearchDigest; public string? WorldDigest; public string? ConditionDigest;
            public Dictionary<string, ZoneDigest>? ZoneDigests;
            public Dictionary<string, int>? StockpileFill; public int LastFillTick;
            public HashSet<string>? RipeZones;
            // Wall-clock ms at which a windowless force pause began, 0 when none.
            // Whether that force pause is a long event rather than a windowless transient.
            public long ForcePauseSinceMs; public bool ForcePauseLongEvent;
            public readonly HashSet<string> Letters = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Messages = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<int, InjurySnapshot> Injuries = new Dictionary<int, InjurySnapshot>();
            // Grows only: an alert that clears and returns is not news again.
            public readonly HashSet<string> AlertKeys = new HashSet<string>(StringComparer.Ordinal);
            // null until the first probe of this epoch has counted.
            public int? ConsciousHostiles; public bool HostilesCleared;
            /// Game tick of each pawn's last demoted-injury invalidation.
            public readonly Dictionary<int, int> MedicalWakes = new Dictionary<int, int>();
            public HashSet<int> IgnoredHostiles = new HashSet<int>(); public HashSet<int> IgnoredDowned = new HashSet<int>(); public HashSet<int> SurgicalRecovery = new HashSet<int>(); public HashSet<int> MedicalRest = new HashSet<int>();
            public HashSet<int> IgnoredInjured = new HashSet<int>(); public int InjuryStopCooldownMs;
            public HashSet<int> IgnoredHungry = new HashSet<int>(); public HungerCategory HungerHold = HungerCategory.UrgentlyHungry;
        }

        private sealed class InjuryStop
        {
            public long AtMs; public int Tick; public string? Name;
        }
    }
}
