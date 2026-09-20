using System;
using System.Collections.Generic;
using System.IO;
#pragma warning disable CS0649 // Controlled native fixture fields intentionally default to zero/null.

// Unified fake Verse/RimWorld/HomeBridge doubles for the in-process (no real Assembly-CSharp.dll)
// probes: native-authority, native-authority-control, native-authority-status, native-clock,
// native-proto-boundary. Reconciled from what were five near-identical, independently-written
// stub files (one per probe) before the NativeContractProbes.csproj consolidation; every field
// and member below is required by at least one of those probes' Program.cs, and none of them
// alter a Check()/Assert() outcome relative to the pre-consolidation per-probe stub it replaces.
namespace Verse
{
    public class Game
    {
        public HomeBridge.BridgeTools.ColonyIdentity Identity = new HomeBridge.BridgeTools.ColonyIdentity();
        public T GetComponent<T>() where T : class => Identity as T;
    }
    public class Map { public int uniqueID; public MapPawns mapPawns = new MapPawns(); }
    public class MapPawns { public List<Pawn> AllPawns = new List<Pawn>(); }
    public class Pawn { public int thingIDNumber; public string GetUniqueLoadID() => "Pawn_" + thingIDNumber; }
    public class Letter { public string Label, Id; public LetterDef def; public string GetUniqueLoadID() => Id; }
    public class LetterDef { public string defName; }
    public static class Current { public static Game Game; }
    public static class Find
    {
        public static Map CurrentMap; public static TickManager TickManager;
        // Real Verse exposes the game's loaded maps; the fixtures load one map, the current one (#35 M2).
        public static List<Map> Maps => CurrentMap == null ? null : new List<Map> { CurrentMap };
    }
    public enum TimeSpeed { Paused, Normal, Fast, Superfast, Ultrafast }
    public class TickManager
    {
        public int TicksGame;
        public TimeSpeed CurTimeSpeed;
        public bool Forced;
        public bool Paused => Forced || CurTimeSpeed == TimeSpeed.Paused;
        public void TickManagerUpdate() { }
        public void DoSingleTick() { }
    }
    public static class LongEventHandler { public static bool AnyEventNowOrWaiting; }
    public static class Log { public static void Warning(string text) { } public static void Message(string text) { } }
    public class Dialog_NodeTree { public int ID; }
    public static class GenFilePaths
    {
        public static string SaveDataFolderPath = Path.Combine(Environment.CurrentDirectory, ".rimgovernor", "clock-tests-" + Guid.NewGuid().ToString("N"));
    }
}
namespace RimWorld { }
namespace HomeBridge.BridgeTools
{
    public class ColonyIdentity { public string ColonyId = "colony"; public string LoadToken = "load"; }

    internal static class HomePlayUntilEventTools
    {
        internal static bool ShortGuardRunning;
        internal static bool CoreWatchersAvailable = true;
        internal static List<Verse.Letter> LiveLetters = new List<Verse.Letter>();
        internal static IEnumerable<Verse.Letter> Letters() => LiveLetters;
    }
    internal static class LetterPauseHook { internal static void EnsurePatched() { } }
    // NativeClockRuntime attaches DialogPause evidence from this lookup; the
    // clock probe never stops for a dialog, so no window is ever pending.
    internal static class ChoiceDialogTools
    {
        internal static Verse.Dialog_NodeTree Pending() => null;
        internal static string Title(Verse.Dialog_NodeTree dialog) => null;
    }

