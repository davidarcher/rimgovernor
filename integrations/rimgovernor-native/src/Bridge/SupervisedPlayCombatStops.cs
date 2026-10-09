#nullable enable
using RimGovernor.Host.Sdk;

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Clock = RimGovernor.Protocol.Clock;
using Mirror = RimGovernor.Protocol.Mirror;

namespace HomeBridge.BridgeTools
{
    /// Event-triggered combat stops. A combat epoch carries the armed
    /// list in WatchPolicy.combat_stop_events. A game hook that sees an armed
    /// event records it; the tick-boundary path (TickBody) stops the epoch at
    /// the boundary of the tick it happened on, so the controller decides on
    /// the exact tick instead of a poll's round trips later. Entering weapon
    /// range and melee contact have no single game call to hook: a per-tick
    /// scan, run only while one of them is armed, sees them on their tick.
    /// Aim warmup, shots and ordinary damage are never stops. The backstop
    /// when nothing fires is the combat window's own tick budget
    /// (combatBackstopTicks in go/cmd/rimgovernor/serve_clock.go).
    internal static partial class Supervisor
    {
        /// The thresholds below are Go-authored WatchPolicy fields (serious_*,
        /// explosive_near_margin_cells, melee_reach_cells); native holds no
        /// literal for them. They read from the last epoch's policy, so a
        /// colonist's damage is classified serious only once Go has started
        /// an epoch, and the combat log records Unspecified before that.
        private static Clock.WatchPolicy? EpochPolicy => _state?.Policy;

        private const string CombatHarmonyId = "homebridge.supervised-play.combat";
        private static int _combatHooked;
        /// Hooks that failed to install, by name; each missing one still
        /// leaves the backstop and the polled probe in force.
        internal static readonly List<string> CombatHookErrors = new List<string>();

        private sealed class CombatStop
        {
            public Clock.CombatEvent Event; public string? ThingId; public int? PawnId; public string Reason = ""; public int Tick;
        }
        // The first armed event seen since the last boundary; the tick path
        // takes it. Hooks run on the main thread between tick boundaries.
        private static CombatStop? _combatPending;
        // Per-combat scan memory: hostiles that have been in range, melee
        // pairs that have fought. It spans the combat's consecutive combat
        // epochs (a melee raider stepping in and out of reach is not news
        // at every window) and is seeded at the combat's first scan, so a
        // fight already in progress when it is armed is not news either.
        private static readonly HashSet<int> _inRange = new HashSet<int>(), _inOurRange = new HashSet<int>();
        private static readonly HashSet<long> _meleePairs = new HashSet<long>();
        private static bool _combatScanBaselined;

        /// Whether a combat clock epoch is running (the combat mirror is
        /// active through it).
        internal static bool CombatEpochRunning
        {
            get { var s = _state; return s != null && s.Active && s.Typed != null && s.Typed.Policy.Mode == Clock.WatchMode.Combat; }
        }

