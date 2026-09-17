#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Only the audited native direct bullet call sites create lineage. Notifications,
    // explosions and other projectile implementations do not inherit this evidence.
    internal static partial class NativeRangedCausality
    {
        private const string Owner="rimgovernor.ranged-causality";
        private const int RecordLimit=4096, FlightLimit=16384;
        private sealed class Tracked
        {
            internal NativeCombatDamageRecord Evidence=null!;
            internal NativeControlIdentity Identity=null!;
            internal Func<bool> LaunchGuard=null!, ImpactGuard=null!;
            internal bool TrackingLost;
        }
        private sealed class Flight
        {
            internal Tracked Record=null!;
            internal Projectile Projectile=null!;
            internal int ProjectileId;
            internal ThingDef Definition=null!;
        }
        private sealed class GameState
        {
            internal readonly List<Tracked> Records=new List<Tracked>();
            internal readonly Dictionary<Projectile,Flight> Flights=new Dictionary<Projectile,Flight>();
            internal readonly Dictionary<Explosion,Blast> Explosions=new Dictionary<Explosion,Blast>();
        }
        private sealed class DamageFrame { internal int PreviousDepth; }
        private sealed class PendingImpact
        {
            internal Flight Flight=null!;
            internal long Sequence;
            internal bool WasDead, WasDowned;
        }
        private static readonly ConditionalWeakTable<Game,GameState> Games=new ConditionalWeakTable<Game,GameState>();
        [ThreadStatic] private static int damageDepth;
        [ThreadStatic] private static long damageSequence;
        [ThreadStatic] private static bool exhausted;
        private static bool launchShape, impactShape;
        private static readonly MethodInfo LaunchTarget=AccessTools.DeclaredMethod(typeof(Verb_LaunchProjectile),"TryCastShot");
        private static readonly MethodInfo ImpactTarget=AccessTools.DeclaredMethod(typeof(Bullet),"Impact",new[]{typeof(Thing),typeof(bool)});
        private static readonly MethodInfo NativeLaunch=AccessTools.DeclaredMethod(typeof(Projectile),"Launch",new[]{typeof(Thing),typeof(Vector3),typeof(LocalTargetInfo),typeof(LocalTargetInfo),typeof(ProjectileHitFlags),typeof(bool),typeof(Thing),typeof(ThingDef)});
        private static readonly MethodInfo DamageTarget=AccessTools.DeclaredMethod(typeof(Thing),nameof(Thing.TakeDamage),new[]{typeof(DamageInfo)});
        private static readonly MethodInfo LaunchTranspiler=AccessTools.Method(typeof(NativeRangedCausality),nameof(RewriteLaunch));
        private static readonly MethodInfo ImpactTranspiler=AccessTools.Method(typeof(NativeRangedCausality),nameof(RewriteImpact));
        private static readonly MethodInfo DamagePrefix=AccessTools.Method(typeof(NativeRangedCausality),nameof(BeforeAnyDamage));
        private static readonly MethodInfo DamageFinalizer=AccessTools.Method(typeof(NativeRangedCausality),nameof(EndAnyDamage));
        private static readonly MethodInfo LaunchWrapper=AccessTools.Method(typeof(NativeRangedCausality),nameof(LaunchBullet));
        private static readonly MethodInfo ImpactWrapper=AccessTools.Method(typeof(NativeRangedCausality),nameof(ApplyBulletDamage));
        private static bool Same(MethodInfo a,MethodInfo b)=>NativeConstructionHookSet.SameMethod(a,b);
        private static int Count(IEnumerable<Patch>? patches,MethodInfo method)=>patches?.Count(p=>p.owner==Owner && Same(p.PatchMethod,method))??0;
        internal static bool IsReady
        {
            get { try {
                var launch=Harmony.GetPatchInfo(LaunchTarget);var impact=Harmony.GetPatchInfo(ImpactTarget);var damage=Harmony.GetPatchInfo(DamageTarget);
                return launchShape && impactShape && Count(launch?.Transpilers,LaunchTranspiler)==1 && Count(impact?.Transpilers,ImpactTranspiler)==1
                    && Count(damage?.Prefixes,DamagePrefix)==1 && Count(damage?.Finalizers,DamageFinalizer)==1
                    && DamagePrefixArgumentsSafe(damage?.Prefixes);
            } catch { return false; } }
        }
        private static bool DamagePrefixArgumentsSafe(IEnumerable<Patch>? prefixes)
        {
            // Wrappers retain the original struct. Reject hooks able to replace
            // effective damage identity instead of guessing their execution order.
            return prefixes!=null && prefixes.All(p=>p.PatchMethod.GetParameters().All(parameter=>
                parameter.Name!="__args" && parameter.ParameterType!=typeof(object[])
                && parameter.ParameterType!=typeof(object[]).MakeByRefType()
                && parameter.ParameterType!=typeof(DamageInfo).MakeByRefType()
                && parameter.ParameterType!=typeof(Thing).MakeByRefType()));
        }
        private static void MarkUnhealthyFlight(Projectile projectile)
        {
            try {
                if (!IsReady && Current.Game!=null && Games.TryGetValue(Current.Game,out var state)
                    && state.Flights.TryGetValue(projectile,out var flight)) flight.Record.TrackingLost=true;
            } catch { /* Unreadable hook state cannot certify an impact. */ }
        }
        internal static void Initialize()
        {
            try {
                var harmony=new Harmony(Owner);
                if (Count(Harmony.GetPatchInfo(LaunchTarget)?.Transpilers,LaunchTranspiler)==0) harmony.Patch(LaunchTarget,transpiler:new HarmonyMethod(LaunchTranspiler));
                if (Count(Harmony.GetPatchInfo(ImpactTarget)?.Transpilers,ImpactTranspiler)==0) harmony.Patch(ImpactTarget,transpiler:new HarmonyMethod(ImpactTranspiler));
                var damage=Harmony.GetPatchInfo(DamageTarget);
                var prefix=Count(damage?.Prefixes,DamagePrefix)==0?new HarmonyMethod(DamagePrefix):null;
                var finalizer=Count(damage?.Finalizers,DamageFinalizer)==0?new HarmonyMethod(DamageFinalizer):null;
                if (prefix!=null || finalizer!=null) harmony.Patch(DamageTarget,prefix,finalizer:finalizer);
            } catch { /* Admission remains unavailable without every verified hook. */ }
            InitializeExplosives();
        }
        private static IEnumerable<CodeInstruction> RewriteLaunch(IEnumerable<CodeInstruction> instructions)
        { return Rewrite(instructions,NativeLaunch,LaunchWrapper,5,out launchShape); }
        private static IEnumerable<CodeInstruction> RewriteImpact(IEnumerable<CodeInstruction> instructions)
        { return Rewrite(instructions,DamageTarget,ImpactWrapper,2,out impactShape); }
        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,MethodInfo target,MethodInfo wrapper,int expected,out bool valid)
        {
            var code=instructions.Select(i=>new CodeInstruction(i)).ToList();
            var calls=code.Where(i=>(i.opcode==OpCodes.Callvirt || i.opcode==OpCodes.Call) && i.operand is MethodInfo m && Same(m,target)).ToList();
            valid=calls.Count==expected && calls.All(i=>i.blocks.Count==0);
            if (!valid) return code;
            var result=new List<CodeInstruction>();
            foreach (var instruction in code) {
                if (calls.Contains(instruction)) {
                    // Preserve branches to the call: the wrapper's last argument is this.
                    var instance=new CodeInstruction(OpCodes.Ldarg_0);
                    instance.labels.AddRange(instruction.labels);instruction.labels.Clear();
                    result.Add(instance);
                    if (Same(wrapper,ImpactWrapper)) result.Add(new CodeInstruction(OpCodes.Ldarg_2));
                    instruction.opcode=OpCodes.Call;instruction.operand=wrapper;
                }
                result.Add(instruction);
            }
            return result;
        }
        internal static bool Supports(Verb verb,Pawn attacker,Pawn target)
        {
            try {
                if (!IsReady || verb==null || attacker==null || target==null || verb.CasterPawn!=attacker
                    || (verb.GetType()!=typeof(Verb_Shoot) && verb.GetType()!=typeof(Verb_LaunchProjectile))
                    || !verb.verbProps.ai_IsWeapon || verb.verbProps.IsMeleeAttack || verb.EquipmentSource==null) return false;
                var projectile=((Verb_LaunchProjectile)verb).Projectile;
                return projectile!=null && projectile.projectile!=null && !projectile.projectile.flyOverhead
                    && ((projectile.thingClass==typeof(Bullet) && projectile.projectile.explosionRadius==0) || SupportedExplosive(projectile));
            } catch { return false; }
        }
        internal static NativeCombatDamageRecord Track(Game game,Pawn attacker,Pawn target,Job job,Func<bool> launchGuard,Func<bool> impactGuard)
        {
            if (!UnityData.IsInMainThread || !IsReady || game!=Current.Game || !attacker.Spawned || !target.Spawned
                || attacker.Map!=target.Map || !ProtoBoundary.IsLoaded(target.Map) || job==null || job.def!=JobDefOf.AttackStatic
                || job.targetA.Thing!=target || !Supports(job.verbToUse,attacker,target) || launchGuard==null || impactGuard==null
                || !NativeControlAuthority.TryGetForGame(game,out var authority) || authority==null || authority.Status().Identity==null)
                throw new InvalidOperationException("Exact native projectile tracking prerequisites are unavailable.");
            var identity=authority.Status().Identity!;
            var state=Games.GetOrCreateValue(game);
            if (state.Records.Count>=RecordLimit || state.Flights.Count>=FlightLimit || state.Records.Any(r=>ReferenceEquals(r.Evidence.Job,job) && r.Evidence.JobId==job.loadID))
                throw new InvalidOperationException("Projectile tracking capacity or unique job identity is unavailable.");
            var evidence=new NativeCombatDamageRecord(game,attacker,target,job,impactGuard);
            state.Records.Add(new Tracked {Evidence=evidence,Identity=identity,LaunchGuard=launchGuard,ImpactGuard=impactGuard});
            return evidence;
        }
        private static bool CurrentIdentity(Tracked record)
        {
            return Current.Game==record.Identity.Game && ProtoBoundary.IsLoaded(record.Identity.Map)
                && NativeControlAuthority.TryGetForGame(Current.Game,out var authority) && authority!=null
                && authority.Status().Identity is NativeControlIdentity identity && NativePawnFacts.SameIdentity(record.Identity,identity);
        }
        internal static bool HasTrackingLoss(NativeCombatDamageRecord evidence)
        {
            try {
                if (!Games.TryGetValue(evidence.Game,out var state)) return true;
                var record=state.Records.FirstOrDefault(r=>ReferenceEquals(r.Evidence,evidence));
                return record==null || record.TrackingLost;
            } catch { return true; }
        }
        private static Tracked? PrepareLaunch(Projectile projectile,Thing launcher,LocalTargetInfo intendedTarget,Thing equipment,Verb_LaunchProjectile verb,out bool admissible)
        {
            admissible=false;
            Tracked? record=null;
            try {
                if (!UnityData.IsInMainThread || Current.Game==null || !Games.TryGetValue(Current.Game,out var state)) return null;
                var attacker=verb.CasterPawn;
                record=state.Records.FirstOrDefault(r=>r.Evidence.Attacker==attacker && r.Evidence.Job==attacker.CurJob && r.Evidence.JobId==r.Evidence.Job.loadID);
                if (record==null) return null;
                var e=record.Evidence;
                if (!IsReady || exhausted || damageDepth!=0 || (projectile.GetType()!=typeof(Bullet) && projectile.GetType()!=typeof(Projectile_Explosive))
                    || launcher!=attacker || intendedTarget.Thing!=e.Target || !Supports(verb,attacker,e.Target)
                    || equipment!=verb.EquipmentSource || projectile.def!=verb.Projectile || projectile.Map!=record.Identity.Map
                    || state.Flights.Count>=FlightLimit || state.Flights.ContainsKey(projectile)
                    || e.JobId!=e.Job.loadID || e.Job.def!=JobDefOf.AttackStatic || e.Job.targetA.Thing!=e.Target || e.Job.verbToUse!=verb
                    || !CurrentIdentity(record) || !record.LaunchGuard()) record.TrackingLost=true;
                else admissible=true;
            } catch { if (record!=null) record.TrackingLost=true; }
            return record;
        }
        private static void LaunchBullet(Projectile projectile,Thing launcher,Vector3 origin,LocalTargetInfo usedTarget,LocalTargetInfo intendedTarget,
            ProjectileHitFlags hitFlags,bool preventFriendlyFire,Thing equipment,ThingDef targetCoverDef,Verb_LaunchProjectile verb)
        {
            var record=PrepareLaunch(projectile,launcher,intendedTarget,equipment,verb,out var admissible);
            // Forward the exact native call even if observation is unavailable.
            try { projectile.Launch(launcher,origin,usedTarget,intendedTarget,hitFlags,preventFriendlyFire,equipment,targetCoverDef); }
            catch { if (record!=null) record.TrackingLost=true; throw; }
            if (record==null || !admissible) return;
            try {
                if (!IsReady || !CurrentIdentity(record) || projectile.Launcher!=launcher
                    || projectile.intendedTarget!=intendedTarget || projectile.usedTarget!=usedTarget || projectile.Map!=record.Identity.Map
                    || projectile.thingIDNumber<0 || projectile.def!=verb.Projectile || !projectile.Spawned) {
                    record.TrackingLost=true;return;
                }
                var state=Games.GetOrCreateValue(record.Identity.Game);
                if (state.Flights.Count>=FlightLimit || state.Flights.ContainsKey(projectile)) {record.TrackingLost=true;return;}
                state.Flights.Add(projectile,new Flight {Record=record,Projectile=projectile,ProjectileId=projectile.thingIDNumber,Definition=projectile.def});
            } catch { record.TrackingLost=true; }
        }
        private static void BeforeAnyDamage(out DamageFrame? __state)
        {
            __state=null;
            try {
                // Even unhealthy calls invalidate enclosing damage evidence. Enter
                // nesting only when this invocation has exactly one cleanup hook.
                damageSequence=checked(damageSequence+1);
                if (Count(Harmony.GetPatchInfo(DamageTarget)?.Finalizers,DamageFinalizer)!=1) return;
                __state=new DamageFrame {PreviousDepth=damageDepth};
                damageDepth=checked(damageDepth+1);
            } catch { exhausted=true; }
        }
        private static Exception? EndAnyDamage(Exception? __exception,DamageFrame? __state)
        {
            try { if (__state!=null) damageDepth=__state.PreviousDepth; }
            catch { exhausted=true; }
            return __exception;
        }
        private static PendingImpact? PrepareImpact(Bullet projectile,Thing victim,DamageInfo damage,bool blockedByShield)
        {
            try {
                if (blockedByShield || !UnityData.IsInMainThread || !IsReady || exhausted || damageDepth!=0 || Current.Game==null
                    || !Games.TryGetValue(Current.Game,out var state) || !state.Flights.TryGetValue(projectile,out var flight)) return null;
                var record=flight.Record;var e=record.Evidence;
                if (projectile.GetType()!=typeof(Bullet) || projectile.thingIDNumber!=flight.ProjectileId || projectile.def!=flight.Definition
                    || projectile.Launcher!=e.Attacker || projectile.intendedTarget.Thing!=e.Target || victim!=e.Target || damage.Instigator!=e.Attacker
                    || !CurrentIdentity(record) || !record.ImpactGuard() || e.Target.Dead || e.CausedDeath || damageSequence==long.MaxValue) return null;
                return new PendingImpact {Flight=flight,Sequence=damageSequence+1,WasDead=e.Target.Dead,WasDowned=e.Target.Downed};
            } catch { return null; }
        }
        private static DamageWorker.DamageResult ApplyBulletDamage(Thing victim,DamageInfo damage,Bullet projectile,bool blockedByShield)
        {
            MarkUnhealthyFlight(projectile);
            var pending=PrepareImpact(projectile,victim,damage,blockedByShield);
            // The wrapper is installed only at Bullet.Impact's two native damage calls.
            var result=victim.TakeDamage(damage);
            MarkUnhealthyFlight(projectile);
            try {
                if (pending!=null && result!=null && !exhausted && damageDepth==0 && damageSequence==pending.Sequence && IsReady
                    && CurrentIdentity(pending.Flight.Record) && pending.Flight.Record.ImpactGuard() && Find.TickManager!=null) {
                    var evidence=pending.Flight.Record.Evidence;
                    evidence.Record(result.totalDamageDealt,pending.WasDead,pending.WasDowned,evidence.Target.Dead,evidence.Target.Downed,Find.TickManager.TicksGame);
                }
            } catch { /* Keep the native result unchanged if outcome evidence is unreadable. */ }
            return result!;
        }
    }
}
