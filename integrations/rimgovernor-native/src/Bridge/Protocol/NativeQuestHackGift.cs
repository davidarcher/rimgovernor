using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using PawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Native
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
        internal static IEnumerable<RimGovernor.Brain.GameFacts.Observations.QuestObjective> Read(Quest quest)
        {
            var targets = quest.PartsListForReading.OfType<QuestPart_Filter_AllThingsHacked>().SelectMany(p => p.things).Where(t => t != null).Distinct().ToList();
            var destroyedAllowed = quest.PartsListForReading.OfType<QuestPart_Filter_AllThingsHackedOrDestroyed>().SelectMany(p => p.things).Where(t => t != null).ToHashSet();
            var strictlyHacked = quest.PartsListForReading.OfType<QuestPart_Filter_AllThingsHacked>().Where(p => !(p is QuestPart_Filter_AllThingsHackedOrDestroyed)).SelectMany(p => p.things).ToHashSet();
            RimGovernor.Brain.GameFacts.Observations.QuestHackRisk? risk = null;
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
                    if (site.Faction != null) risk = new RimGovernor.Brain.GameFacts.Observations.QuestHackRisk { Hostile = site.Faction.HostileTo(Faction.OfPlayer) };
                }
            }
            if (targets.Count > 0 || quest.root?.defName == "Hack_WorshippedTerminal")
            {
                var row = new RimGovernor.Brain.GameFacts.Observations.QuestObjective { Kind = RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.HackThings, Active = quest.State == QuestState.Ongoing, Goal = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveGoal.Uncounted(), Timing = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveTiming.Untimed(), PawnIds = [], MinimumMood = new RimGovernor.Brain.GameFacts.Observations.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
                if (risk != null) row = row with { HackRisk = risk };
                foreach (var target in targets.Distinct().OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
                {
                    var hack = target.TryGetComp<CompHackable>();
                    if (hack == null) continue;
                    RimGovernor.Brain.GameFacts.Observations.QuestHackTargetPlace place = target.Spawned
                        ? new RimGovernor.Brain.GameFacts.Observations.QuestHackTargetPlace.OnMap(target.Map.uniqueID, [.. EligibleHackers(target).Select(p => new PawnId(p.GetUniqueLoadID()))])
                        : new RimGovernor.Brain.GameFacts.Observations.QuestHackTargetPlace.Unspawned();
                    var fact = new RimGovernor.Brain.GameFacts.Observations.QuestHackTarget { Id = target.GetUniqueLoadID(), LockedOut = hack.LockedOut, Autohack = hack.Autohack,
                        Satisfied = hack.IsHacked || (target.Destroyed && destroyedAllowed.Contains(target) && !strictlyHacked.Contains(target)), Place = place };
                    row = row with { HackTargets = [.. row.HackTargets, fact] };
                }
                yield return row;
            }
            foreach (var part in quest.PartsListForReading.OfType<QuestPart_BegForItems>())
            {
                var receiver = part.target;
                if (receiver == null || part.thingDef == null) continue;
                RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk ask = new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.Away();
                if (receiver.Spawned)
                {
                    ask = new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.NotAsking();
                    var lord = receiver.GetLord();
                    Pawn? exact = null; ThingDef? requested = null;
                    if (lord?.CurLordToil is LordToil_WaitForItems wait) { exact = wait.target; requested = wait.requestedThingDef; }
                    else if (lord?.CurLordToil is LordToil_TravelAndWaitForItems travel) { exact = travel.target; requested = travel.requestedThingDef; }
                    if (exact == receiver && requested == part.thingDef)
                        ask = new RimGovernor.Brain.GameFacts.Observations.QuestGiftAsk.Asking(receiver.Map.uniqueID, Math.Max(0, GiveItemsToPawnUtility.ItemCountLeftToCollect(receiver)),
                            [.. receiver.Map.mapPawns.AllPawnsSpawned.Where(p => p.CurJob?.def == JobDefOf.GiveToPawn && p.CurJob.lord == lord).Select(p => new PawnId(p.GetUniqueLoadID()))],
                            [.. receiver.Map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && !receiver.IsForbidden(p)
                                && p.CanReach(receiver, PathEndMode.Touch, Danger.Deadly) && GiveItemsToPawnUtility.FindItemToGive(p, requested) != null)
                                .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(p => new PawnId(p.GetUniqueLoadID()))]);
                }
                var gift = new RimGovernor.Brain.GameFacts.Observations.QuestGiftRequest
                {
                    Recipient = new PawnId(receiver.GetUniqueLoadID()),
                    DefName = part.thingDef.defName,
                    Pawns = [.. part.pawns.Where(p => p != null).Select(p => p.GetUniqueLoadID()).Distinct().Select(id => new PawnId(id))],
                    Ask = ask,
                };
                yield return new RimGovernor.Brain.GameFacts.Observations.QuestObjective { Kind = RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.GiveItems, GiftRequest = gift, Active = quest.State == QuestState.Ongoing, Goal = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveGoal.Uncounted(), Timing = new RimGovernor.Brain.GameFacts.Observations.QuestObjectiveTiming.Untimed(), PawnIds = [], MinimumMood = new RimGovernor.Brain.GameFacts.Observations.QuestMoodFloor.NoFloor(), LodgerMoods = [] };
            }
        }
    }
}
