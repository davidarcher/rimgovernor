#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Anomaly colony facts: the GameComponent_Anomaly level state and the
    // void monolith it gates. Per-thing facts ride the pawn and building rows
    // (NativeAnomalyFacts). Every verdict is the game's own; the section is
    // inactive without Anomaly, and a failed read throws and fails the colony
    // read.
    internal static class NativeAnomalyColony
    {
        internal static Obs.AnomalySection Read()
        {
            if (!ModsConfig.AnomalyActive) return new Obs.AnomalySection.Inactive();
            var anomaly = Find.Anomaly;
            return new Obs.AnomalySection.Active(new Obs.AnomalyColonyFacts
            {
                Incidents = new Obs.AnomalyIncidentState { MonolithSpawned = anomaly.MonolithSpawned, Level = anomaly.Level, AmbientHorrorMode = anomaly.AmbientHorrorMode },
                Monolith = Monolith(anomaly),
            });
        }

        // The monolith and the endgame it gates; null unless it is spawned.
        // Every verdict is the game's own: CanActivate, the next level def's
        // requirement, the Gleaming comp's CanInteract and the spawned void
        // things on the monolith's map.
        private static Obs.MonolithState? Monolith(GameComponent_Anomaly anomaly)
        {
            if (!anomaly.MonolithSpawned || anomaly.monolith is not Building_VoidMonolith monolith || monolith.Map is not Map map) return null;
            var next = anomaly.NextLevelDef;
            var structures = map.listerThings.ThingsOfDef(ThingDefOf.VoidStructure);
            // The node stands on the pocket map the Gleaming monolith skips a pawn into, so every loaded map is searched.
            var node = Find.Maps.SelectMany(m => m.listerThings.ThingsOfDef(ThingDefOf.VoidNode)).OrderBy(t => t.thingIDNumber).FirstOrDefault();
            var nodeComp = node?.TryGetComp<CompVoidNode>();
            var touchable = node != null && nodeComp != null && node.Map != null && nodeComp.CanInteract().Accepted;
            return new Obs.MonolithState
            {
                MonolithId = monolith.GetUniqueLoadID(),
                CanActivate = monolith.CanActivate(out _, out _),
                NextLevelDef = next?.defName,
                CodexShortfall = next?.entityCatagoryCompletionRequired is EntityCategoryDef category
                    ? Math.Max(0, next.entityCountCompletionRequired - Find.EntityCodex.DiscoveredCount(category))
                    : 0,
                BlockingConditions = [.. Blocking(map, next)],
                GleamingInteractionAvailable = monolith.GetComp<CompGleamingMonolith>()?.CanInteract().Accepted ?? false,
                PendingVoidStructureIds = [.. structures.OrderBy(t => t.thingIDNumber)
                    .Where(t => t.TryGetComp<CompVoidStructure>()?.CanInteract().Accepted == true).Select(t => t.GetUniqueLoadID())],
                VoidNodeId = touchable ? node!.GetUniqueLoadID() : null,
                VoidNodePawnIds = touchable
                    ? [.. node!.Map!.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).Where(p => nodeComp!.CanInteract(p).Accepted).Select(p => p.GetUniqueLoadID())]
                    : [],
            };
        }

        // The active conditions on the monolith's map that the next level
        // lists as unreachableDuringConditions, sorted.
        private static IEnumerable<string> Blocking(Map map, MonolithLevelDef? next)
        {
            if (next?.unreachableDuringConditions == null) return [];
            return map.GameConditionManager.ActiveConditions.Select(c => c.def).Distinct().Where(next.unreachableDuringConditions.Contains)
                .Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal);
        }
    }
}
