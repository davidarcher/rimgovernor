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
    public static class Find { public static Map CurrentMap; public static TickManager TickManager; }
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

    internal sealed class NativeOperationState
    {
        internal NativeAttemptLedger Ledger;
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
        private static long NowMs() => WallTime;
        private static void EnsurePatched() { HarmonyLib.Harmony.Installed = true; }
        private static List<string> ForcePausingWindows() => new List<string>();
        private static void EnsureJournal() { if (Journal == null) { Journal = new ClockEventJournal(); _cursor = Journal.Newest; } }
        private static object Start(string owner, Verse.TimeSpeed speed, int leaseMs, string mode, float healthDrop, float minHealth, float hostileWithin,
            string ignoredHostiles, string ignoredDowned, string ignoredInjured, int cooldown, int maxTicks, string surgical, bool acceleration, string rest)
        {
            var s = new State
            {
                Active = true, Epoch = ++_epoch, Session = Verse.Current.Game, Map = Verse.Find.CurrentMap, RequestedSpeed = speed,
                StartTick = Verse.Find.TickManager.TicksGame, TickDeadline = Verse.Find.TickManager.TicksGame + maxTicks, LastTick = Verse.Find.TickManager.TicksGame
            };
            AttachTypedEpoch(s); _state = s;
            if (InitialStop != null) Stop(s, InitialStop, "Initial safety probe stopped", true, null);
            else { Verse.Find.TickManager.CurTimeSpeed = speed; Add("started", "Started", s, null); }
            return null;
        }
        private static object Speed(string owner, long epoch, Verse.TimeSpeed speed)
        {
            if (!typedSpeedCall) throw new InvalidOperationException("Canonical epoch requires typed speed capability");
            _state.RequestedSpeed = speed; Verse.Find.TickManager.CurTimeSpeed = speed;
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
            s.Active = false; s.StopReason = kind; s.StopDetail = detail; s.StopAtMs = NowMs(); s.PendingKind = null;
            Add(kind, detail, s, payload);
        }
        private static void Add(string kind, string detail, State s, Dictionary<string, object> payload)
        { EnsureJournal(); var row = new Dictionary<string, object> { ["cursor"] = checked(_cursor + 1) }; AttachTypedEvent(row, kind, detail, s, payload); Journal.Append(row); _cursor = Journal.Newest; }
        private sealed class State
        {
            internal TypedEpoch Typed; internal bool Active; internal object Session; internal Verse.Map Map; internal long Epoch;
            internal Verse.TimeSpeed RequestedSpeed; internal long LeaseExpiresMs; internal int StartTick, LastTick, MaxProbeTickGap, ProbeCount;
            internal long? TickDeadline, StopAtMs; internal string PendingKind, PendingDetail, StopReason, StopDetail, ForcePauseKind;
            internal bool? PauseVerified; internal long ForcePauseSinceMs;
            internal List<Dictionary<string, object>> BaselineAlerts = new List<Dictionary<string, object>>(), SuppressedInjuries = new List<Dictionary<string, object>>();
        }
        internal static void FixtureReset()
        { _state = null; Journal = null; _epoch = _cursor = 0; RefusePause = false; InitialStop = null; _patchError = null; HarmonyLib.Harmony.Installed = false; }
        internal static void FixtureExpire() => _state.LeaseExpiresMs = LeaseNow(_state);
        internal static bool IsActiveForFixture() => _state != null && _state.Active;
        internal static void FixtureEvent(string kind, Dictionary<string, object> row) => Add(kind, "Observed", _state, row);
        internal static void FixtureLegacyEvent() { Journal.Append(new Dictionary<string, object> { ["cursor"] = _cursor + 1 }); _cursor = Journal.Newest; }
        internal static void FixtureLegacyEpoch() { _state.Typed = null; }
        internal static void OnTick() => OnUpdate();
        internal static void OnUpdate()
        {
            var s = _state; if (s == null || !s.Active) return;
            if (!ReferenceEquals(Verse.Current.Game, s.Session) || !ReferenceEquals(Verse.Find.CurrentMap, s.Map)) { Stop(s, "session_changed", "Context changed", false, null); return; }
            s.LastTick = Verse.Find.TickManager.TicksGame; CaptureTypedContext(s);
            if (s.PendingKind != null) { Stop(s, s.PendingKind, s.PendingDetail, true, null); return; }
            if (StopInvalidTypedAuthority(s)) return;
            if (s.LastTick >= s.TickDeadline) Stop(s, "tick_budget", "Budget", true, null);
        }
    }
}
