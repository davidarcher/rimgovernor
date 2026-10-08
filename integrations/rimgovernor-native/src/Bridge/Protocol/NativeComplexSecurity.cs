#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class NativeComplexSecurity
    {
        private static readonly FloatRange AttemptFactor = (FloatRange)typeof(LayoutWorkerComplex).GetField("ThreatPointsFactorRange", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        private static readonly FloatRange FuelRadius = (FloatRange)typeof(ComplexThreatWorker_FuelNode).GetField("ExplosiveRadiusRandomRange", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

        internal static double? Bound(SitePart part)
        {
            var sketch = part.parms.ancientLayoutStructureSketch;
            if (sketch == null && part.site.HasMap)
            {
                var observed = part.site.Map.layoutStructureSketches.Where(s => s.layoutDef is ComplexLayoutDef).ToArray();
                if (observed.Length != 1) return null;
                sketch = observed[0];
            }
            if (!(sketch?.layoutDef is ComplexLayoutDef layout) || layout.threats == null) return null;
            if (layout.workerClass != typeof(LayoutWorkerComplex_Ancient) && layout.workerClass != typeof(LayoutWorkerComplex_Mechanitor)) return null;
            var definitions = new HashSet<ComplexThreatDef>(layout.threats.Select(t => t.def));
            foreach (var def in definitions.ToArray())
                if (def.workerClass == typeof(ComplexThreatWorker_SecurityCrate))
                    definitions.UnionWith(((ComplexThreatWorker_SecurityCrate)def.Worker).SubThreats);
            double factor = 1, passive = 1;
            foreach (var def in definitions)
            {
                var type = def.workerClass;
                if (type != typeof(ComplexThreatWorker_SecurityCrate) && type != typeof(ComplexThreatWorker_Ambush)
                    && type != typeof(ComplexThreatWorker_RaidTerminal) && type != typeof(ComplexThreatWorker_FuelNode)
                    && type != typeof(ComplexThreatWorker_Infestations) && type != typeof(ComplexThreatWorker_CryptosleepPods)
                    && type != typeof(ComplexThreatWorker_SleepingInsects) && type != typeof(ComplexThreatWorker_SleepingMechanoids)) return null;
                if (def.postSpawnPassiveThreatFactor <= 0 || float.IsNaN(def.postSpawnPassiveThreatFactor)) return null;
                passive = Math.Min(passive, def.postSpawnPassiveThreatFactor);
                if (def.delayChance > 0)
                {
                    if (def.delayTickOptions == null || def.threatFactorOverDelayTicksCurve == null) return null;
                    foreach (var ticks in def.delayTickOptions) factor = Math.Max(factor, def.threatFactorOverDelayTicksCurve.Evaluate(ticks));
                }
            }
            var budget = part.parms.threatPoints;
            if (budget <= 0) return 0;
            // SecurityCrate may resolve a subthreat, multiplying delay and
            // passive factors twice. The last resolution can overspend the
            // remaining budget by a minimum-size group or explosion.
            var largestPawn = DefDatabase<PawnKindDef>.AllDefsListForReading.Max(p => p.combatPower);
            var factionMinimum = Find.FactionManager.AllFactionsListForReading.Where(f => f.HostileTo(Faction.OfPlayer))
                .Select(f => f.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat)).DefaultIfEmpty(0).Max();
            // Vanilla Infestations floors its allocation at 200; FuelNode
            // spends radius*10; RaidTerminal floors at faction minimum*1.05.
            var final = Math.Max(200, Math.Max(FuelRadius.max * 10, Math.Max(factionMinimum * 1.05, AttemptFactor.max * budget * factor * factor + largestPawn)));
            var bound = (budget + final) / (passive * passive);
            return double.IsNaN(bound) || double.IsInfinity(bound) || bound < 0 ? null : (double?)bound;
        }

        internal static bool Latent(Map map, out double points, out int explosives)
        {
            points = 0; explosives = 0;
            var pawns = new HashSet<Pawn>();
            foreach (var action in map.listerThings.AllThings.OfType<SignalAction>())
            {
                switch (action)
                {
                    case SignalAction_Incident incident:
                        if (incident.incident?.category != IncidentCategoryDefOf.ThreatBig && incident.incident?.category != IncidentCategoryDefOf.ThreatSmall) break;
                        if (incident.incidentParms == null) return false;
                        points += incident.incidentParms.points; break;
                    case SignalAction_Ambush ambush: points += ambush.points; break;
                    case SignalAction_Infestation infestation:
                        if (!infestation.insectsPoints.HasValue) return false;
                        points += infestation.hivesCount * infestation.insectsPoints.Value; break;
                    case SignalAction_OpenCasket open:
                        foreach (var casket in open.caskets.OfType<Building_Casket>())
                            foreach (var pawn in casket.GetDirectlyHeldThings().OfType<Pawn>())
                                if (pawn.HostileTo(Faction.OfPlayer) && pawns.Add(pawn)) points += pawn.kindDef.combatPower;
                        break;
                    case SignalAction_DormancyWakeUp wake:
                        if (wake.lord != null) foreach (var pawn in wake.lord.ownedPawns)
                            if (pawn.HostileTo(Faction.OfPlayer) && pawns.Add(pawn)) points += pawn.kindDef.combatPower;
                        break;
                    case SignalAction_StartWick wick:
                        if (wick.thingWithWick != null && !wick.thingWithWick.Destroyed) explosives++;
                        break;
                    case SignalAction_Letter _: case SignalAction_Message _: case SignalAction_OpenDoor _: case SignalAction_SoundOneShot _: break;
                    default: return false;
                }
            }
            return !double.IsNaN(points) && !double.IsInfinity(points) && points >= 0;
        }
    }
}
