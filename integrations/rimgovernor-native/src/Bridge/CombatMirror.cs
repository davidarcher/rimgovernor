#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Clock = RimGovernor.Protocol.Clock;
using Common = RimGovernor.Protocol.Common;
using Mirror = RimGovernor.Protocol.Mirror;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The combat mirror sections (#851): SECTION_COMBAT_PAWNS and
    /// SECTION_COMBAT_EVENTS of rimgovernor/mirror_poll.
    ///
    /// Combat is active while the last poll saw a hostile on the map or a
    /// combat clock epoch runs. Only then do the hooks here do anything past
    /// one static read: a hook that changes a pawn's decision facts (job,
    /// stance, draft, fire mode, equipment) marks the section dirty so the
    /// next poll compares at once; a hook that is an event appends a row to
    /// the event ring at a fresh (tick, seq) mark and stamps the pawns it
    /// concerns with that mark, so their row change and the event share a
    /// watermark. The #849 stop hooks (Supervisor) feed the ring through
    /// Record. The pawn rows are projected at poll time and compared by
    /// digest (EntityTracking); the sampled fields are thresholded in the
    /// projection so noise does not change a row.
    /// </summary>
    internal static class CombatMirror
    {
        internal const string PawnsShape = "rimgovernor/mirror_poll|combat_pawns";
        /// A clean combat pawns section is compared at most this often on a
        /// running game (a dirty one every poll): half a game second.
        internal const int MinCombatCompareTicks = 30;
        /// The event ring's size; a keyframe carries it whole.
        internal const int RingSize = 1024;

        private static volatile bool _active;
        private static volatile bool _dirty;
        private static int _hooked;
        internal static readonly List<string> HookErrors = new List<string>();

        private struct Entry { public EntityTracking.Mark Mark; public object? Game; public int MapId; public Mirror.CombatEventRow Row; }
        private static readonly List<Entry> Ring = new List<Entry>();
        private static EntityTracking.Mark _droppedThrough = new EntityTracking.Mark { Tick = int.MinValue };
        // The event mark per pawn since its row was last compared: the newest
        // event of the highest rank, a downing or death (2) over another stop
        // kind (1) over the rest (0), so a downing inside a damage call keeps
        // the downing's mark.
        private static readonly Dictionary<int, (EntityTracking.Mark Mark, int Rank)> Stamped = new Dictionary<int, (EntityTracking.Mark, int)>();
        private static void Stamp(Thing? thing, EntityTracking.Mark mark, int rank)
        {
            if (!(thing is Pawn pawn)) return;
            if (Stamped.TryGetValue(pawn.thingIDNumber, out var held) && held.Rank > rank) return;
            Stamped[pawn.thingIDNumber] = (mark, rank);
        }

        internal static bool Active => _active || Supervisor.CombatEpochRunning;
        internal static bool Dirty => _dirty;

        internal static void EnsureHooks()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _hooked, 1, 0) != 0) return;
            var harmony = new Harmony("homebridge.combat-mirror");
            void Patch(string name, System.Reflection.MethodBase? target, string? prefix, string? postfix)
            {
                try
                {
                    if (target == null) throw new MissingMethodException(name);
                    harmony.Patch(target, prefix: prefix == null ? null : new HarmonyMethod(typeof(CombatMirror), prefix),
                        postfix: postfix == null ? null : new HarmonyMethod(typeof(CombatMirror), postfix));
                }
                catch (Exception ex)
                {
                    lock (HookErrors) HookErrors.Add(name + ": " + ex.GetType().Name + ": " + ex.Message);
                    Log.Warning("RimGovernor combat mirror hook " + name + " not installed: " + ex.Message);
                }
            }
            Patch("Pawn_JobTracker.StartJob", AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob)), null, nameof(OnPawnTracker));
            Patch("Pawn_JobTracker.EndCurrentJob", AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob)), null, nameof(OnPawnTracker));
            Patch("Pawn_StanceTracker.SetStance", AccessTools.Method(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.SetStance)), null, nameof(OnStance));
            Patch("Pawn_DraftController.Drafted", AccessTools.PropertySetter(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted)), null, nameof(OnDraft));
            Patch("Pawn_DraftController.FireAtWill", AccessTools.PropertySetter(typeof(Pawn_DraftController), nameof(Pawn_DraftController.FireAtWill)), null, nameof(OnDraft));
            Patch("Pawn_EquipmentTracker.Notify_EquipmentAdded", AccessTools.Method(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Notify_EquipmentAdded)), null, nameof(OnEquipment));
            Patch("Pawn_EquipmentTracker.Notify_EquipmentRemoved", AccessTools.Method(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Notify_EquipmentRemoved)), null, nameof(OnEquipment));
            // Verb.TryCastShot is abstract: patch the ranged and melee bases
            // (Verb_Shoot calls the ranged one once per shot).
            Patch("Verb_LaunchProjectile.TryCastShot", AccessTools.DeclaredMethod(typeof(Verb_LaunchProjectile), "TryCastShot"), null, nameof(OnShot));
            Patch("Verb_MeleeAttack.TryCastShot", AccessTools.DeclaredMethod(typeof(Verb_MeleeAttack), "TryCastShot"), null, nameof(OnShot));
            Patch("Explosion.StartExplosion", AccessTools.Method(typeof(Explosion), nameof(Explosion.StartExplosion)), null, nameof(OnExplosion));
            Patch("Fire.SpawnSetup", AccessTools.DeclaredMethod(typeof(Fire), nameof(Fire.SpawnSetup)), null, nameof(OnFire));
            Patch("Building_Door.DoorOpen", AccessTools.Method(typeof(Building_Door), "DoorOpen"), nameof(OnDoorPrefix), nameof(OnDoorPostfix));
            Patch("Building_Door.DoorTryClose", AccessTools.Method(typeof(Building_Door), "DoorTryClose"), nameof(OnDoorPrefix), nameof(OnDoorPostfix));
            Patch("CompShield.Reset", AccessTools.Method(typeof(CompShield), "Reset"), null, nameof(OnShieldReset));
        }

        /// Record appends an event row on thing's map while combat is active,
        /// stamping the pawns among thing and other with its mark.
        internal static void Record(Mirror.CombatLogKind kind, Clock.CombatEvent stop, Thing? thing, Thing? other, string? defName, string? detail, IntVec3? cell = null, string? strategy = null)
        {
            if (!Active) return;
            try
            {
                var map = thing?.MapHeld ?? other?.MapHeld;
                if (map == null) return;
                var mark = EntityTracking.Next();
                var row = new Mirror.CombatEventRow { At = new Mirror.Watermark { Tick = mark.Tick, Seq = mark.Seq }, Kind = kind };
                if (stop != Clock.CombatEvent.Unspecified) row.Stop = stop;
                if (thing != null) row.ThingId = LoadId(thing);
                if (other != null) row.TargetId = LoadId(other);
                if (!string.IsNullOrEmpty(defName)) row.DefName = defName;
                if (!string.IsNullOrEmpty(detail)) row.Detail = detail!.Length > 256 ? detail.Substring(0, 256) : detail;
                if (!string.IsNullOrEmpty(strategy)) row.RaidStrategy = strategy;
                var at = cell ?? thing?.PositionHeld ?? other?.PositionHeld;
                if (at.HasValue && at.Value.IsValid) row.Cell = new Common.Cell { X = at.Value.x, Z = at.Value.z };
                Ring.Add(new Entry { Mark = mark, Game = Verse.Current.Game, MapId = map.uniqueID, Row = row });
                if (Ring.Count > RingSize)
                {
                    _droppedThrough = Ring[0].Mark;
                    Ring.RemoveAt(0);
                }
                Stamp(thing, mark, kind == Mirror.CombatLogKind.Downed || kind == Mirror.CombatLogKind.Killed ? 2 : stop != Clock.CombatEvent.Unspecified ? 1 : 0);
                Stamp(other, mark, 0);
                _dirty = true;
            }
            catch { }
        }

        // The events section: a keyframe of the ring on this map, or the rows
        // after since while the ring still reaches it.
        internal static Mirror.SectionPage Events(Map map, EntityTracking.Mark? since)
        {
            // A load or lab restart sets the game's tick back: events stamped
            // past it belong to the earlier timeline.
            var now = Find.TickManager.TicksGame;
            Ring.RemoveAll(e => e.Mark.Tick > now || !ReferenceEquals(e.Game, Verse.Current.Game));
            if (_droppedThrough.Tick > now) _droppedThrough = new EntityTracking.Mark { Tick = int.MinValue };
            var page = new Mirror.SectionPage { Section = Mirror.Section.CombatEvents };
            if (since == null || since.Value.CompareTo(_droppedThrough) < 0)
            {
                page.Keyframe = new Mirror.Keyframe();
                page.Keyframe.CombatEvents.AddRange(Ring.Where(e => e.MapId == map.uniqueID).Select(e => e.Row.Clone()));
                return page;
            }
            page.Delta = new Mirror.Delta { From = new Mirror.Watermark { Tick = since.Value.Tick, Seq = since.Value.Seq } };
            page.Delta.CombatEvents.AddRange(Ring.Where(e => e.MapId == map.uniqueID && e.Mark.CompareTo(since.Value) > 0).Select(e => e.Row.Clone()));
            return page;
        }

        // The pawns section: every colonist, hostile and colony animal while
        // combat is active (the poll's hostile sighting sets it), noted into
        // the tracker, as a keyframe or the rows changed after since.
        internal static Mirror.SectionPage Pawns(Map map, Mirror.SectionAsk ask, EntityTracking.Mark? since)
        {
            var hostile = false;
            var pawns = new List<(Pawn pawn, Mirror.CombatSide side)>();
            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                Mirror.CombatSide side;
                if (p.IsColonist) side = Mirror.CombatSide.Colonist;
                else if (p.HostileTo(Faction.OfPlayer)) { side = Mirror.CombatSide.Hostile; if (!p.Downed) hostile = true; }
                else if (p.Faction == Faction.OfPlayer && p.RaceProps.Animal) side = Mirror.CombatSide.ColonyAnimal;
                else continue;
                pawns.Add((p, side));
            }
            _active = hostile;
            if (!Active) pawns.Clear();
            var tracking = EntityTracking.For(map, PawnsShape);
            var rows = new List<Mirror.CombatPawn>(pawns.Count);
            foreach (var (pawn, side) in pawns)
            {
                var row = Project(pawn, side);
                tracking.Note(row.Id, row, Stamped.TryGetValue(pawn.thingIDNumber, out var stamp) ? stamp.Mark : (EntityTracking.Mark?)null);
                var changed = tracking.Changed(row.Id);
                if (changed.HasValue) row.Changed = new Mirror.Watermark { Tick = changed.Value.Tick, Seq = changed.Value.Seq };
                rows.Add(row);
            }
            tracking.Sweep(new HashSet<string>(rows.Select(r => r.Id)));
            Stamped.Clear();
            _dirty = false;
            var page = new Mirror.SectionPage { Section = Mirror.Section.CombatPawns };
            if (since == null || !tracking.Covers(since.Value.Tick))
            {
                page.Keyframe = new Mirror.Keyframe();
                page.Keyframe.CombatPawns.AddRange(rows);
                return page;
            }
            page.Delta = new Mirror.Delta { From = new Mirror.Watermark { Tick = since.Value.Tick, Seq = since.Value.Seq } };
            page.Delta.CombatPawns.AddRange(rows.Where(r => tracking.ChangedAfter(r.Id, since.Value)));
            page.Delta.Tombstones.AddRange(tracking.RemovedAfter(since.Value));
            if (ask.Resync)
            {
                page.Resync = new Mirror.Keyframe();
                page.Resync.CombatPawns.AddRange(rows);
            }
            return page;
        }

        private static double Step(double value, double step) => Math.Round(value / step) * step;

        internal static Mirror.CombatPawn Project(Pawn pawn, Mirror.CombatSide side)
        {
            var row = new Mirror.CombatPawn { Id = LoadId(pawn), Side = side, Cell = new Common.Cell { X = pawn.Position.x, Z = pawn.Position.z }, Downed = pawn.Downed, Dead = pawn.Dead };
            try { if (pawn.Faction != null) row.FactionId = pawn.Faction.GetUniqueLoadID(); } catch { }
            try { var lord = pawn.GetLord(); if (lord != null) row.LordId = lord.GetUniqueLoadID(); } catch { }
            if (pawn.MentalStateDef != null) row.MentalState = pawn.MentalStateDef.defName;
            if (pawn.drafter != null)
            {
                row.Drafted = pawn.drafter.Drafted;
                if (pawn.drafter.Drafted) row.FireMode = pawn.drafter.FireAtWill ? "fire_at_will" : "hold_fire";
            }
            var job = pawn.CurJob;
            if (job != null)
            {
                row.Job = job.def.defName;
                if (job.targetA.Thing != null) row.TargetId = LoadId(job.targetA.Thing);
            }
            var stance = pawn.stances?.curStance;
            if (stance is Stance_Warmup warmup) { row.Stance = Mirror.CombatStance.Warmup; row.StanceTicksLeft = warmup.ticksLeft; }
            else if (stance is Stance_Cooldown cooldown) { row.Stance = Mirror.CombatStance.Cooldown; row.StanceTicksLeft = cooldown.ticksLeft; }
            else if (job != null && job.def == JobDefOf.AttackMelee) row.Stance = Mirror.CombatStance.Melee;
            else if (pawn.pather != null && pawn.pather.Moving) row.Stance = Mirror.CombatStance.Moving;
            else row.Stance = Mirror.CombatStance.Idle;
            try
            {
                row.Health = Step(pawn.health.summaryHealth.SummaryHealthPercent, 0.01);
                row.BleedRate = Step(pawn.health.hediffSet.BleedRateTotal, 0.05);
                row.Pain = Step(pawn.health.hediffSet.PainTotal, 0.05);
                row.MoveSpeed = Step(pawn.GetStatValue(StatDefOf.MoveSpeed), 0.1);
            }
            catch { }
            var shield = pawn.apparel?.WornApparel.Select(a => a.GetComp<CompShield>()).FirstOrDefault(c => c != null);
            if (shield != null)
            {
                try
                {
                    var max = shield.parent.GetStatValue(StatDefOf.EnergyShieldEnergyMax);
                    row.ShieldEnergy = max > 0 ? Step(Math.Max(0, Math.Min(1, shield.Energy / max)), 0.05) : 0;
                    row.ShieldBroken = shield.ShieldState == ShieldState.Resetting;
                }
                catch { }
            }
            var weapon = pawn.equipment?.Primary;
            var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (weapon != null && verb != null)
            {
                row.Weapon = weapon.def.defName;
                row.WeaponMelee = verb.IsMeleeAttack;
                row.WeaponRange = verb.IsMeleeAttack ? Supervisor.MeleeReachCells : verb.verbProps.range;
                row.WeaponWarmupTicks = verb.verbProps.warmupTime.SecondsToTicks();
                try { row.WeaponCooldownTicks = weapon.GetStatValue(verb.IsMeleeAttack ? StatDefOf.MeleeWeapon_CooldownMultiplier : StatDefOf.RangedWeapon_Cooldown).SecondsToTicks(); }
                catch { }
            }
            return row;
        }

        internal static string LoadId(Thing thing)
        {
            try { return thing.GetUniqueLoadID(); }
            catch { return "Thing_" + thing.thingIDNumber.ToString(CultureInfo.InvariantCulture); }
        }

        // Hooks. Each is one static read unless combat is active.
        private static void Touch(Pawn? pawn) { if (pawn != null && pawn.Spawned && Active) _dirty = true; }
        private static void OnPawnTracker(Pawn ___pawn) => Touch(___pawn);
        private static void OnStance(Pawn ___pawn) => Touch(___pawn);
        private static void OnDraft(Pawn ___pawn) => Touch(___pawn);
        private static void OnEquipment(Pawn ___pawn) => Touch(___pawn);

        private static void OnShot(Verb __instance, bool __result)
        {
            if (!__result || !Active) return;
            try
            {
                var caster = __instance.caster;
                if (caster == null || !caster.Spawned) return;
                var target = __instance.CurrentTarget;
                Record(Mirror.CombatLogKind.ShotFired, Clock.CombatEvent.Unspecified, caster, target.Thing, __instance.EquipmentSource?.def.defName ?? __instance.verbProps.label,
                    null, caster.Position);
            }
            catch { }
        }

        private static void OnExplosion(Explosion __instance)
        {
            if (!Active) return;
            try { Record(Mirror.CombatLogKind.Explosion, Clock.CombatEvent.Unspecified, __instance, __instance.instigator, __instance.damType?.defName,
                "radius " + __instance.radius.ToString("0.#", CultureInfo.InvariantCulture)); }
            catch { }
        }

        private static void OnFire(Fire __instance)
        {
            if (!Active) return;
            try { Record(Mirror.CombatLogKind.FireStarted, Clock.CombatEvent.Unspecified, __instance, __instance.parent, null, null); }
            catch { }
        }

        private static void OnDoorPrefix(Building_Door __instance, out bool __state) { __state = __instance != null && __instance.Open; }

        private static void OnDoorPostfix(Building_Door __instance, bool __state)
        {
            if (!Active || __instance == null || __instance.Open == __state) return;
            Record(__instance.Open ? Mirror.CombatLogKind.DoorOpened : Mirror.CombatLogKind.DoorClosed, Clock.CombatEvent.Unspecified, __instance, null, __instance.def.defName, null);
        }

        private static void OnShieldReset(CompShield __instance)
        {
            if (!Active) return;
            var wearer = (__instance.parent as Apparel)?.Wearer;
            if (wearer != null) { _dirty = true; Stamp(wearer, EntityTracking.Next(), 0); }
        }

        /// The raid strategy a lord is running, by its lord job: a siege, or
        /// an assault with sappers or breachers.
        internal static string Strategy(Lord lord)
        {
            var job = lord.LordJob;
            if (job == null) return "";
            var name = job.GetType().Name;
            var t = Traverse.Create(job);
            try { if (t.Field("sappers").FieldExists() && t.Field("sappers").GetValue<bool>()) name += "+sappers"; } catch { }
            try { if (t.Field("breachers").FieldExists() && t.Field("breachers").GetValue<bool>()) name += "+breachers"; } catch { }
            return name;
        }
    }
}
