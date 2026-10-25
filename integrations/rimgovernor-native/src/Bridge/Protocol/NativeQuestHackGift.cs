using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Obs = RimGovernor.Contracts.Wire.Observations;
using PawnId = RimGovernor.Brain.Domain.PawnId;

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
                    if (site.Faction != null) risk = new Obs.QuestHackRisk { Hostile = site.Faction.HostileTo(Faction.OfPlayer) };
                }
            }
            if (targets.Count > 0 || quest.root?.defName == "Hack_WorshippedTerminal")
            {
                var row = new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.HackThings, Active = quest.State == QuestState.Ongoing, Goal = new Obs.QuestObjectiveGoal.Uncounted(), Timing = new Obs.QuestObjectiveTiming.Untimed(), PawnIds = [], MinimumMood = new Obs.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
                if (risk != null) row = row with { HackRisk = risk };
                foreach (var target in targets.Distinct().OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
                {
                    var hack = target.TryGetComp<CompHackable>();
                    if (hack == null) continue;
                    Obs.QuestHackTargetPlace place = target.Spawned
                        ? new Obs.QuestHackTargetPlace.OnMap(target.Map.uniqueID, [.. EligibleHackers(target).Select(p => new PawnId(p.GetUniqueLoadID()))])
                        : new Obs.QuestHackTargetPlace.Unspawned();
                    var fact = new Obs.QuestHackTarget { Id = target.GetUniqueLoadID(), LockedOut = hack.LockedOut, Autohack = hack.Autohack,
                        Satisfied = hack.IsHacked || (target.Destroyed && destroyedAllowed.Contains(target) && !strictlyHacked.Contains(target)), Place = place };
                    row = row with { HackTargets = [.. row.HackTargets, fact] };
                }
                yield return row;
            }
            foreach (var part in quest.PartsListForReading.OfType<QuestPart_BegForItems>())
            {
                var receiver = part.target;
                if (receiver == null || part.thingDef == null) continue;
                Obs.QuestGiftAsk ask = new Obs.QuestGiftAsk.Away();
                if (receiver.Spawned)
                {
                    ask = new Obs.QuestGiftAsk.NotAsking();
                    var lord = receiver.GetLord();
                    Pawn? exact = null; ThingDef? requested = null;
                    if (lord?.CurLordToil is LordToil_WaitForItems wait) { exact = wait.target; requested = wait.requestedThingDef; }
                    else if (lord?.CurLordToil is LordToil_TravelAndWaitForItems travel) { exact = travel.target; requested = travel.requestedThingDef; }
                    if (exact == receiver && requested == part.thingDef)
                        ask = new Obs.QuestGiftAsk.Asking(receiver.Map.uniqueID, Math.Max(0, GiveItemsToPawnUtility.ItemCountLeftToCollect(receiver)),
                            [.. receiver.Map.mapPawns.AllPawnsSpawned.Where(p => p.CurJob?.def == JobDefOf.GiveToPawn && p.CurJob.lord == lord).Select(p => new PawnId(p.GetUniqueLoadID()))],
                            [.. receiver.Map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && !receiver.IsForbidden(p)
                                && p.CanReach(receiver, PathEndMode.Touch, Danger.Deadly) && GiveItemsToPawnUtility.FindItemToGive(p, requested) != null)
                                .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(p => new PawnId(p.GetUniqueLoadID()))]);
                }
                var gift = new Obs.QuestGiftRequest
                {
                    Recipient = new PawnId(receiver.GetUniqueLoadID()),
                    DefName = part.thingDef.defName,
                    Pawns = [.. part.pawns.Where(p => p != null).Select(p => p.GetUniqueLoadID()).Distinct().Select(id => new PawnId(id))],
                    Ask = ask,
                };
                yield return new Obs.QuestObjective { Kind = Obs.QuestObjectiveKind.GiveItems, GiftRequest = gift, Active = quest.State == QuestState.Ongoing, Goal = new Obs.QuestObjectiveGoal.Uncounted(), Timing = new Obs.QuestObjectiveTiming.Untimed(), PawnIds = [], MinimumMood = new Obs.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
            }
        }
    }
}