        internal static void EnsureCombatHooks()
        {
            CombatMirror.EnsureHooks();
            if (System.Threading.Interlocked.CompareExchange(ref _combatHooked, 1, 0) != 0) return;
            var harmony = new Harmony(CombatHarmonyId);
            void Patch(string name, System.Reflection.MethodBase? target, string? prefix, string? postfix)
            {
                try
                {
                    if (target == null) throw new MissingMethodException(name);
                    harmony.Patch(target,
                        prefix: prefix == null ? null : new HarmonyMethod(typeof(Supervisor), prefix),
                        postfix: postfix == null ? null : new HarmonyMethod(typeof(Supervisor), postfix));
                }
                catch (Exception ex)
                {
                    CombatHookErrors.Add(name + ": " + ex.GetType().Name + ": " + ex.Message);
                    ModLog.Warn("startup", "combat stop hook " + name + " not installed: " + ex.Message);
                }
            }
            Patch("PostApplyDamage", AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.PostApplyDamage)), nameof(OnDamagePrefix), nameof(OnDamagePostfix));
            Patch("CompShield.Break", AccessTools.Method(typeof(CompShield), "Break"), null, nameof(OnShieldBroken));
            Patch("Projectile.Launch", AccessTools.Method(typeof(Projectile), nameof(Projectile.Launch),
                new[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) }),
                null, nameof(OnProjectileLaunched));
            Patch("Lord.GotoToil", AccessTools.Method(typeof(Lord), nameof(Lord.GotoToil)), nameof(OnToilPrefix), nameof(OnToilPostfix));
            Patch("Thing.Destroy", AccessTools.Method(typeof(Thing), nameof(Thing.Destroy)), nameof(OnThingDestroying), null);
            Patch("TryStartMentalState", AccessTools.Method(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState)), null, nameof(OnMentalState));
        }

        private static object? _combatSession;

        /// At an epoch's start: drop any unserved event; forget the combat's
        /// scan memory unless this epoch continues a combat in the same game.
        private static void ResetCombatStops(State s)
        {
            _combatPending = null;
            if (s.Typed?.Policy.Mode == Clock.WatchMode.Combat && ReferenceEquals(_combatSession, s.Session)) return;
            _inRange.Clear(); _inOurRange.Clear(); _meleePairs.Clear(); _combatScanBaselined = false;
            _combatSession = s.Typed?.Policy.Mode == Clock.WatchMode.Combat ? s.Session : null;
        }

        /// Whether the running epoch is a combat epoch armed for this event.
        private static bool CombatArmed(Clock.CombatEvent e)
        {
            var s = _state;
            return s != null && s.Active && s.Typed != null && s.Typed.Policy.Mode == Clock.WatchMode.Combat
                && s.Typed.Policy.CombatStopEvents.Contains(e);
        }

        /// A hook saw an armed event: record it for the tick boundary. The
        /// first one since the boundary wins; the rest are the same stop.
        private static void NoteCombatEvent(Clock.CombatEvent e, Thing? thing, string reason)
        {
            if (_combatPending != null || !CombatArmed(e)) return;
            var tm = Find.TickManager;
            if (tm == null) return;
            _combatPending = new CombatStop { Event = e, Reason = reason, Tick = tm.TicksGame,
                ThingId = thing != null ? SafeLoadId(thing) : null, PawnId = (thing as Pawn)?.thingIDNumber };
        }

        /// The tick boundary's combat stop: runs the range/melee scan when
        /// armed, then stops on a recorded event. -> true when play stopped.
        private static bool StopOnCombatEvent(State s)
        {
            if (s.Typed == null || s.Typed.Policy.Mode != Clock.WatchMode.Combat || s.Typed.Policy.CombatStopEvents.Count == 0) return false;
            if (_combatPending == null) ScanCombatContact(s);
            var hit = _combatPending;
            if (hit == null) return false;
            _combatPending = null;
            s.Typed.CombatEvent = hit.Event;
            var payload = new Dictionary<string, object?> { { "combatEvent", (int)hit.Event }, { "reason", hit.Reason },
                { "thingId", hit.ThingId }, { "occurrenceTick", hit.Tick } };
            Stop(s, "combat_event", EventName(hit.Event) + ": " + hit.Reason, true, payload);
            return true;
        }

        private static string EventName(Clock.CombatEvent e) => e.ToString();

        private static void ScanCombatContact(State s)
        {
            var range = s.Typed!.Policy.CombatStopEvents.Contains(Clock.CombatEvent.EnteredRange);
            var melee = s.Typed.Policy.CombatStopEvents.Contains(Clock.CombatEvent.MeleeContact);
            if (!range && !melee) return;
            var map = s.Map;
            var pawns = map.mapPawns.AllPawnsSpawned;
            var colonists = new List<Pawn>(); var hostiles = new List<Pawn>();
            foreach (var p in pawns)
            {
                if (p.Dead || p.Downed) continue;
                if (p.IsColonist) colonists.Add(p);
                else if (p.HostileTo(Faction.OfPlayer)) hostiles.Add(p);
            }
            var baseline = !_combatScanBaselined;
            _combatScanBaselined = true;
            if (range)
            {
                var turrets = map.listerBuildings.AllBuildingsColonistOfClass<Building_Turret>().Where(t => t.Spawned).ToList();
                foreach (var h in hostiles)
                {
                    // Either direction, each once per hostile per combat: the
                    // hostile's weapon reaching a defender (it can hurt one),
                    // or a defender's weapon reaching it (we can shoot it).
                    if (!_inRange.Contains(h.thingIDNumber))
                    {
                        var hostileRange = PawnRange(h);
                        Thing? near = colonists.FirstOrDefault(c => InRange(h, c, hostileRange));
                        if (near == null) near = turrets.FirstOrDefault(t => InRange(h, t, hostileRange));
                        if (near != null)
                        {
                            _inRange.Add(h.thingIDNumber);
                            if (!baseline) { NoteCombatEvent(Clock.CombatEvent.EnteredRange, h, SafeLoadId(h) + " reaches " + SafeLoadId(near)); return; }
                        }
                    }
                    if (!_inOurRange.Contains(h.thingIDNumber))
                    {
                        Thing? shooter = colonists.FirstOrDefault(c => InRange(c, h, PawnRange(c)));
                        if (shooter == null) shooter = turrets.FirstOrDefault(t => InRange(t, h, TurretRange(t)));
                        if (shooter != null)
                        {
                            _inOurRange.Add(h.thingIDNumber);
                            if (!baseline) { NoteCombatEvent(Clock.CombatEvent.EnteredRange, h, SafeLoadId(h) + " reached by " + SafeLoadId(shooter)); return; }
                        }
                    }
                }
            }
            if (melee)
            {
                foreach (var p in hostiles.Concat(colonists))
                {
                    if (p.CurJobDef != JobDefOf.AttackMelee || !(p.CurJob?.targetA.Thing is Pawn target)) continue;
                    if (p.IsColonist ? !target.HostileTo(Faction.OfPlayer) : !target.IsColonist) continue;
                    var key = ((long)p.thingIDNumber << 32) | (uint)target.thingIDNumber;
                    if (!_meleePairs.Add(key) || baseline) continue;
                    NoteCombatEvent(Clock.CombatEvent.MeleeContact, p, SafeLoadId(p) + " began melee on " + SafeLoadId(target));
                    return;
                }
            }
        }

        private static bool InRange(Thing a, Thing b, float range) => (a.Position - b.Position).LengthHorizontalSquared <= range * range;
        private static float PawnRange(Pawn p)
        {
            var reach = EpochPolicy?.MeleeReachCells ?? 0f;
            try { var verb = p.equipment?.PrimaryEq?.PrimaryVerb; return verb != null && !verb.IsMeleeAttack ? verb.verbProps.range : reach; }
            catch { return reach; }
        }
        private static float TurretRange(Building_Turret t) { try { return t.AttackVerb?.verbProps.range ?? 0f; } catch { return 0f; } }
        private static string SafeLoadId(Thing t) { try { return t.GetUniqueLoadID(); } catch { return t.thingIDNumber.ToString(System.Globalization.CultureInfo.InvariantCulture); } }
        private static bool OnEpochMap(Thing? t) { var s = _state; return t != null && s != null && t.Spawned && ReferenceEquals(t.Map, s.Map); }

        // Health and bleed rate before the damage, for the crossing test.
        private static void OnDamagePrefix(Pawn ___pawn, out Vector2 __state)
        {
            __state = Vector2.zero;
            try { if (___pawn != null && ___pawn.IsColonist) __state = new Vector2(___pawn.health.summaryHealth.SummaryHealthPercent, ___pawn.health.hediffSet.BleedRateTotal); }
            catch { }
        }

        private static void OnDamagePostfix(Pawn ___pawn, DamageInfo dinfo, float totalDamageDealt, Vector2 __state)
        {
            try
            {
                var pawn = ___pawn;
                if (pawn == null) return;
                if (CombatMirror.Active)
                {
                    var serious = pawn.IsColonist ? SeriousWhy(pawn, dinfo, totalDamageDealt, __state) : null;
                    CombatMirror.Record(Mirror.CombatLogKind.Damaged, serious != null ? Clock.CombatEvent.SeriousInjury : Clock.CombatEvent.Unspecified, pawn, dinfo.Instigator,
                        dinfo.Weapon?.defName, totalDamageDealt.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + (serious != null ? "; " + serious : ""));
                }
                if (!pawn.IsColonist || !OnEpochMap(pawn) || !CombatArmed(Clock.CombatEvent.SeriousInjury)) return;
                var why = SeriousWhy(pawn, dinfo, totalDamageDealt, __state);
                if (why != null) NoteCombatEvent(Clock.CombatEvent.SeriousInjury, pawn, why);
            }
            catch { }
        }

        private static string? SeriousWhy(Pawn pawn, DamageInfo dinfo, float totalDamageDealt, Vector2 __state)
        {
            {
                var policy = EpochPolicy;
                if (policy == null) return null;
                var health = pawn.health.summaryHealth.SummaryHealthPercent;
                var bleed = pawn.health.hediffSet.BleedRateTotal;
                string? why = null;
                if (totalDamageDealt >= policy.SeriousSingleHitDamage) why = "single hit of " + totalDamageDealt.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                else if (__state.x > policy.SeriousSummaryHealthFloor && health <= policy.SeriousSummaryHealthFloor) why = "summary health under " + policy.SeriousSummaryHealthFloor;
                else if (__state.y < policy.SeriousBleedRateFloor && bleed >= policy.SeriousBleedRateFloor) why = "bleed rate over " + policy.SeriousBleedRateFloor;
                else
                {
                    var part = dinfo.HitPart;
                    if (part != null && part.def.tags.Any(t => t.vital) && !pawn.health.hediffSet.PartIsMissing(part)
                        && pawn.health.hediffSet.GetPartHealth(part) / part.def.GetMaxHealth(pawn) < policy.SeriousVitalPartFloor)
                        why = "vital part " + part.def.defName + " under " + policy.SeriousVitalPartFloor;
                }
                return why;
            }
        }

        private static void OnShieldBroken(CompShield __instance)
        {
            try
            {
                var wearer = (__instance.parent as Apparel)?.Wearer ?? __instance.parent as Pawn;
                if (wearer != null) CombatMirror.Record(Mirror.CombatLogKind.ShieldBroken, wearer.HostileTo(Faction.OfPlayer) ? Clock.CombatEvent.Unspecified : Clock.CombatEvent.ShieldBroken,
                    wearer, null, __instance.parent.def.defName, null);
                if (wearer == null || !OnEpochMap(wearer) || wearer.HostileTo(Faction.OfPlayer)) return;
                NoteCombatEvent(Clock.CombatEvent.ShieldBroken, wearer, "shield broke");
            }
            catch { }
        }

        private static void OnProjectileLaunched(Projectile __instance, Vector3 ___destination)
        {
            try
            {
                var radius = __instance.def?.projectile?.explosionRadius ?? 0f;
                var launcher = __instance.Launcher;
                if (CombatMirror.Active && launcher != null)
                {
                    var target = ___destination.ToIntVec3();
                    var margin = EpochPolicy?.ExplosiveNearMarginCells;
                    var explosiveNear = margin.HasValue && radius > 0f && launcher.HostileTo(Faction.OfPlayer) && launcher.Map != null
                        && launcher.Map.mapPawns.FreeColonistsSpawned.Any(c => (c.Position - target).LengthHorizontalSquared <= (radius + margin.Value) * (radius + margin.Value));
                    CombatMirror.Record(Mirror.CombatLogKind.ProjectileLaunched, explosiveNear ? Clock.CombatEvent.ExplosiveLaunched : Clock.CombatEvent.Unspecified,
                        launcher, __instance.intendedTarget.Thing, __instance.def?.defName, radius > 0f ? "explosive radius " + radius.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : null, target);
                }
                if (radius <= 0f || launcher == null || !OnEpochMap(launcher) || !launcher.HostileTo(Faction.OfPlayer)) return;
                if (!CombatArmed(Clock.CombatEvent.ExplosiveLaunched)) return;
                var at = ___destination.ToIntVec3();
                var near = radius + _state!.Policy!.ExplosiveNearMarginCells;
                var colonist = _state!.Map.mapPawns.FreeColonistsSpawned.FirstOrDefault(c => (c.Position - at).LengthHorizontalSquared <= near * near);
                if (colonist != null) NoteCombatEvent(Clock.CombatEvent.ExplosiveLaunched, launcher, __instance.def!.defName + " launched at " + SafeLoadId(colonist));
            }
            catch { }
        }

        private static void OnToilPrefix(Lord __instance, out LordToil? __state) { __state = __instance?.CurLordToil; }

        private static void OnToilPostfix(Lord __instance, LordToil newLordToil, LordToil? __state)
        {
            try
            {
                var s = _state;
                if (__state != null && !ReferenceEquals(__state, newLordToil) && __instance != null && CombatMirror.Active)
                {
                    var hostile = __instance.faction != null && __instance.faction.HostileTo(Faction.OfPlayer);
                    CombatMirror.Record(Mirror.CombatLogKind.LordToil, hostile ? Clock.CombatEvent.RaidPhase : Clock.CombatEvent.Unspecified, __instance.ownedPawns.FirstOrDefault(), null,
                        newLordToil.GetType().Name, __state.GetType().Name + " -> " + newLordToil.GetType().Name, null, CombatMirror.Strategy(__instance));
                }
                if (__state == null || ReferenceEquals(__state, newLordToil) || s == null || !ReferenceEquals(__instance!.Map, s.Map)) return;
                if (__instance.faction == null || !__instance.faction.HostileTo(Faction.OfPlayer)) return;
                NoteCombatEvent(Clock.CombatEvent.RaidPhase, __instance.ownedPawns.FirstOrDefault(),
                    __state.GetType().Name + " -> " + newLordToil.GetType().Name);
            }
            catch { }
        }

        private static void OnThingDestroying(Thing __instance, DestroyMode mode)
        {
            try
            {
                if (mode != DestroyMode.KillFinalize) return;
                var def = __instance.def;
                var wall = def.building != null && def.passability == Traversability.Impassable && def.fillPercent >= 0.99f && !def.building.isNaturalRock;
                if (CombatMirror.Active && __instance is Building && __instance.Spawned)
                {
                    var breach = __instance.Faction == Faction.OfPlayer && (wall || def.IsDoor || __instance is Building_Turret);
                    CombatMirror.Record(Mirror.CombatLogKind.BuildingDestroyed, breach ? Clock.CombatEvent.Breach : Clock.CombatEvent.Unspecified, __instance, null, def.defName, null);
                }
                if (__instance.Faction != Faction.OfPlayer || !OnEpochMap(__instance)) return;
                if (!wall && !def.IsDoor && !(__instance is Building_Turret)) return;
                NoteCombatEvent(Clock.CombatEvent.Breach, __instance, def.defName + " destroyed");
            }
            catch { }
        }

        private static void OnMentalState(bool __result, Pawn ___pawn, MentalStateDef stateDef)
        {
            try
            {
                if (__result && ___pawn != null && ___pawn.Spawned)
                    CombatMirror.Record(Mirror.CombatLogKind.MentalState, ___pawn.RaceProps?.Humanlike == true ? Clock.CombatEvent.MentalBreak : Clock.CombatEvent.Unspecified, ___pawn, null, stateDef?.defName, null);
                if (!__result || !OnEpochMap(___pawn) || ___pawn!.RaceProps == null || !___pawn.RaceProps.Humanlike) return;
                NoteCombatEvent(Clock.CombatEvent.MentalBreak, ___pawn, stateDef?.defName ?? "mental state");
            }
            catch { }
        }
    }
}
