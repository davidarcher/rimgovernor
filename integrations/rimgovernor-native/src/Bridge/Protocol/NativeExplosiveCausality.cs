#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static partial class NativeRangedCausality
    {
        private sealed class Blast
        {
            internal Flight Flight=null!;
            internal Explosion Explosion=null!;
            internal int ExplosionId;
            internal bool Started;
        }
        private sealed class ExplosionTicket
        {
            internal Flight Flight=null!;
            internal int ExpectedDepth;
            internal bool Bound, Invalid;
        }
        private sealed class ExplosionFrame { internal int PreviousDepth; }
        [ThreadStatic] private static ExplosionTicket? explosionTicket;
        [ThreadStatic] private static int explosionDepth;
        [ThreadStatic] private static bool explosionDepthLost;
        private static bool explodeShape, startShape, explosionDamageShape;
        private static readonly MethodInfo ExplodeTarget=AccessTools.DeclaredMethod(typeof(Projectile_Explosive),"Explode",Type.EmptyTypes);
        private static readonly MethodInfo StartExplosionTarget=AccessTools.DeclaredMethod(typeof(Explosion),"StartExplosion",new[]{typeof(SoundDef),typeof(List<Thing>)});
        private static readonly MethodInfo ExplosionDamageTarget=AccessTools.DeclaredMethod(typeof(DamageWorker),"ExplosionDamageThing",new[]{typeof(Explosion),typeof(Thing),typeof(List<Thing>),typeof(List<Thing>),typeof(IntVec3)});
        private static readonly MethodInfo ExplosionCellTarget=AccessTools.DeclaredMethod(typeof(Explosion),"AffectCell",new[]{typeof(IntVec3)});
        private static readonly MethodInfo DoExplosionTarget=AccessTools.DeclaredMethod(typeof(GenExplosion),"DoExplosion",new[]{typeof(IntVec3),typeof(Map),typeof(float),typeof(DamageDef),typeof(Thing),typeof(int),typeof(float),typeof(SoundDef),typeof(ThingDef),typeof(ThingDef),typeof(Thing),typeof(ThingDef),typeof(float),typeof(int),typeof(GasType?),typeof(float?),typeof(int),typeof(bool),typeof(ThingDef),typeof(float),typeof(int),typeof(float),typeof(bool),typeof(float?),typeof(List<Thing>),typeof(FloatRange?),typeof(bool),typeof(float),typeof(float),typeof(bool),typeof(ThingDef),typeof(float),typeof(SimpleCurve),typeof(List<IntVec3>),typeof(ThingDef),typeof(ThingDef)});
        private static readonly MethodInfo ExplodeTranspiler=AccessTools.Method(typeof(NativeRangedCausality),nameof(RewriteExplode));
        private static readonly MethodInfo StartTranspiler=AccessTools.Method(typeof(NativeRangedCausality),nameof(RewriteExplosionStart));
        private static readonly MethodInfo ExplosionDamageTranspiler=AccessTools.Method(typeof(NativeRangedCausality),nameof(RewriteExplosionDamage));
        private static readonly MethodInfo DoExplosionPrefix=AccessTools.Method(typeof(NativeRangedCausality),nameof(BeginDoExplosion));
        private static readonly MethodInfo DoExplosionFinalizer=AccessTools.Method(typeof(NativeRangedCausality),nameof(EndDoExplosion));
        private static readonly MethodInfo CellFinalizer=AccessTools.Method(typeof(NativeRangedCausality),nameof(EndExplosionCell));
        private static readonly MethodInfo ExplodeWrapper=AccessTools.Method(typeof(NativeRangedCausality),nameof(DoExplosionFromProjectile));
        private static readonly MethodInfo StartWrapper=AccessTools.Method(typeof(NativeRangedCausality),nameof(StartTrackedExplosion));
        private static readonly MethodInfo ExplosionDamageWrapper=AccessTools.Method(typeof(NativeRangedCausality),nameof(ApplyExplosionDamage));
        internal static bool ExplosiveIsReady
        {
            get { try {
                var factory=Harmony.GetPatchInfo(DoExplosionTarget);
                return IsReady && explodeShape && startShape && explosionDamageShape
                    && Count(Harmony.GetPatchInfo(ExplodeTarget)?.Transpilers,ExplodeTranspiler)==1
                    && Count(factory?.Transpilers,StartTranspiler)==1
                    // An earlier foreign prefix could enter the factory before our
                    // nesting callback and consume the outer ticket. No ordering guess.
                    && factory!=null && factory.Prefixes.Count==1
                    && Count(factory.Prefixes,DoExplosionPrefix)==1 && Count(factory.Finalizers,DoExplosionFinalizer)==1
                    && Count(Harmony.GetPatchInfo(ExplosionDamageTarget)?.Transpilers,ExplosionDamageTranspiler)==1
                    && Count(Harmony.GetPatchInfo(ExplosionCellTarget)?.Finalizers,CellFinalizer)==1;
            } catch { return false; } }
        }
        private static void InitializeExplosives()
        {
            try {
                var harmony=new Harmony(Owner);
                if (Count(Harmony.GetPatchInfo(ExplodeTarget)?.Transpilers,ExplodeTranspiler)==0) harmony.Patch(ExplodeTarget,transpiler:new HarmonyMethod(ExplodeTranspiler));
                var factory=Harmony.GetPatchInfo(DoExplosionTarget);
                var prefix=Count(factory?.Prefixes,DoExplosionPrefix)==0?new HarmonyMethod(DoExplosionPrefix):null;
                var finalizer=Count(factory?.Finalizers,DoExplosionFinalizer)==0?new HarmonyMethod(DoExplosionFinalizer):null;
                var transpiler=Count(factory?.Transpilers,StartTranspiler)==0?new HarmonyMethod(StartTranspiler):null;
                if (prefix!=null || finalizer!=null || transpiler!=null) harmony.Patch(DoExplosionTarget,prefix,transpiler:transpiler,finalizer:finalizer);
                if (Count(Harmony.GetPatchInfo(ExplosionDamageTarget)?.Transpilers,ExplosionDamageTranspiler)==0) harmony.Patch(ExplosionDamageTarget,transpiler:new HarmonyMethod(ExplosionDamageTranspiler));
                if (Count(Harmony.GetPatchInfo(ExplosionCellTarget)?.Finalizers,CellFinalizer)==0) harmony.Patch(ExplosionCellTarget,finalizer:new HarmonyMethod(CellFinalizer));
            } catch { /* Explosive readiness is independent of accepted bullet support. */ }
        }
        private static IEnumerable<CodeInstruction> RewriteExplode(IEnumerable<CodeInstruction> instructions)
            => RewriteExplosionCall(instructions,DoExplosionTarget,ExplodeWrapper,new[]{OpCodes.Ldarg_0},out explodeShape);
        private static IEnumerable<CodeInstruction> RewriteExplosionStart(IEnumerable<CodeInstruction> instructions)
            => RewriteExplosionCall(instructions,StartExplosionTarget,StartWrapper,Array.Empty<OpCode>(),out startShape);
        private static IEnumerable<CodeInstruction> RewriteExplosionDamage(IEnumerable<CodeInstruction> instructions)
            => RewriteExplosionCall(instructions,DamageTarget,ExplosionDamageWrapper,new[]{OpCodes.Ldarg_1,OpCodes.Ldarg_0},out explosionDamageShape);
        private static IEnumerable<CodeInstruction> RewriteExplosionCall(IEnumerable<CodeInstruction> instructions,MethodInfo target,MethodInfo wrapper,OpCode[] arguments,out bool valid)
        {
            var code=instructions.Select(i=>new CodeInstruction(i)).ToList();
            var calls=code.Where(i=>(i.opcode==OpCodes.Call || i.opcode==OpCodes.Callvirt) && i.operand is MethodInfo method && Same(method,target)).ToList();
            valid=calls.Count==1 && calls[0].blocks.Count==0;
            if (!valid) return code;
            var result=new List<CodeInstruction>();
            foreach (var instruction in code) {
                if (ReferenceEquals(instruction,calls[0])) {
                    foreach (var argument in arguments) {
                        var load=new CodeInstruction(argument);load.labels.AddRange(instruction.labels);instruction.labels.Clear();result.Add(load);
                    }
                    instruction.opcode=OpCodes.Call;instruction.operand=wrapper;
                }
                result.Add(instruction);
            }
            return result;
        }
        private static bool SupportedExplosive(ThingDef definition)
        {
            try {
                var p=definition.projectile;
                return ExplosiveIsReady && definition.thingClass==typeof(Projectile_Explosive) && p!=null && !p.flyOverhead
                    && p.explosionRadius>0 && !float.IsInfinity(p.explosionRadius) && p.explosionDelay>=0
                    && p.damageDef!=null && p.damageDef.Worker.GetType()==typeof(DamageWorker_AddInjury)
                    && p.preExplosionSpawnThingDef==null && p.postExplosionSpawnThingDef==null && p.postExplosionSpawnThingDefWater==null
                    && p.preExplosionSpawnSingleThingDef==null && p.postExplosionSpawnSingleThingDef==null && p.postExplosionGasType==null
                    && p.explosionChanceToStartFire==0 && p.filth==null && !p.explosionSpawnsSingleFilth && p.extraDamages.NullOrEmpty();
            } catch { return false; }
        }
        private static bool SafeExplosion(Explosion explosion)
        {
            return explosion.GetType()==typeof(Explosion) && explosion.damType!=null && explosion.damType.Worker.GetType()==typeof(DamageWorker_AddInjury)
                && explosion.preExplosionSpawnThingDef==null && explosion.postExplosionSpawnThingDef==null && explosion.postExplosionSpawnThingDefWater==null
                && explosion.preExplosionSpawnSingleThingDef==null && explosion.postExplosionSpawnSingleThingDef==null && explosion.postExplosionGasType==null
                && explosion.chanceToStartFire==0;
        }
        private static Flight? ExplosiveFlight(Projectile_Explosive projectile)
        {
            if (Current.Game!=null && Games.TryGetValue(Current.Game,out var state) && state.Flights.TryGetValue(projectile,out var flight)) return flight;
            return null;
        }
        private static bool ValidDetonation(Flight flight)
        {
            var projectile=flight.Projectile;
            return UnityData.IsInMainThread && ExplosiveIsReady && !explosionDepthLost && !exhausted && damageDepth==0
                && projectile.GetType()==typeof(Projectile_Explosive) && projectile.thingIDNumber==flight.ProjectileId && projectile.def==flight.Definition
                && SupportedExplosive(flight.Definition) && projectile.damageDefOverride==null && projectile.extraDamages.NullOrEmpty()
                && projectile.Launcher==flight.Record.Evidence.Attacker && projectile.intendedTarget.Thing==flight.Record.Evidence.Target
                && CurrentIdentity(flight.Record) && flight.Record.ImpactGuard();
        }
        private static void BeginDoExplosion(out ExplosionFrame? __state)
        {
            __state=null;
            try {
                if (Count(Harmony.GetPatchInfo(DoExplosionTarget)?.Finalizers,DoExplosionFinalizer)!=1) {
                    if (explosionTicket!=null) {explosionTicket.Invalid=true;explosionTicket.Flight.Record.TrackingLost=true;}
                    return;
                }
                __state=new ExplosionFrame {PreviousDepth=explosionDepth};explosionDepth=checked(explosionDepth+1);
            } catch { explosionDepthLost=true; }
        }
        private static Exception? EndDoExplosion(Exception? __exception,ExplosionFrame? __state)
        {
            try {
                if (__exception!=null && explosionTicket!=null) explosionTicket.Flight.Record.TrackingLost=true;
                if (__state!=null) explosionDepth=__state.PreviousDepth;
            } catch { explosionDepthLost=true; }
            return __exception;
        }
        private static Exception? EndExplosionCell(Explosion __instance,Exception? __exception)
        {
            try {
                if (__exception!=null && Current.Game!=null && Games.TryGetValue(Current.Game,out var state)
                    && state.Explosions.TryGetValue(__instance,out var blast)) blast.Flight.Record.TrackingLost=true;
            } catch { /* Native propagation retains its original exception. */ }
            return __exception;
        }
        private static void DoExplosionFromProjectile(IntVec3 center, Map map, float radius, DamageDef damType, Thing instigator, int damAmount, float armorPenetration, SoundDef explosionSound, ThingDef weapon, ThingDef projectile, Thing intendedTarget, ThingDef postExplosionSpawnThingDef, float postExplosionSpawnChance, int postExplosionSpawnThingCount, GasType? postExplosionGasType, float? postExplosionGasRadiusOverride, int postExplosionGasAmount, bool applyDamageToExplosionCellsNeighbors, ThingDef preExplosionSpawnThingDef, float preExplosionSpawnChance, int preExplosionSpawnThingCount, float chanceToStartFire, bool damageFalloff, float? direction, List<Thing> ignoredThings, FloatRange? affectedAngle, bool doVisualEffects, float propagationSpeed, float excludeRadius, bool doSoundEffects, ThingDef postExplosionSpawnThingDefWater, float screenShakeFactor, SimpleCurve flammabilityChanceCurve, List<IntVec3> overrideCells, ThingDef postExplosionSpawnSingleThingDef, ThingDef preExplosionSpawnSingleThingDef, Projectile_Explosive source)
        {
            var previous=explosionTicket;
            Flight? flight=null;
            ExplosionTicket? ticket=null;
            try {
                flight=ExplosiveFlight(source);
                if (flight!=null) {
                    if (ValidDetonation(flight) && map==flight.Record.Identity.Map && instigator==flight.Record.Evidence.Attacker
                        && intendedTarget==flight.Record.Evidence.Target && projectile==flight.Definition && damType==flight.Definition.projectile.damageDef
                        && radius==flight.Definition.projectile.explosionRadius) ticket=new ExplosionTicket {Flight=flight,ExpectedDepth=checked(explosionDepth+1)};
                    else flight.Record.TrackingLost=true;
                }
            } catch { if (flight!=null) flight.Record.TrackingLost=true; }
            explosionTicket=ticket;
            try {
                GenExplosion.DoExplosion(center, map, radius, damType, instigator, damAmount, armorPenetration, explosionSound, weapon, projectile, intendedTarget, postExplosionSpawnThingDef, postExplosionSpawnChance, postExplosionSpawnThingCount, postExplosionGasType, postExplosionGasRadiusOverride, postExplosionGasAmount, applyDamageToExplosionCellsNeighbors, preExplosionSpawnThingDef, preExplosionSpawnChance, preExplosionSpawnThingCount, chanceToStartFire, damageFalloff, direction, ignoredThings, affectedAngle, doVisualEffects, propagationSpeed, excludeRadius, doSoundEffects, postExplosionSpawnThingDefWater, screenShakeFactor, flammabilityChanceCurve, overrideCells, postExplosionSpawnSingleThingDef, preExplosionSpawnSingleThingDef);
                if (ticket!=null && !ticket.Bound) ticket.Flight.Record.TrackingLost=true;
            } catch { if (flight!=null) flight.Record.TrackingLost=true; throw; }
            finally { explosionTicket=previous; }
        }

        private static void StartTrackedExplosion(Explosion explosion,SoundDef sound,List<Thing> ignored)
        {
            Blast? blast=null;
            try {
                var ticket=explosionTicket;
                if (ticket!=null && !ticket.Invalid && !ticket.Bound && ticket.ExpectedDepth==explosionDepth && ValidDetonation(ticket.Flight)) {
                    ticket.Bound=true;
                    var flight=ticket.Flight;var e=flight.Record.Evidence;
                    var state=Games.GetOrCreateValue(flight.Record.Identity.Game);
                    if (state.Explosions.Count>=FlightLimit || state.Explosions.ContainsKey(explosion) || explosion.GetType()!=typeof(Explosion)
                        || !explosion.Spawned || explosion.Map!=flight.Record.Identity.Map || explosion.thingIDNumber<0
                        || explosion.instigator!=e.Attacker || explosion.intendedTarget!=e.Target || explosion.projectile!=flight.Definition
                        || explosion.damType!=flight.Definition.projectile.damageDef || explosion.radius!=flight.Definition.projectile.explosionRadius || !SafeExplosion(explosion)) flight.Record.TrackingLost=true;
                    else {
                        blast=new Blast {Flight=flight,Explosion=explosion,ExplosionId=explosion.thingIDNumber};
                        state.Explosions.Add(explosion,blast);
                    }
                }
            } catch { if (explosionTicket!=null) explosionTicket.Flight.Record.TrackingLost=true; }
            try { explosion.StartExplosion(sound,ignored); }
            catch { if (blast!=null) blast.Flight.Record.TrackingLost=true; throw; }
            try {
                if (blast!=null) {
                    if (ExplosiveIsReady && explosion.Spawned && explosion.Map==blast.Flight.Record.Identity.Map && CurrentIdentity(blast.Flight.Record)) blast.Started=true;
                    else blast.Flight.Record.TrackingLost=true;
                }
            } catch { if (blast!=null) blast.Flight.Record.TrackingLost=true; }
        }
        private static void MarkUnhealthyExplosion(Explosion explosion)
        {
            try {
                if (!ExplosiveIsReady && Current.Game!=null && Games.TryGetValue(Current.Game,out var state)
                    && state.Explosions.TryGetValue(explosion,out var blast)) blast.Flight.Record.TrackingLost=true;
            } catch { /* Unreadable hook state cannot certify an impact. */ }
        }
        private static PendingImpact? PrepareExplosionImpact(Explosion explosion,DamageWorker worker,Thing victim,DamageInfo damage)
        {
            try {
                if (!UnityData.IsInMainThread || !ExplosiveIsReady || exhausted || damageDepth!=0 || Current.Game==null
                    || !Games.TryGetValue(Current.Game,out var state) || !state.Explosions.TryGetValue(explosion,out var blast)) return null;
                var flight=blast.Flight;var e=flight.Record.Evidence;
                if (!blast.Started || !SafeExplosion(explosion) || explosion.GetType()!=typeof(Explosion) || explosion.thingIDNumber!=blast.ExplosionId
                    || explosion.Map!=flight.Record.Identity.Map || explosion.instigator!=e.Attacker || explosion.intendedTarget!=e.Target
                    || explosion.projectile!=flight.Definition || explosion.damType!=flight.Definition.projectile.damageDef
                    || worker.GetType()!=typeof(DamageWorker_AddInjury) || worker!=explosion.damType.Worker || victim!=e.Target
                    || damage.Instigator!=e.Attacker || damage.Def!=explosion.damType || !CurrentIdentity(flight.Record) || !flight.Record.ImpactGuard()
                    || e.Target.Dead || e.CausedDeath || damageSequence==long.MaxValue) return null;
                return new PendingImpact {Flight=flight,Sequence=damageSequence+1,WasDead=e.Target.Dead,WasDowned=e.Target.Downed};
            } catch { return null; }
        }
        private static DamageWorker.DamageResult ApplyExplosionDamage(Thing victim,DamageInfo damage,Explosion explosion,DamageWorker worker)
        {
            MarkUnhealthyExplosion(explosion);
            var pending=PrepareExplosionImpact(explosion,worker,victim,damage);
            DamageWorker.DamageResult result;
            try { result=victim.TakeDamage(damage); }
            catch { if (pending!=null) pending.Flight.Record.TrackingLost=true; throw; }
            MarkUnhealthyExplosion(explosion);
            try {
                if (pending!=null && result!=null && !exhausted && damageDepth==0 && damageSequence==pending.Sequence && ExplosiveIsReady
                    && CurrentIdentity(pending.Flight.Record) && pending.Flight.Record.ImpactGuard() && Find.TickManager!=null) {
                    var e=pending.Flight.Record.Evidence;e.Record(result.totalDamageDealt,pending.WasDead,pending.WasDowned,e.Target.Dead,e.Target.Downed,Find.TickManager.TicksGame);
                }
            } catch { /* Forward native results without manufacturing completion. */ }
            return result!;
        }
    }
}
