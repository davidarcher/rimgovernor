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
        private static readonly ConditionalWeakTable<Game,GameState> Games=new ConditionalWeakTable<Game,GameState>();
        private static readonly MethodInfo Target=AccessTools.Method(typeof(Thing),nameof(Thing.TakeDamage),new[]{typeof(DamageInfo)});
        private static readonly MethodInfo Prefix=AccessTools.Method(typeof(NativeCombatCausality),nameof(BeforeDamage));
        private static readonly MethodInfo Postfix=AccessTools.Method(typeof(NativeCombatCausality),nameof(AfterDamage));
        internal static void Initialize()
        {
            if (IsReady) return;
            if (Target==null || Prefix==null || Postfix==null) return;
            try { new Harmony(Owner).Patch(Target,new HarmonyMethod(Prefix),new HarmonyMethod(Postfix)); }
            catch { /* Missing live hooks keep admission unavailable. */ }
        }
        internal static bool IsReady
        {
            get {
                try {
                    var hooks=Target==null?null:Harmony.GetPatchInfo(Target);
                    return hooks!=null && hooks.Prefixes.Any(p=>p.owner==Owner && NativeConstructionHookSet.SameMethod(p.PatchMethod,Prefix))
                        && hooks.Postfixes.Any(p=>p.owner==Owner && NativeConstructionHookSet.SameMethod(p.PatchMethod,Postfix));
                } catch { return false; }
            }
        }
        internal static NativeCombatDamageRecord Track(Game game,Pawn attacker,Pawn target,Job job,Func<bool> guard)
        {
            if (!UnityData.IsInMainThread || !IsReady || game!=Current.Game || attacker==null || target==null
                || !attacker.Spawned || !target.Spawned || attacker.Map!=target.Map || target.Map!=Find.CurrentMap
                || job==null || job.def!=JobDefOf.AttackMelee || job.targetA.Thing!=target || guard==null)
                throw new InvalidOperationException("Exact live melee damage tracking prerequisites are unavailable.");
            var state=Games.GetOrCreateValue(game);
            if (state.Count>=4096) throw new InvalidOperationException("Combat damage tracking capacity exhausted.");
            if (!state.Targets.TryGetValue(target,out var tracked)) { tracked=new TargetState(); state.Targets.Add(target,tracked); }
            var record=new NativeCombatDamageRecord(game,attacker,target,job,guard);
            tracked.Records.Add(record); state.Count++;
            return record;
        }
        private static void BeforeDamage(Thing __instance,DamageInfo dinfo,out List<PendingDamage>? __state)
        {
            __state=null;
            try {
                if (!UnityData.IsInMainThread || Current.Game==null || !(__instance is Pawn victim)
                    || !Games.TryGetValue(Current.Game,out var game) || !game.Targets.TryGetValue(victim,out var target)) return;
                if (target.Exhausted || target.Sequence==long.MaxValue) { target.Exhausted=true; return; }
                var sequence=++target.Sequence;
                if (!IsReady || victim.Dead) return;
                foreach (var record in target.Records) {
                    if (record.CausedDeath || record.Game!=Current.Game || record.Map!=Find.CurrentMap
                        || record.Map!=victim.Map || dinfo.Instigator!=record.Attacker || record.Attacker.CurJob!=record.Job
                        || record.Job.loadID!=record.JobId || record.Job.def!=JobDefOf.AttackMelee || record.Job.targetA.Thing!=victim) continue;
                    if (!record.Guard()) continue;
                    if (__state==null) __state=new List<PendingDamage>();
                    __state.Add(new PendingDamage {Record=record,Target=target,Sequence=sequence,WasDead=victim.Dead,WasDowned=victim.Downed});
                }
            } catch { __state=null; }
        }
        private static void AfterDamage(DamageInfo dinfo,DamageWorker.DamageResult __result,List<PendingDamage>? __state)
        {
            if (__state==null || __result==null) return;
            try {
                foreach (var pending in __state) {
                    var record=pending.Record;
                    // Nested damage invalidates the outer call's attribution; the
                    // nested call can certify its own exact instigator independently.
                    if (pending.Target.Exhausted || pending.Target.Sequence!=pending.Sequence || !IsReady
                        || dinfo.Instigator!=record.Attacker || record.Game!=Current.Game || record.Map!=Find.CurrentMap || Find.TickManager==null) continue;
                    record.Record(__result.totalDamageDealt,pending.WasDead,pending.WasDowned,
                        record.Target.Dead,record.Target.Downed,Find.TickManager.TicksGame);
                }
            } catch { /* Unreadable outcome supplies no completion evidence. */ }
        }
    }
}
