#nullable enable
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

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
        internal static Obs.QuestSiteSecurity Read(Site site, Quest[] quests)
        {
            var row = new Obs.QuestSiteSecurity { Known = false };
            var visible = site.sitePartsKnown && site.parts.All(p => !p.hidden);
            if (visible) row.InitialPoints = site.ActualThreatPoints;
            // Random complex threats are not resolved until map generation.
            // Other unrecognised parts remain unknown rather than zero risk.
            var closed = visible && site.parts.All(ClosedPart);
            var complexes = site.parts.Where(p => p.def.workerClass == typeof(SitePartWorker_AncientComplex)).ToArray();
            if (visible && site.parts.All(p => ClosedPart(p) || complexes.Contains(p)))
            {
                double initial = site.parts.Where(ClosedPart).Sum(InitialBudget);
                var complete = true;
                foreach (var part in complexes) { var bound = NativeComplexSecurity.Bound(part); if (!bound.HasValue) { complete = false; break; } initial += bound.Value; }
                if (complete) { closed = true; row.InitialPoints = initial; }
            }
            if (site.HasMap)
            {
                var map = site.Map;
                row.ActiveThreat = GenHostility.AnyHostileActiveThreatToPlayer(map, countDormantPawnsAsHostile: false, canBeFogged: true);
                row.DormantThreat = map.listerThings.AllThings.Any(t => t.HostileTo(Faction.OfPlayer) && t.TryGetComp<CompCanBeDormant>()?.Awake == false);
                row.TrapCount = map.listerThings.AllThings.OfType<Building_Trap>().Count(t => t.Faction != Faction.OfPlayer);
                row.Known = closed;
                if (visible && site.parts.All(p => ClosedPart(p) || LoadedGroundPart(p)))
                {
                    row.Known = !map.listerThings.AllThings.OfType<Building_Turret>().Any(t => t.HostileTo(Faction.OfPlayer));
                    // Generated layouts have resolved their actual pawn
                    // identities. Turrets retain unknown until their native
                    // weapon strength is part of this envelope.
                    row.InitialPoints = System.Math.Max(row.HasInitialPoints ? row.InitialPoints : 0,
                        map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(Faction.OfPlayer) && !p.Dead).Sum(p => p.kindDef.combatPower));
                }
                if (NativeComplexSecurity.Latent(map, out var latent, out var explosives)) { row.PendingRaidPoints = latent; row.TrapCount += explosives; }
                else row.Known = false;
            }
            else row.Known = closed;
            var timed = site.GetComponent<TimedDetectionRaids>();
            if (timed != null)
            {
                row.DetectionActive = timed.DetectionCountdownStarted;
                row.RaidsSent = timed.RaidsSentCount;
                if (timed.DetectionCountdownStarted) row.DetectionTicksLeft = System.Math.Max(0, timed.TicksLeftToSendRaids);
                if (site.HasMap) row.PendingRaidPoints = (row.HasPendingRaidPoints ? row.PendingRaidPoints : 0) + StorytellerUtility.DefaultThreatPointsNow(site.Map) * TimedDetectionRaids.RaidThreatPointsMultiplier;
                else row.Known = false;
            }
            else row.DetectionActive = false;
            var linked = quests.Where(q => q.QuestLookTargets.Any(t => t.HasWorldObject && t.WorldObject == site)).ToArray();
            double pending = row.HasPendingRaidPoints ? row.PendingRaidPoints : 0;
            foreach (var quest in linked)
            {
                var points = NativeQuestThreats.Read(quest);
                if (points.HasValue) pending += points.Value;
                else if (quest.PartsListForReading.OfType<QuestPart_Incident>().Any(p => p.incident?.category == IncidentCategoryDefOf.ThreatBig || p.incident?.category == IncidentCategoryDefOf.ThreatSmall)) row.Known = false;
                foreach (var part in quest.PartsListForReading)
                {
                    if (part is QuestPart_RandomRaid raid && raid.mapParent == site && (raid.faction == null || raid.faction.HostileTo(Faction.OfPlayer)))
                    {
                        if (raid.useCurrentThreatPoints && !site.HasMap) { row.Known = false; continue; }
                        var budget = raid.useCurrentThreatPoints ? StorytellerUtility.DefaultThreatPointsNow(site.Map) * raid.currentThreatPointsFactor : raid.pointsRange.max;
                        if (raid.faction != null) budget = System.Math.Max(budget, raid.faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat));
                        pending += budget;
                    }
                    if (part is QuestPart_SleepingMechs sleeping && sleeping.mapParent == site) pending += SleepingMechCurve.Evaluate(sleeping.points);
                }
            }
            row.PendingRaidPoints = pending;
            return row;
        }
    }
}
