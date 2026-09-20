#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// The direct game hooks behind the hazard bounds (#626): a hook that
    /// sees a hazard arise requests a probe at the next tick boundary, and a
    /// hook that sees a fact the digests report change marks the digest
    /// dirty. Neither does any work itself; the supervisor's tick path
    /// serves both. Installation failures are recorded, not thrown: the
    /// polled probe still holds the interval bound without them, and
    /// Clock.Status reports each class as unhooked.
    internal static partial class Supervisor
    {
        private const string HazardHarmonyId = "homebridge.supervised-play.hazards";
        private static int _hazardHooked;
        private static string? _hazardHookError;

        /// Set by a digest hook; cleared when the digest pass runs.
        internal static volatile bool DigestDirty;

        // Spawn tick per pawn id for pawns spawned under the current epoch,
        // and the tick a colonist was downed or killed, for occurrence ticks.
        private static readonly Dictionary<int, int> _spawnTicks = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> _downedTicks = new Dictionary<int, int>();

        /// Whether the hooks are installed; the status marks every class
        /// unhooked otherwise.
        internal static bool HazardHooksInstalled => _hazardHooked == 1 && _hazardHookError == null;

        internal static void EnsureHazardHooks()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _hazardHooked, 1, 0) != 0) return;
            try
            {
                var harmony = new Harmony(HazardHarmonyId);
                Postfix(harmony, AccessTools.Method(typeof(Pawn), nameof(Pawn.SpawnSetup)), nameof(OnPawnSpawned));
                Postfix(harmony, AccessTools.Method(typeof(Pawn_HealthTracker), "MakeDowned"), nameof(OnPawnDowned));
                Postfix(harmony, AccessTools.Method(typeof(Pawn), nameof(Pawn.Kill)), nameof(OnPawnKilled));
                Postfix(harmony, AccessTools.Method(typeof(LetterStack), nameof(LetterStack.ReceiveLetter),
                    new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) }), nameof(OnLetterReceived));
                foreach (var target in DigestTargets())
                    Postfix(harmony, target, nameof(OnDigestDirty));
                _hazardHookError = null;
            }
            catch (Exception ex)
            {
                _hazardHookError = ex.GetType().Name + ": " + ex.Message;
                Log.Warning("RimGovernor hazard hooks not installed; polled bounds only: " + _hazardHookError);
            }
        }

        private static void Postfix(Harmony harmony, System.Reflection.MethodBase? target, string handler)
        {
            if (target == null) throw new MissingMethodException("hazard hook target for " + handler);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(Supervisor), handler));
        }

        private static IEnumerable<System.Reflection.MethodBase?> DigestTargets()
        {
            yield return AccessTools.Method(typeof(ZoneManager), nameof(ZoneManager.RegisterZone));
            yield return AccessTools.Method(typeof(ZoneManager), nameof(ZoneManager.DeregisterZone));
            yield return AccessTools.Method(typeof(Zone), nameof(Zone.AddCell));
            yield return AccessTools.Method(typeof(Zone), nameof(Zone.RemoveCell));
            yield return AccessTools.Method(typeof(ResearchManager), nameof(ResearchManager.FinishProject));
            yield return AccessTools.Method(typeof(ResearchManager), nameof(ResearchManager.SetCurrentProject));
            yield return AccessTools.Method(typeof(ResearchManager), nameof(ResearchManager.StopProject));
            yield return AccessTools.Method(typeof(GameConditionManager), nameof(GameConditionManager.RegisterCondition));
            yield return AccessTools.Method(typeof(GameConditionManager), nameof(GameConditionManager.OnConditionEnd));
            yield return AccessTools.Method(typeof(Faction), nameof(Faction.TryAffectGoodwillWith));
            yield return AccessTools.Method(typeof(Faction), nameof(Faction.SetRelationDirect));
        }

        /// Forget the per-epoch occurrence records at a new epoch's start.
        private static void ResetHazardHooks()
        {
            lock (_spawnTicks) { _spawnTicks.Clear(); _downedTicks.Clear(); }
        }

        /// A hook asking the tick path for a probe, keyed by the class it
        /// serves so the class's hook-to-probe gap is recorded.
        private static void RequestProbe(string hazardClass)
        {
            var s = _state;
            if (s == null || !s.Active) return;
            var tm = Find.TickManager;
            s.ProbeRequests.Add(new KeyValuePair<string, int>(hazardClass, tm != null ? tm.TicksGame : s.LastTick));
            s.ProbeRequested = true;
        }

        private static void OnPawnSpawned(Pawn __instance)
        {
            try
            {
                var s = _state; var tm = Find.TickManager;
                if (s == null || !s.Active || tm == null || __instance == null) return;
                lock (_spawnTicks) _spawnTicks[__instance.thingIDNumber] = tm.TicksGame;
                if (__instance.HostileTo(Faction.OfPlayer)) RequestProbe("hostile");
            }
            catch { }
        }

        // Pawn_HealthTracker.pawn is private; Harmony injects it by name.
        private static void OnPawnDowned(Pawn ___pawn)
        {
            try
            {
                var pawn = ___pawn; var tm = Find.TickManager;
                if (pawn == null || tm == null || !pawn.IsColonist) return;
                lock (_spawnTicks) _downedTicks[pawn.thingIDNumber] = tm.TicksGame;
                RequestProbe("colonist_downed");
            }
            catch { }
        }

        private static void OnPawnKilled(Pawn __instance)
        {
            try
            {
                var tm = Find.TickManager;
                if (__instance == null || tm == null || !__instance.IsColonist) return;
                lock (_spawnTicks) _downedTicks[__instance.thingIDNumber] = tm.TicksGame;
                RequestProbe("colonist_downed");
            }
            catch { }
        }

        private static void OnLetterReceived() { try { RequestProbe("notification_batch"); } catch { } }

        private static void OnDigestDirty() { DigestDirty = true; }

        /// The pawn's spawn tick when it spawned under this epoch, else null.
        private static int? SpawnedTick(Pawn pawn)
        {
            lock (_spawnTicks) return _spawnTicks.TryGetValue(pawn.thingIDNumber, out var tick) ? tick : (int?)null;
        }

        /// The tick the downed/killed hook recorded for the colonist, else a
        /// corpse's time of death, else null.
        private static int? DownedTick(Pawn pawn)
        {
            lock (_spawnTicks) if (_downedTicks.TryGetValue(pawn.thingIDNumber, out var tick)) return tick;
            try { var corpse = pawn.Corpse; if (corpse != null) return corpse.timeOfDeath; } catch { }
            return null;
        }
    }
}
