#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeCombatDamageRecord
    {
        internal readonly Game Game;
        internal readonly Map Map;
        internal readonly Pawn Attacker, Target;
        internal readonly Job Job;
        internal readonly int JobId;
        internal readonly Func<bool> Guard;
        internal NativeCombatDamageRecord(Game game, Pawn attacker, Pawn target, Job job, Func<bool> guard)
        { Game=game; Map=target.Map; Attacker=attacker; Target=target; Job=job; JobId=job.loadID; Guard=guard; }
        internal bool ObservedDamage { get; private set; }
        internal bool CausedDowning { get; private set; }
        internal bool CausedDeath { get; private set; }
        internal long ObservedTick { get; private set; }
        internal void Record(float amount, bool wasDead, bool wasDowned, bool dead, bool downed, long tick)
        {
            if (float.IsNaN(amount) || float.IsInfinity(amount) || amount<=0 || wasDead || tick<0) return;
            ObservedDamage=true; ObservedTick=tick;
            CausedDeath |= dead;
            CausedDowning |= !wasDowned && downed;
        }
    }

    // Melee damage is synchronous with the admitted attack job. Ranged projectiles
    // need their own launch lineage before this evidence can cover them.
    internal static class NativeCombatCausality
    {
        private const string Owner="rimgovernor.combat-causality";
        private sealed class TargetState
        {
            internal readonly List<NativeCombatDamageRecord> Records=new List<NativeCombatDamageRecord>();
            internal long Sequence;
            internal bool Exhausted;
        }
        private sealed class GameState
        {
            internal readonly Dictionary<Pawn,TargetState> Targets=new Dictionary<Pawn,TargetState>();
            internal int Count;
        }
        private sealed class PendingDamage
        {
            internal NativeCombatDamageRecord Record=null!;
            internal TargetState Target=null!;
            internal long Sequence;
            internal bool WasDead, WasDowned;
        }
        private sealed class MeleeScope
        {
            internal MeleeScope? Previous;
            internal Pawn? Caster;
            internal Thing? Victim;
            internal Job? Job;
            internal int JobId;
        }
        private sealed class DamageFrame
        {
            internal int PreviousDepth;
            internal readonly List<PendingDamage> Pending=new List<PendingDamage>();
        }
        [ThreadStatic] private static MeleeScope? meleeScope;
        [ThreadStatic] private static int damageDepth;
        private static readonly ConditionalWeakTable<Game,GameState> Games=new ConditionalWeakTable<Game,GameState>();
        private static readonly MethodInfo Target=AccessTools.Method(typeof(Thing),nameof(Thing.TakeDamage),new[]{typeof(DamageInfo)});
        private static readonly MethodInfo Prefix=AccessTools.Method(typeof(NativeCombatCausality),nameof(BeforeDamage));
        private static readonly MethodInfo Postfix=AccessTools.Method(typeof(NativeCombatCausality),nameof(AfterDamage));
        private static readonly MethodInfo DamageFinalizer=AccessTools.Method(typeof(NativeCombatCausality),nameof(EndDamage));
        private static readonly MethodInfo MeleeTarget=AccessTools.DeclaredMethod(typeof(Verb_MeleeAttackDamage),"ApplyMeleeDamageToTarget",new[]{typeof(LocalTargetInfo)});
        private static readonly MethodInfo MeleePrefix=AccessTools.Method(typeof(NativeCombatCausality),nameof(BeforeMelee));
        private static readonly MethodInfo MeleeFinalizer=AccessTools.Method(typeof(NativeCombatCausality),nameof(EndMelee));
        internal static void Initialize()
        {
            if (IsReady) return;
            if (Target==null || Prefix==null || Postfix==null || DamageFinalizer==null || MeleeTarget==null || MeleePrefix==null || MeleeFinalizer==null) return;
            try {
                var harmony=new Harmony(Owner);
                var damage=Harmony.GetPatchInfo(Target);
                var damagePrefix=Missing(damage?.Prefixes,Prefix);
                var damagePostfix=Missing(damage?.Postfixes,Postfix);
                var damageFinalizer=Missing(damage?.Finalizers,DamageFinalizer);
                if (damagePrefix!=null || damagePostfix!=null || damageFinalizer!=null)
                    harmony.Patch(Target,damagePrefix,damagePostfix,finalizer:damageFinalizer);
                var melee=Harmony.GetPatchInfo(MeleeTarget);
                var meleePrefix=Missing(melee?.Prefixes,MeleePrefix);
                var meleeFinalizer=Missing(melee?.Finalizers,MeleeFinalizer);
                if (meleePrefix!=null || meleeFinalizer!=null)
                    harmony.Patch(MeleeTarget,meleePrefix,finalizer:meleeFinalizer);
            }
            catch { /* Missing live hooks keep admission unavailable. */ }
        }
        private static HarmonyMethod? Missing(IEnumerable<Patch>? hooks,MethodInfo method)
            => hooks!=null && hooks.Any(p=>p.owner==Owner && NativeConstructionHookSet.SameMethod(p.PatchMethod,method)) ? null : new HarmonyMethod(method);
        internal static bool IsReady
        {
            get {
                try {
                    var hooks=Target==null?null:Harmony.GetPatchInfo(Target);
                    var melee=MeleeTarget==null?null:Harmony.GetPatchInfo(MeleeTarget);
                    return hooks!=null && melee!=null && hooks.Prefixes.Count(p=>p.owner==Owner && NativeConstructionHookSet.SameMethod(p.PatchMethod,Prefix))==1
                        && hooks.Postfixes.Count(p=>p.owner==Owner && NativeConstructionHookSet.SameMethod(p.PatchMethod,Postfix))==1
                        && hooks.Finalizers.Count(p=>p.owner==Owner && NativeConstructionHookSet.SameMethod(p.PatchMethod,DamageFinalizer))==1
                        && melee.Prefixes.Count(p=>p.owner==Owner && NativeConstructionHookSet.SameMethod(p.PatchMethod,MeleePrefix))==1
                        && melee.Finalizers.Count(p=>p.owner==Owner && NativeConstructionHookSet.SameMethod(p.PatchMethod,MeleeFinalizer))==1;
                } catch { return false; }
            }
        }
        internal static NativeCombatDamageRecord Track(Game game,Pawn attacker,Pawn target,Job job,Func<bool> guard)
        {
            if (!UnityData.IsInMainThread || !IsReady || game!=Current.Game || attacker==null || target==null
                || !attacker.Spawned || !target.Spawned || attacker.Map!=target.Map || !ProtoBoundary.IsLoaded(target.Map)
                || job==null || job.def!=JobDefOf.AttackMelee || job.targetA.Thing!=target || guard==null)
                throw new InvalidOperationException("Exact live melee damage tracking prerequisites are unavailable.");
            var state=Games.GetOrCreateValue(game);
            if (state.Count>=4096) throw new InvalidOperationException("Combat damage tracking capacity exhausted.");
            if (!state.Targets.TryGetValue(target,out var tracked)) { tracked=new TargetState(); state.Targets.Add(target,tracked); }
            var record=new NativeCombatDamageRecord(game,attacker,target,job,guard);
            tracked.Records.Add(record); state.Count++;
            return record;
        }
        private static void BeforeMelee(Verb_MeleeAttackDamage __instance,LocalTargetInfo target,out MeleeScope? __state)
        {
            __state=null;
            try {
                if (Harmony.GetPatchInfo(MeleeTarget)?.Finalizers.Count(p=>p.owner==Owner && NativeConstructionHookSet.SameMethod(p.PatchMethod,MeleeFinalizer))!=1) {
                    // Do not leave an unpaired scope behind, or let an enclosing
                    // scope adopt an unverified nested invocation after repair.
                    if (meleeScope!=null) meleeScope.Caster=null;
                    return;
                }
                __state=new MeleeScope {Previous=meleeScope};
                meleeScope=__state;
                if (!UnityData.IsInMainThread || !IsReady || damageDepth!=0) return;
                var caster=__instance.CasterPawn;
                var job=caster?.CurJob;
                if (caster==null || job==null || job.def!=JobDefOf.AttackMelee || job.targetA.Thing!=target.Thing) return;
                __state.Caster=caster; __state.Victim=target.Thing; __state.Job=job; __state.JobId=job.loadID;
            } catch { if (__state!=null) __state.Caster=null; }
        }
        private static Exception? EndMelee(Exception? __exception,MeleeScope? __state)
        {
            try { if (__state!=null) meleeScope=__state.Previous; }
            catch { meleeScope=null; }
            return __exception;
        }
        private static Exception? EndDamage(Exception? __exception,DamageFrame? __state)
        {
            try { if (__state!=null) damageDepth=__state.PreviousDepth; }
            catch { damageDepth=0; }
            return __exception;
        }
        private static void BeforeDamage(Thing __instance,DamageInfo dinfo,out DamageFrame? __state)
        {
            __state=null;
            try {
                if (Harmony.GetPatchInfo(Target)?.Finalizers.Count(p=>p.owner==Owner && NativeConstructionHookSet.SameMethod(p.PatchMethod,DamageFinalizer))==1) {
                    __state=new DamageFrame {PreviousDepth=damageDepth};
                    damageDepth=checked(damageDepth+1);
                }
                if (!UnityData.IsInMainThread || Current.Game==null || !(__instance is Pawn victim)
                    || !Games.TryGetValue(Current.Game,out var game) || !game.Targets.TryGetValue(victim,out var target)) return;
                if (target.Exhausted || target.Sequence==long.MaxValue) { target.Exhausted=true; return; }
                var sequence=++target.Sequence;
                var scope=meleeScope;
                if (__state==null || __state.PreviousDepth!=0 || scope==null || scope.Caster==null || scope.Victim!=victim || !IsReady || victim.Dead) return;
                foreach (var record in target.Records) {
                    if (scope.Caster!=record.Attacker || scope.Job!=record.Job || scope.JobId!=record.JobId || record.CausedDeath || record.Game!=Current.Game || !ProtoBoundary.IsLoaded(record.Map)
                        || record.Map!=victim.Map || dinfo.Instigator!=record.Attacker || record.Attacker.CurJob!=record.Job
                        || record.Job.loadID!=record.JobId || record.Job.def!=JobDefOf.AttackMelee || record.Job.targetA.Thing!=victim) continue;
                    if (!record.Guard()) continue;
                    __state.Pending.Add(new PendingDamage {Record=record,Target=target,Sequence=sequence,WasDead=victim.Dead,WasDowned=victim.Downed});
                }
            } catch { if (__state!=null) __state.Pending.Clear(); }
        }
        private static void AfterDamage(DamageInfo dinfo,DamageWorker.DamageResult __result,DamageFrame? __state)
        {
            if (__state==null || __result==null) return;
            try {
                foreach (var pending in __state.Pending) {
                    var record=pending.Record;
                    // Nested damage to this target makes the outer outcome ambiguous;
                    // nested calls never acquire their own melee attribution.
                    if (pending.Target.Exhausted || pending.Target.Sequence!=pending.Sequence || !IsReady
                        || dinfo.Instigator!=record.Attacker || record.Game!=Current.Game || !ProtoBoundary.IsLoaded(record.Map) || Find.TickManager==null) continue;
                    record.Record(__result.totalDamageDealt,pending.WasDead,pending.WasDowned,
                        record.Target.Dead,record.Target.Downed,Find.TickManager.TicksGame);
                }
            } catch { /* Unreadable outcome supplies no completion evidence. */ }
        }
    }
}
