#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Material delivery by construction tier (#2523). Gates the delivery job, not
    // labor: a frame needs every material before work starts. See
    // docs/developers/contracts/construction-tiers.md.
    internal static class ConstructionTierGate
    {
        private sealed class Site { public Thing Thing = null!; public int Tier; public List<ThingDef> Needs = new List<ThingDef>(); }
        private static readonly Dictionary<Map, List<Site>> cache = new Dictionary<Map, List<Site>>();
        private static int cacheTick = -1;
        private static bool installed;

        internal static void Install()
        {
            if (installed) return;
            new Harmony("rimgovernor.construction-tier-gate").Patch(
                AccessTools.Method(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor"),
                postfix: new HarmonyMethod(typeof(ConstructionTierGate), nameof(Gate)));
            installed = true;
        }

        // The vanilla job is HaulToContainer: targetA is the resource, targetC the site it
        // was found for, targetB and targetQueueB the sites it will deliver to in order.
        private static void Gate(Pawn pawn, ref Job? __result)
        {
            var job = __result;
            if (job == null || !Supervisor.IsActive || Current.Game == null || job.def != JobDefOf.HaulToContainer) return;
            var material = job.targetA.Thing?.def;
            if (material == null) return;
            if (Blocked(pawn, job.targetC.Thing, material)) { __result = null; JobFailReason.Is("a lower construction tier still needs this material"); return; }
            var targets = new List<LocalTargetInfo> { job.targetB };
            if (job.targetQueueB != null) targets.AddRange(job.targetQueueB);
            var kept = targets.Where(t => !Blocked(pawn, t.Thing, material)).ToList();
            if (kept.Count == targets.Count) return;
            job.targetB = kept[0];
            job.targetQueueB = kept.Count > 1 ? kept.Skip(1).ToList() : null;
        }

        private static bool Blocked(Pawn pawn, Thing? site, ThingDef material)
        {
            if (site == null || !(site is IConstructible) || pawn.Map == null) return false;
            var tier = ConstructionSkillGuard.Setting(site)?.Tier ?? ConstructionSkillSetting.None;
            if (tier == ConstructionSkillSetting.None) return false;
            var sites = Sites(pawn.Map).Where(s => s.Tier < tier && s.Needs.Contains(material))
                .Select(s => new KeyValuePair<int, Func<bool>>(s.Tier, () => CanDeliver(pawn, s.Thing)));
            return !ConstructionTierGatePolicy.Allows(tier, sites);
        }

        // The checks vanilla applies before this pawn could deliver: same map, not
        // forbidden, reachable, and the construction skill the def requires.
        private static bool CanDeliver(Pawn pawn, Thing site) =>
            site.Spawned && site.Map == pawn.Map && !site.IsForbidden(pawn)
            && pawn.CanReach(site, PathEndMode.Touch, Danger.Deadly)
            && (site.def.entityDefToBuild?.constructionSkillPrerequisite ?? 0) <= (pawn.skills?.GetSkill(SkillDefOf.Construction).Level ?? 0);

        // Tiered sites on the map with their unmet materials, rebuilt once per tick.
        private static List<Site> Sites(Map map)
        {
            var now = Find.TickManager.TicksGame;
            if (cacheTick != now) { cache.Clear(); cacheTick = now; }
            if (cache.TryGetValue(map, out var cached)) return cached;
            var list = new List<Site>();
            foreach (var setting in Current.Game.GetComponent<ConstructionSkillState>().Settings)
            {
                var t = setting.Target;
                if (setting.Tier == ConstructionSkillSetting.None || t == null || t.Destroyed || !t.Spawned
                    || t.Map != map || t.Faction != Faction.OfPlayer || !(t is IConstructible c)) continue;
                var needs = c.TotalMaterialCost().Select(n => n.thingDef).Where(d => c.ThingCountNeeded(d) > 0).ToList();
                if (needs.Count != 0) list.Add(new Site { Thing = t, Tier = setting.Tier, Needs = needs });
            }
            cache[map] = list;
            return list;
        }
    }
}
