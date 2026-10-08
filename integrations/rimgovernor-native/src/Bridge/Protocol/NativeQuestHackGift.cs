#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestHackGift
    {
        internal static IEnumerable<Pawn> EligibleHackers(Thing target)
        {
            var hack = target.TryGetComp<CompHackable>();
            if (!target.Spawned || target.Destroyed || hack == null) return Enumerable.Empty<Pawn>();
            return target.Map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                && p.workSettings != null && p.workSettings.GetPriority(WorkTypeDefOf.Research) > 0
                && !target.IsForbidden(p) && hack.CanHackNow(p) && p.CanReserve(target))
                .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal);
        }
        internal static IEnumerable<Obs.QuestObjective> Read(Quest quest)
        {
            var targets = quest.PartsListForReading.OfType<QuestPart_Filter_AllThingsHacked>().SelectMany(p => p.things).Where(t => t != null).Distinct().ToList();
            var destroyedAllowed = quest.PartsListForReading.OfType<QuestPart_Filter_AllThingsHackedOrDestroyed>().SelectMany(p => p.things).Where(t => t != null).ToHashSet();
            var strictlyHacked = quest.PartsListForReading.OfType<QuestPart_Filter_AllThingsHacked>().Where(p => !(p is QuestPart_Filter_AllThingsHackedOrDestroyed)).SelectMany(p => p.things).ToHashSet();
            Obs.QuestHackRisk? risk = null;
            if (quest.root?.defName == "Hack_WorshippedTerminal")
            {
                var sites = quest.QuestLookTargets.Where(t => t.HasWorldObject).Select(t => t.WorldObject).OfType<Site>()
                    .Where(s => s.parts.Any(p => p.def == SitePartDefOf.WorshippedTerminal)).Distinct().ToArray();
                if (sites.Length == 1)
                {
                    var site = sites[0];
                    targets.AddRange(site.parts.Where(p => p.def == SitePartDefOf.WorshippedTerminal).SelectMany(p => p.things)
                        .Where(t => t.def == ThingDefOf.AncientTerminal_Worshipful));
                    // GenSpawn removes the terminal from the site's ThingOwner.
                    // The vanilla root's exact quest signal retains its identity on-map.
                    if (site.HasMap)
                    {
                        var loaded = site.Map.listerThings.ThingsOfDef(ThingDefOf.AncientTerminal_Worshipful)
                            .Where(t => NativeQuestTargetIdentity.WorshippedTerminalSignal(quest.id, t.TryGetComp<CompHackable>()?.hackingStartedSignal ?? ""))
                            .ToArray();
                        if (loaded.Length == 1) targets.Add(loaded[0]);
                    }
                    if (site.Faction != null) risk = new Obs.QuestHackRisk { FactionId = site.Faction.GetUniqueLoadID(), Hostile = site.Faction.HostileTo(Faction.OfPlayer) };
                }
            }
            if (targets.Count > 0 || quest.root?.defName == "Hack_WorshippedTerminal")
            {
                var row = new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.HackThings, Active = quest.State == QuestState.Ongoing };
                if (risk != null) row.HackRisk = risk;
                foreach (var target in targets.Distinct().OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
                {
                    var hack = target.TryGetComp<CompHackable>();
                    if (hack == null) continue;
                    var state = new Obs.HackableState { Hacked = hack.IsHacked, LockedOut = hack.LockedOut, Autohack = hack.Autohack };
                    if (!double.IsNaN(hack.ProgressPercent) && !double.IsInfinity(hack.ProgressPercent)) state.ProgressPercent = hack.ProgressPercent;
                    if (!double.IsNaN(hack.defence) && !double.IsInfinity(hack.defence)) state.Defence = hack.defence;
                    var fact = new Obs.QuestHackTarget { Id = target.GetUniqueLoadID(), Spawned = target.Spawned, Hackable = state,
                        Satisfied = hack.IsHacked || (target.Destroyed && destroyedAllowed.Contains(target) && !strictlyHacked.Contains(target)) };
                    if (target.Spawned) { fact.MapId = target.Map.uniqueID; fact.EligiblePawnIds.Add(EligibleHackers(target).Select(p => p.GetUniqueLoadID())); }
                    row.HackTargets.Add(fact);
                }
                yield return row;
            }
            foreach (var part in quest.PartsListForReading.OfType<QuestPart_BegForItems>())
            {
                var receiver = part.target;
                if (receiver == null || part.thingDef == null) continue;
                var gift = new Obs.QuestGiftRequest { RecipientId = receiver.GetUniqueLoadID(), Def = part.thingDef.defName };
                gift.PawnIds.Add(part.pawns.Where(p => p != null).Select(p => p.GetUniqueLoadID()).Distinct());
                if (receiver.Spawned)
                {
                    gift.MapId = receiver.Map.uniqueID;
                    var lord = receiver.GetLord();
                    Pawn? exact = null; ThingDef? requested = null;
                    if (lord?.CurLordToil is LordToil_WaitForItems wait) { exact = wait.target; requested = wait.requestedThingDef; }
                    else if (lord?.CurLordToil is LordToil_TravelAndWaitForItems travel) { exact = travel.target; requested = travel.requestedThingDef; }
                    if (exact == receiver && requested == part.thingDef)
                    {
                        gift.Remaining = Math.Max(0, GiveItemsToPawnUtility.ItemCountLeftToCollect(receiver));
                        gift.HaulingPawnIds.Add(receiver.Map.mapPawns.AllPawnsSpawned.Where(p => p.CurJob?.def == JobDefOf.GiveToPawn && p.CurJob.lord == lord).Select(p => p.GetUniqueLoadID()));
                        gift.EligiblePawnIds.Add(receiver.Map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                            && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && !receiver.IsForbidden(p)
                            && p.CanReach(receiver, PathEndMode.Touch, Danger.Deadly) && GiveItemsToPawnUtility.FindItemToGive(p, requested) != null)
                            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(p => p.GetUniqueLoadID()));
                    }
                }
                yield return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.GiveItems, GiftRequest = gift, Active = quest.State == QuestState.Ongoing };
            }
        }
    }
}