    // native-clock's construction-record double: Observe answers with whatever
    // progress the fixture last set, so a watched attempt can be driven to
    // pending, completed or unknown without a live map.
    internal sealed class NativeConstructionRecord
    {
        internal RimGovernor.Protocol.Receipts.Progress Next = new RimGovernor.Protocol.Receipts.Progress
        { Pending = new RimGovernor.Protocol.Receipts.PendingEffect { Evidence = new RimGovernor.Protocol.Receipts.EffectEvidence
            { Construction = new RimGovernor.Protocol.Receipts.ConstructionEffect { DefName = "Wall", Stage = RimGovernor.Protocol.Receipts.ConstructionStage.Blueprint, Present = true, Started = true } } } };
        internal RimGovernor.Protocol.Receipts.Progress Observe(RimGovernor.Protocol.Common.AttemptKey attempt, RimGovernor.Protocol.Common.ObservationContext context)
        { var progress = Next.Clone(); progress.Attempt = attempt.Clone(); progress.Context = context.Clone(); return progress; }
    }
    // Haul double with the same Observe shape; the clock watch resolves a
    // watched key against both record tables (#108) and the probe only ever
    // arms construction keys, so no haul is ever recorded here.
    internal sealed class NativeHaulRecord
    {
        internal RimGovernor.Protocol.Receipts.Progress Observe(RimGovernor.Protocol.Common.AttemptKey attempt, RimGovernor.Protocol.Common.ObservationContext context)
            => throw new InvalidOperationException("The clock probe records no haul operations.");
    }
    internal sealed class NativeOperationState
    {
        internal NativeAttemptLedger Ledger;
        internal readonly Dictionary<RimGovernor.Protocol.Common.AttemptKey, NativeConstructionRecord> Construction = new Dictionary<RimGovernor.Protocol.Common.AttemptKey, NativeConstructionRecord>();
        internal readonly Dictionary<RimGovernor.Protocol.Common.AttemptKey, NativeHaulRecord> Hauls = new Dictionary<RimGovernor.Protocol.Common.AttemptKey, NativeHaulRecord>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Verse.Game, NativeOperationState> States = new System.Runtime.CompilerServices.ConditionalWeakTable<Verse.Game, NativeOperationState>();
        internal static NativeOperationState ForAdmission(RimGovernor.Protocol.Common.Identity identity) => States.GetValue(Verse.Current.Game, _ => new NativeOperationState { Ledger = new NativeAttemptLedger(identity) });
        internal static bool TryGet(RimGovernor.Protocol.Common.Identity identity, out NativeOperationState state) => States.TryGetValue(Verse.Current.Game, out state);
    }

