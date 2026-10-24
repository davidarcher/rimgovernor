#nullable enable
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeSiteSecurity
    {
        private static readonly SimpleCurve SleepingMechCurve = (SimpleCurve)typeof(QuestPart_SleepingMechs).GetField("ThreatPointsToMechPointsCurve", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        private static bool ClosedPart(SitePart part)
        {
            var type = part.def.workerClass;
            switch (part.def.defName)
            {
                case "BanditCamp": case "Opportunity_SurveySite": return type == typeof(SitePartWorker);
                case "MechCluster": case "MechClusterForceNoConditionCauser": return type == typeof(SitePartWorker_MechCluster);
                case "MechanoidRelay": return type == typeof(SitePartWorker_MechanoidRelay);
                case "DownedRefugee": return type == typeof(SitePartWorker_DownedRefugee);
                case "PrisonerWillingToJoin": return type == typeof(SitePartWorker_PrisonerWillingToJoin);
                case "ItemStash": return type == typeof(SitePartWorker_ItemStash);
                case "PreciousLump": return type == typeof(SitePartWorker_PreciousLump);
                case "Outpost": return type == typeof(SitePartWorker_Outpost);
                case "AmbushEdge": case "AmbushHidden": return type == typeof(SitePartWorker_Ambush);
                case "Manhunters": return type == typeof(SitePartWorker_Manhunters);
                case "SleepingMechanoids": return type == typeof(SitePartWorker_SleepingMechanoids);
                case "Turrets": return type == typeof(SitePartWorker_Turrets);
                case "GravshipWreckage": case "BanditGang": return type == typeof(SitePartWorker);
                default: return false;
            }
        }
        private static bool LoadedGroundPart(SitePart part)
        {
            if (part.def.workerClass != typeof(SitePartWorker)) return false;
            switch (part.def.defName)
            {
                case "AncientReactor": case "CrashedMechanoidPlatform": case "FrozenTerraformer":
                case "OpportunitySite_AncientLaunchSite": case "OpportunitySite_AncientGarrison":
                case "OpportunitySite_AncientChemfuelRefinery": case "OpportunitySite_AncientWarehouse":
                case "OpportunitySite_AncientInfestedSettlement": return true;
                default: return false;
            }
        }
        private static double InitialBudget(SitePart part)
        {
            if (part.def.defName == "GravshipWreckage") return System.Math.Max(part.parms.points, Faction.OfMechanoids.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat))
                + DefDatabase<PawnKindDef>.AllDefsListForReading.Max(p => p.combatPower);
            if (part.def.defName == "BanditGang") return System.Math.Max(part.parms.points, part.parms.threatPoints);
            return part.parms.threatPoints;
        }
        /// <summary>
        /// The hostiles left on a loaded map, most pressing first. Traps count the non-player traps and the latent
        /// complex explosives; an unreadable complex trigger leaves the explosives out, and the site's budget is then
        /// unbounded.
        /// </summary>
        internal static Obs.SiteThreat Threat(Map map)
        {
            if (GenHostility.AnyHostileActiveThreatToPlayer(map, countDormantPawnsAsHostile: false, canBeFogged: true)) return new Obs.SiteThreat.Active();
            if (GenHostility.AnyHostileActiveThreatToPlayer(map, countDormantPawnsAsHostile: true, canBeFogged: true)
                || map.listerThings.AllThings.Any(t => t.HostileTo(Faction.OfPlayer) && t.TryGetComp<CompCanBeDormant>()?.Awake == false))
                return new Obs.SiteThreat.Dormant();
            var traps = map.listerThings.AllThings.OfType<Building_Trap>().Count(t => t.Faction != Faction.OfPlayer);
            if (NativeComplexSecurity.Latent(map, out _, out var explosives)) traps += explosives;
            return traps > 0 ? new Obs.SiteThreat.Trapped() : new Obs.SiteThreat.Clear();
        }

        internal static Obs.SiteThreatBudget Budget(Site site, Quest[] quests)
        {
            var visible = site.sitePartsKnown && site.parts.All(p => !p.hidden);
            double initial = visible ? site.ActualThreatPoints : 0;
            // Random complex threats are not resolved until map generation.
            // Other unrecognised parts leave the bound open rather than zero risk.
            var closed = visible && site.parts.All(ClosedPart);
            var complexes = site.parts.Where(p => p.def.workerClass == typeof(SitePartWorker_AncientComplex)).ToArray();
            if (visible && site.parts.All(p => ClosedPart(p) || complexes.Contains(p)))
            {
                double bounded = site.parts.Where(ClosedPart).Sum(InitialBudget);
                var complete = true;
                foreach (var part in complexes) { var bound = NativeComplexSecurity.Bound(part); if (!bound.HasValue) { complete = false; break; } bounded += bound.Value; }
                if (complete) { closed = true; initial = bounded; }
            }
            double pending = 0;
            if (site.HasMap)
            {
                var map = site.Map;
                if (visible && site.parts.All(p => ClosedPart(p) || LoadedGroundPart(p)))
                {
                    // Generated layouts have resolved their actual pawn
                    // identities. Turrets leave the bound open until their native
                    // weapon strength is part of this envelope.
                    closed = !map.listerThings.AllThings.OfType<Building_Turret>().Any(t => t.HostileTo(Faction.OfPlayer));
                    initial = System.Math.Max(initial, map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer) && !p.Dead).Sum(p => p.kindDef.combatPower));
                }
                if (NativeComplexSecurity.Latent(map, out var latent, out _)) pending = latent;
                else closed = false;
            }
            var timed = site.GetComponent<TimedDetectionRaids>();
            if (timed != null)
            {
                if (site.HasMap) pending += StorytellerUtility.DefaultThreatPointsNow(site.Map) * TimedDetectionRaids.RaidThreatPointsMultiplier;
                else closed = false;
            }
            var linked = quests.Where(q => q.QuestLookTargets.Any(t => t.HasWorldObject && t.WorldObject == site)).ToArray();
            foreach (var quest in linked)
            {
                if (NativeQuestThreats.TryRead(quest, out var points)) pending += points;
                else closed = false;
                foreach (var part in quest.PartsListForReading)
                {
                    if (part is QuestPart_RandomRaid raid && raid.mapParent == site && (raid.faction == null || raid.faction.HostileTo(Faction.OfPlayer)))
                    {
                        if (raid.useCurrentThreatPoints && !site.HasMap) { closed = false; continue; }
                        var budget = raid.useCurrentThreatPoints ? StorytellerUtility.DefaultThreatPointsNow(site.Map) * raid.currentThreatPointsFactor : raid.pointsRange.max;
                        if (raid.faction != null) budget = System.Math.Max(budget, raid.faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat));
                        pending += budget;
                    }
                    if (part is QuestPart_SleepingMechs sleeping && sleeping.mapParent == site) pending += SleepingMechCurve.Evaluate(sleeping.points);
                }
            }
            if (!closed || double.IsNaN(initial) || double.IsInfinity(initial) || initial < 0 || double.IsNaN(pending) || double.IsInfinity(pending) || pending < 0)
                return new Obs.SiteThreatBudget.Unbounded();
            return new Obs.SiteThreatBudget.Bounded(initial, pending);
        }
    }
}
