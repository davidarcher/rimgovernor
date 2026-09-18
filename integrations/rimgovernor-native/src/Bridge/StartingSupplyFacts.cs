#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Forbidden starting-supply census shared by the typed colony facts and
    /// the JSON colony facts tool. A stack belongs when its definition is one
    /// the scenario placed at the player start ("start with" and "start near"
    /// parts), it is still forbidden, reachable and within <see cref="Radius"/>
    /// cells of the colonists. Identity travels with each row so the
    /// controller keys its cohort by thing rather than by cell: a stack a
    /// builder hauls aside stays the same stack at a new cell.
    /// </summary>
    internal static class StartingSupplyFacts
    {
        // Near-start scatter lands within 20 cells of the player start spot and the
        // colonists' mean sits beside it at load; the margin covers their spread.
        internal const int Radius = 40;
        internal const int Limit = 256;
        private static readonly FieldInfo? ThingDefField = typeof(ScenPart_ThingCount).GetField("thingDef", BindingFlags.NonPublic | BindingFlags.Instance);

        internal static HashSet<ThingDef> ScenarioDefinitions()
        {
            var defs = new HashSet<ThingDef>();
            var scenario = Current.Game?.Scenario;
            if (scenario == null || ThingDefField == null) return defs;
            foreach (var part in scenario.AllParts)
            {
                if (!(part is ScenPart_StartingThing_Defined) && !(part is ScenPart_ScatterThingsNearPlayerStart)) continue;
                if (ThingDefField.GetValue(part) is ThingDef def) defs.Add(def);
            }
            return defs;
        }

        /// <summary>Forbidden starting stacks in native (z, x, id) order, bounded to <see cref="Limit"/>.</summary>
        internal static List<Thing> Forbidden(List<Thing> things, IntVec3 center, Func<Thing, bool> reachable)
        {
            var defs = ScenarioDefinitions();
            var player = Faction.OfPlayerSilentFail;
            return things.Where(t => t.def.category == ThingCategory.Item && defs.Contains(t.def)
                    && (t.Faction == null || t.Faction.IsPlayer) && t.IsForbidden(player)
                    && t.Position.DistanceTo(center) <= Radius && reachable(t))
                .OrderBy(t => t.Position.z).ThenBy(t => t.Position.x).ThenBy(t => t.thingIDNumber).Take(Limit).ToList();
        }
    }
}