    // native-clock's fixture-only supervisor double: completes the partial class whose typed-epoch
    // members (AttachTypedEpoch/AttachTypedEvent/CaptureTypedContext/StopInvalidTypedAuthority/
    // typedSpeedCall/LeaseNow) live in the real production NativeClock*.cs files this project compiles.
    internal static partial class Supervisor
    {
        private static readonly object Gate = new object();
        private static State _state;
        private static ClockEventJournal Journal;
        private static long _epoch, _cursor;
        private static string _patchError;
        internal static long WallTime = 1700000000000;
        internal static bool RefusePause;
        internal static string InitialStop;
        // The hazard hooks (#626) are Verse-bound; the fake reports them installed.
        internal static bool HazardHooksInstalled = true;
        // The production partial reads TickManager.UltraSpeedBoost by reflection
        // (#109); the fake TickManager has no boost, so test acceleration is
        // never available to the probe.
        private static readonly System.Reflection.FieldInfo BoostField = null;
        private static long NowMs() => WallTime;
        private static void EnsurePatched() { HarmonyLib.Harmony.Installed = true; }
        private static List<string> ForcePausingWindows() => new List<string>();
        // Same contract as the production partial: the journal is created on first
        // use and returned so callers can read it under the Gate.
        private static ClockEventJournal EnsureJournal() { if (Journal == null) { Journal = new ClockEventJournal(); _cursor = Journal.Newest; } return Journal; }
        private static State ActiveState => _state ?? throw new InvalidOperationException("No supervised clock epoch.");
        private static object Start(string owner, Verse.TimeSpeed speed, int leaseMs, string mode, float healthDrop, float minHealth, float hostileWithin,
            string ignoredHostiles, string ignoredDowned, string ignoredInjured, int cooldown, int maxTicks, string surgical, bool acceleration, string rest,
            int blindTickBudget = 0, int maxTicksPerSecond = 0)
        {
            var s = new State
            {
                Active = true, Epoch = ++_epoch, Session = Verse.Current.Game, Map = Verse.Find.CurrentMap, RequestedSpeed = speed,
                BlindTickBudget = blindTickBudget, MaxTicksPerSecond = maxTicksPerSecond,
                StartTick = Verse.Find.TickManager.TicksGame, TickDeadline = Verse.Find.TickManager.TicksGame + maxTicks, LastTick = Verse.Find.TickManager.TicksGame
            };
            EnsureJournal();
            AttachTypedEpoch(s); _state = s; ClockPauseAccounting.Started(s.Session);
            if (InitialStop != null) Stop(s, InitialStop, "Initial safety probe stopped", true, null);
            else { Verse.Find.TickManager.CurTimeSpeed = speed; Add("started", "Started", s, null); }
            return null;
        }
        private static object Speed(string owner, long epoch, Verse.TimeSpeed speed, int? maxTicksPerSecond = null)
        {
            if (!typedSpeedCall) throw new InvalidOperationException("Canonical epoch requires typed speed capability");
            _state.RequestedSpeed = speed; Verse.Find.TickManager.CurTimeSpeed = speed;
            if (maxTicksPerSecond.HasValue) _state.MaxTicksPerSecond = maxTicksPerSecond.Value;
            Add("speed_changed", "Speed changed", _state, new Dictionary<string, object> { ["speed"] = speed.ToString() });
            return null;
        }
        private static void Stop(State s, string kind, string detail, bool pause, Dictionary<string, object> payload)
        {
            if (!s.Active) return;
            s.Typed.PauseRequested = pause;
            if (pause && !RefusePause) Verse.Find.TickManager.CurTimeSpeed = Verse.TimeSpeed.Paused;
            s.PauseVerified = !pause || Verse.Find.TickManager.CurTimeSpeed == Verse.TimeSpeed.Paused;
            s.Typed.StopPauseVerified = ReferenceEquals(Verse.Current.Game, s.Session) && ReferenceEquals(Verse.Find.CurrentMap, s.Map) && Verse.Find.TickManager.CurTimeSpeed == Verse.TimeSpeed.Paused;
            if (s.PauseVerified == false) { s.PendingKind = kind; s.PendingDetail = detail; Add("pause_failed", detail, s, payload); return; }
            s.Active = false; s.StopReason = kind; s.StopDetail = detail; s.StopAtMs = NowMs(); s.PendingKind = null; ClockPauseAccounting.Stopped();
            Add(kind, detail, s, payload);
        }
        private static void Add(string kind, string detail, State s, Dictionary<string, object> payload)
        { EnsureJournal(); var row = new Dictionary<string, object> { ["cursor"] = checked(_cursor + 1) }; AttachTypedEvent(row, kind, detail, s, payload); Journal.Append(row); _cursor = Journal.Newest; SignalWaiters(_cursor); }
        private static void Publish(string kind, string detail, Dictionary<string, object> payload)
        {
            lock (Gate)
            {
                EnsureJournal(); var row = new Dictionary<string, object> { ["cursor"] = checked(_cursor + 1), ["epoch"] = 0L };
                if (!AttachOwnerlessEvent(row, kind, detail, payload)) return;
                Journal.Append(row); _cursor = Journal.Newest; SignalWaiters(_cursor);
            }
        }
        private sealed class State
        {
            internal TypedEpoch Typed; internal bool Active; internal object Session; internal Verse.Map Map; internal long Epoch;
            internal Verse.TimeSpeed RequestedSpeed; internal long LeaseExpiresMs; internal int StartTick, LastTick, MaxProbeTickGap, ProbeCount;
            internal long? TickDeadline, StopAtMs; internal string PendingKind, PendingDetail, StopReason, StopDetail, ForcePauseKind;
            internal bool? PauseVerified; internal long ForcePauseSinceMs; internal bool TestAcceleration;
            internal int BlindTickBudget, MaxTicksPerSecond, RegulatedTicksPerSecond;
            internal List<Dictionary<string, object>> BaselineAlerts = new List<Dictionary<string, object>>(), SuppressedInjuries = new List<Dictionary<string, object>>();
        }
        // The blind-tick regulator (#583) lives in the production
        // SupervisedPlayRegulator.cs partial, outside this project; the probe
        // only records what the typed runtime reports of it.
        private static void NoteControllerRead(State s) { }
        private static void AcknowledgeRows(State s, long afterCursor) { }
        internal static void FixtureReset()
        { _state = null; Journal = null; _epoch = _cursor = 0; RefusePause = false; InitialStop = null; _patchError = null; HarmonyLib.Harmony.Installed = false; }
        internal static void FixtureExpire() => _state.LeaseExpiresMs = LeaseNow(_state);
        internal static bool IsActiveForFixture() => _state != null && _state.Active;
        internal static void FixtureEvent(string kind, Dictionary<string, object> row) => Add(kind, "Observed", _state, row);
        internal static void FixtureLegacyEvent() { Journal.Append(new Dictionary<string, object> { ["cursor"] = _cursor + 1 }); _cursor = Journal.Newest; }
        internal static void FixtureLegacyEpoch() { _state.Typed = null; }
        internal static void OnTick() => OnUpdate();
        internal static void OnFrame() => OnUpdate();
        internal static void OnUpdate()
        {
            var s = _state; if (s == null || !s.Active) return;
            if (!ReferenceEquals(Verse.Current.Game, s.Session) || !ReferenceEquals(Verse.Find.CurrentMap, s.Map)) { Stop(s, "session_changed", "Context changed", false, null); return; }
            s.LastTick = Verse.Find.TickManager.TicksGame; CaptureTypedContext(s);
            if (s.PendingKind != null) { Stop(s, s.PendingKind, s.PendingDetail, true, null); return; }
            if (StopInvalidTypedAuthority(s)) return;
            if (CheckWatches(s)) return;
            if (s.LastTick >= s.TickDeadline) Stop(s, "tick_budget", "Budget", true, null);
        }
    }
}
