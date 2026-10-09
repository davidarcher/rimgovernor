#nullable enable
using RimGovernor.Host.Sdk;

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
using Observations = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Combat state for the snapshot stream: the combat_pawns
    /// and combat_events sections of every frame.
    ///
    /// Combat is active while the last capture saw a hostile on the map or a
    /// combat clock epoch runs. Only then do the hooks here do anything past
    /// one static read: a hook that changes a pawn's decision facts (job,
    /// stance, draft, fire mode, equipment) marks the state dirty so the
    /// stream captures sooner (MinCombatCompareTicks); a hook that is an
    /// event appends a row to the event ring at a fresh (tick, seq) mark and
    /// stamps the pawns it concerns with that mark, so their row change and
    /// the event share a watermark. The stop hooks (Supervisor) feed the
    /// ring through Record. The pawn rows are projected at capture time and
    /// compared with the previous capture's; the sampled fields are
    /// thresholded in the projection so noise does not change a row.
    /// </summary>
    internal static class CombatMirror
    {
        /// While combat is dirty the stream captures at most this often on a
        /// running game: half a game second.
        internal const int MinCombatCompareTicks = 30;
        /// The event ring's size; every frame carries it whole.
        internal const int RingSize = 1024;

        private static volatile bool _active;
        private static volatile bool _dirty;
        private static int _hooked;
        internal static readonly List<string> HookErrors = new List<string>();

        /// A (tick, seq) watermark: seq orders the marks taken within a tick.
        internal struct Mark : IComparable<Mark>
        {
            public int Tick;
            public uint Seq;
            public int CompareTo(Mark other) => Tick != other.Tick ? Tick.CompareTo(other.Tick) : Seq.CompareTo(other.Seq);
            public Mirror.Watermark Wire() => new Mirror.Watermark { Tick = Tick, Seq = Seq };
        }
        private static Mark _last = new Mark { Tick = int.MinValue };
        /// Next is a fresh mark, after every mark taken before it.
        internal static Mark Next()
        {
            var tick = Find.TickManager?.TicksGame ?? 0;
            _last = tick == _last.Tick ? new Mark { Tick = tick, Seq = _last.Seq + 1 } : new Mark { Tick = tick };
            return _last;
        }

        private struct Entry { public Mark Mark; public object? Game; public int MapId; public Mirror.CombatEventRow Row; }
        private static readonly List<Entry> Ring = new List<Entry>();
        // The event mark per pawn since the last capture: the newest event of
        // the highest rank, a downing or death (2) over another stop kind (1)
        // over the rest (0), so a downing inside a damage call keeps the
        // downing's mark.
        private static readonly Dictionary<int, (Mark Mark, int Rank)> Stamped = new Dictionary<int, (Mark, int)>();
        // Each pawn row's hash as last captured (without its changed mark),
        // by the shared row diff, and the mark of its last change,
        // keyed by load id.
        private static readonly RowDiff Compared = new RowDiff();
        private static readonly Dictionary<string, Mark> ChangedAt = new Dictionary<string, Mark>();
        private static void Stamp(Thing? thing, Mark mark, int rank)
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
                    ModLog.Warn("startup", "combat mirror hook " + name + " not installed: " + ex.Message);
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
            // Drop-pod arrivals: the pods strategy is the arrival
            // mode, not the lord job, so the lord toil rows never carry it.
            foreach (var worker in typeof(PawnsArrivalModeWorker).AllSubclassesNonAbstract().Where(t => t.Name.Contains("Drop") && AccessTools.DeclaredMethod(t, nameof(PawnsArrivalModeWorker.Arrive)) != null))
                Patch(worker.Name + ".Arrive", AccessTools.DeclaredMethod(worker, nameof(PawnsArrivalModeWorker.Arrive)), null, nameof(OnDropArrival));
        }

        /// A pod's open tick when its holder chain cannot be read: vanilla's
        /// skyfaller fall plus ActiveDropPodInfo's default open delay.
        internal const int PodOpenFallbackTicks = 520;

        // A drop-pod arrival mode placed hostile pawns in incoming pods: one
        // hostile arrived row with strategy "pods", the landing cells and
        // the tick the last pod opens. A raid on its way starts combat.
        private static void OnDropArrival(PawnsArrivalModeWorker __instance, List<Pawn> pawns, IncidentParms parms)
        {
            try
            {
                if (pawns == null || pawns.Count == 0 || !pawns[0].HostileTo(Faction.OfPlayer)) return;
                var now = Find.TickManager.TicksGame;
                var open = 0;
                var cells = new List<IntVec3>();
                foreach (var pawn in pawns)
                {
                    var at = pawn.PositionHeld;
                    if (at.IsValid && !cells.Contains(at)) cells.Add(at);
                    // pawn -> pod contents (openDelay) -> pod -> incoming skyfaller.
                    var info = pawn.ParentHolder;
                    var fall = info?.ParentHolder?.ParentHolder as Skyfaller;
                    var delay = info == null ? null : Traverse.Create(info).Field("openDelay");
                    open = Math.Max(open, fall != null && delay != null && delay.FieldExists() ? now + fall.ticksToImpact + delay.GetValue<int>() : now + PodOpenFallbackTicks);
                }
                _active = true;
                Record(Mirror.CombatLogKind.HostileArrived, Clock.CombatEvent.Unspecified, pawns[0], null, __instance.def?.defName ?? __instance.GetType().Name,
                    pawns.Count + " pawns in pods", cells.Count > 0 ? cells[0] : (IntVec3?)null, "pods", row =>
                    {
                        row.OpenTick = open;
                        row.LandingCells.AddRange(cells.Select(c => new Common.Cell { X = c.x, Z = c.z }));
                    });
            }
            catch { } // Harmony hook inside vanilla code, outside any tool call: a throw would reach the game (not probed in #1887)
        }

        /// Record appends an event row on thing's map while combat is active,
        /// stamping the pawns among thing and other with its mark.
        internal static void Record(Mirror.CombatLogKind kind, Clock.CombatEvent stop, Thing? thing, Thing? other, string? defName, string? detail, IntVec3? cell = null, string? strategy = null, Action<Mirror.CombatEventRow>? fill = null)
        {
            if (!Active) return;
            try
            {
                var map = thing?.MapHeld ?? other?.MapHeld;
                if (map == null) return;
                var mark = Next();
                var row = new Mirror.CombatEventRow { At = new Mirror.Watermark { Tick = mark.Tick, Seq = mark.Seq }, Kind = kind };
                if (stop != Clock.CombatEvent.Unspecified) row.Stop = stop;
                if (thing != null) row.ThingId = LoadId(thing);
                if (other != null) row.TargetId = LoadId(other);
                if (!string.IsNullOrEmpty(defName)) row.DefName = defName;
                if (!string.IsNullOrEmpty(detail)) row.Detail = detail!.Length > 256 ? detail.Substring(0, 256) : detail;
                if (!string.IsNullOrEmpty(strategy)) row.RaidStrategy = strategy;
                fill?.Invoke(row);
                var at = cell ?? thing?.PositionHeld ?? other?.PositionHeld;
                if (at.HasValue && at.Value.IsValid) row.Cell = new Common.Cell { X = at.Value.x, Z = at.Value.z };
                Ring.Add(new Entry { Mark = mark, Game = Verse.Current.Game, MapId = map.uniqueID, Row = row });
                if (Ring.Count > RingSize) Ring.RemoveAt(0);
                Stamp(thing, mark, kind == Mirror.CombatLogKind.Downed || kind == Mirror.CombatLogKind.Killed ? 2 : stop != Clock.CombatEvent.Unspecified ? 1 : 0);
                Stamp(other, mark, 0);
                _dirty = true;
            }
            catch { } // called from Harmony hooks inside vanilla code: a throw would reach the game (not probed in #1887)
        }

        /// Capture fills a frame's combat sections on the game thread: every
        /// colonist, hostile and colony animal while combat is active (a
        /// spawned, undowned hostile sets it), and the event ring on this
        /// map, oldest first. A pawn row's changed mark is the event that
        /// last concerned it (a downing outranks the damage around it), else
        /// the capture that first saw its current row.
        internal static void Capture(Map map, Observations.BundleSnapshot frame)
        {
            // A load or lab restart sets the game's tick back: events stamped
            // past it belong to the earlier timeline.
            var now = Find.TickManager.TicksGame;
            Ring.RemoveAll(e => e.Mark.Tick > now || !ReferenceEquals(e.Game, Verse.Current.Game));
            var hostile = false;
            var pawns = new List<(Pawn pawn, Mirror.CombatSide side)>();
            var wild = new List<Pawn>();
            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                Mirror.CombatSide side;
                if (p.IsColonist) side = Mirror.CombatSide.Colonist;
                else if (p.HostFaction == Faction.OfPlayer && PrisonBreakUtility.IsPrisonBreaking(p)) { side = Mirror.CombatSide.Prisoner; if (!p.Downed) hostile = true; }
                else if (p.HostileTo(Faction.OfPlayer)) { side = Mirror.CombatSide.Hostile; if (!p.Downed) hostile = true; }
                else if (p.Faction == Faction.OfPlayer && p.RaceProps.Animal) side = Mirror.CombatSide.ColonyAnimal;
                else if (p.Faction == null && p.RaceProps.Animal && (p.RaceProps.predator || p.BodySize >= 1f)) { wild.Add(p); continue; }
                else continue;
                pawns.Add((p, side));
            }
            // Wild predators and large animals near an undowned hostile
            //: the policy may shoot one to enrage it at the raiders.
            var raiders = pawns.Where(e => e.side == Mirror.CombatSide.Hostile && !e.pawn.Downed).Select(e => e.pawn.Position).ToList();
            foreach (var w in wild)
                if (raiders.Any(c => c.InHorDistOf(w.Position, WildRange))) pawns.Add((w, Mirror.CombatSide.WildAnimal));
            // Raiders still in their drop pods are not spawned: a pods
            // arrival keeps combat active until its open tick.
            _active = hostile || Ring.Any(e => e.MapId == map.uniqueID && e.Row.OpenTick > now);
            _dirty = false;
            if (!Active)
            {
                Stamped.Clear();
                Compared.Reset();
                ChangedAt.Clear();
                return;
            }
            Mark? capture = null;
            var rows = pawns.Select(e => (e.pawn, row: Project(e.pawn, e.side))).ToList();
            var step = Compared.Step(rows.Select(e => new KeyValuePair<string, Mirror.CombatPawn>(e.row.Id, e.row)));
            foreach (var (pawn, row) in rows)
            {
                Mark changed;
                if (Stamped.TryGetValue(pawn.thingIDNumber, out var stamp)) changed = stamp.Mark;
                else if (!step.Changed.Contains(row.Id) && ChangedAt.TryGetValue(row.Id, out var held)) changed = held;
                else changed = capture ??= Next();
                ChangedAt[row.Id] = changed;
                row.Changed = changed.Wire();
                frame.CombatPawns.Add(row);
            }
            foreach (var id in step.Removed) ChangedAt.Remove(id);
            Stamped.Clear();
            frame.CombatEvents.AddRange(Ring.Where(e => e.MapId == map.uniqueID).Select(e => e.Row.Clone()));
        }

        private const float WildRange = 15f;

        private static double Step(double value, double step) => Math.Round(value / step) * step;

        // A hediff by def name; false when the def is not loaded.
        internal static bool HasHediff(Pawn pawn, string defName)
        {
            var def = DefDatabase<HediffDef>.GetNamedSilentFail(defName);
            return def != null && pawn.health?.hediffSet?.HasHediff(def) == true;
        }

        internal static Mirror.CombatPawn Project(Pawn pawn, Mirror.CombatSide side)
        {
            var row = new Mirror.CombatPawn { Id = LoadId(pawn), Side = side, Cell = new Common.Cell { X = pawn.Position.x, Z = pawn.Position.z }, Downed = pawn.Downed, Dead = pawn.Dead };
            try { if (pawn.Faction != null) row.Faction = NativeRef.Of(pawn.Faction); } catch { } // one pawn field must not drop the whole combat frame; it stays at its unobserved default
            try { var lord = pawn.GetLord(); if (lord != null) row.LordId = lord.GetUniqueLoadID(); } catch { } // one pawn field must not drop the whole combat frame; it stays at its unobserved default
            if (pawn.MentalStateDef != null) row.MentalState = pawn.MentalStateDef.defName;
            if (pawn.drafter != null)
            {
                row.Drafted = pawn.drafter.Drafted;
                if (pawn.drafter.Drafted) row.FireMode = pawn.drafter.FireAtWill ? RimGovernor.Protocol.Operations.CombatFireMode.AtWill : RimGovernor.Protocol.Operations.CombatFireMode.Hold;
            }
            var job = pawn.CurJob;
            if (job != null)
            {
                row.Job = job.def.defName;
                var target = job.targetA.Thing;
                if (target != null)
                {
                    row.TargetId = LoadId(target);
                    // A frame builds its entity def.
                    var built = target is Frame frame ? frame.def.entityDefToBuild as ThingDef : target.def;
                    if (built?.building?.IsMortar == true) row.TargetMortar = true;
                }
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
                row.MeleePower = Step(pawn.GetStatValue(StatDefOf.MeleeDPS), 0.1);
            }
            catch { } // one pawn field must not drop the whole combat frame; it stays at its unobserved default
            // Enemy drugs.
            row.GoJuiceHigh = HasHediff(pawn, "GoJuiceHigh");
            row.LuciferiumAddicted = HasHediff(pawn, "LuciferiumAddiction");
            if (side == Mirror.CombatSide.Colonist)
            {
                var drugs = new Mirror.CarriedDrugs();
                if (pawn.inventory != null)
                    drugs.Defs.Add(pawn.inventory.innerContainer
                        .Where(t => t.stackCount > 0 && t.def.GetCompProperties<CompProperties_Drug>() != null)
                        .Select(t => t.def.defName).Distinct().OrderBy(name => name, StringComparer.Ordinal));
                row.CarriedDrugs = drugs;
            }
            var shield = pawn.apparel?.WornApparel.Select(a => a.GetComp<CompShield>()).FirstOrDefault(c => c != null);
            if (shield != null)
            {
                try
                {
                    var max = shield.parent.GetStatValue(StatDefOf.EnergyShieldEnergyMax);
                    row.ShieldEnergy = max > 0 ? Step(Math.Max(0, Math.Min(1, shield.Energy / max)), 0.05) : 0;
                    row.ShieldBroken = shield.ShieldState == ShieldState.Resetting;
                }
                catch { } // one pawn field must not drop the whole combat frame; it stays at its unobserved default
            }
            if (side == Mirror.CombatSide.Colonist)
            {
                try { row.Armor = Step(NativeGearFacts.PawnArmor(pawn), 0.05); }
                catch { } // one pawn field must not drop the whole combat frame; it stays at its unobserved default
            }
            try { var stun = pawn.stances?.stunner; if (stun != null && stun.Stunned) row.StunTicksLeft = (stun.StunTicksLeft + 29) / 30 * 30; } catch { } // one pawn field must not drop the whole combat frame; it stays at its unobserved default
            row.ShieldBelt = shield != null;
            try { var medicine = pawn.skills?.GetSkill(SkillDefOf.Medicine); if (medicine != null) row.MedicalSkill = medicine.Level; } catch { } // one pawn field must not drop the whole combat frame; it stays at its unobserved default
            var weapon = pawn.equipment?.Primary;
            var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (weapon != null && verb != null)
            {
                row.Weapon = weapon.def.defName;
                row.WeaponWarmupTicks = verb.verbProps.warmupTime.SecondsToTicks();
                try { row.WeaponCooldownTicks = weapon.GetStatValue(verb.IsMeleeAttack ? StatDefOf.MeleeWeapon_CooldownMultiplier : StatDefOf.RangedWeapon_Cooldown).SecondsToTicks(); }
                catch { } // one pawn field must not drop the whole combat frame; it stays at its unobserved default
            }
            return row;
        }

        internal static string LoadId(Thing thing)
        {
            try { return thing.GetUniqueLoadID(); }
            catch { /* a thing without a load id keeps a stable synthetic one */ return "Thing_" + thing.thingIDNumber.ToString(CultureInfo.InvariantCulture); }
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
            catch { } // Harmony hook inside vanilla code: a throw would reach the game (not probed in #1887)
        }

        private static void OnExplosion(Explosion __instance)
        {
            if (!Active) return;
            try { Record(Mirror.CombatLogKind.Explosion, Clock.CombatEvent.Unspecified, __instance, __instance.instigator, __instance.damType?.defName,
                "radius " + __instance.radius.ToString("0.#", CultureInfo.InvariantCulture)); }
            catch { } // Harmony hook inside vanilla code: a throw would reach the game (not probed in #1887)
        }

        private static void OnFire(Fire __instance)
        {
            if (!Active) return;
            try { Record(Mirror.CombatLogKind.FireStarted, Clock.CombatEvent.Unspecified, __instance, __instance.parent, null, null); }
            catch { } // Harmony hook inside vanilla code: a throw would reach the game (not probed in #1887)
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
            if (wearer != null) { _dirty = true; Stamp(wearer, Next(), 0); }
        }

        /// The raid strategy a lord is running, by its lord job: a siege, or
        /// an assault with sappers or breachers.
        internal static string Strategy(Lord lord)
        {
            var job = lord.LordJob;
            if (job == null) return "";
            var name = job.GetType().Name;
            var t = Traverse.Create(job);
            try { if (t.Field("sappers").FieldExists() && t.Field("sappers").GetValue<bool>()) name += "+sappers"; } catch { } // reflection over a LordJob field a mod may type differently; the name omits the suffix
            try { if (t.Field("breachers").FieldExists() && t.Field("breachers").GetValue<bool>()) name += "+breachers"; } catch { } // reflection over a LordJob field a mod may type differently; the name omits the suffix
            return name;
        }
    }
}
