#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Royalty colony facts, a keyed colony section: the neuroformer
    // stock, the pending bestowing ceremonies and the standing player thrones
    // of the player's home maps. The section is absent without Royalty.
    internal static class NativeRoyaltyColony
    {
        internal static Obs.RoyaltySection? Read(Map map)
        {
            if (!ModsConfig.RoyaltyActive) return null;
            try {
                var facts = new Obs.RoyaltyColonyFacts();
                ReadThrones(facts);
                ReadNeuroformers(facts);
                ReadCeremonies(facts);
                return new Obs.RoyaltySection { Observed = facts };
            } catch (Exception) {
                return new Obs.RoyaltySection { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed,
                    Detail = "Royalty colony facts unavailable." } };
            }
        }

        // Throne ownership: every spawned player throne on the home
        // maps with its assigned colonist (Building_Throne.AssignedPawn), so
        // the planner can tell when a throne's assignment is done.
        private static void ReadThrones(Obs.RoyaltyColonyFacts facts)
        {
            foreach (var map in Find.Maps.Where(m => m.IsPlayerHome))
                foreach (var throne in map.listerBuildings.AllBuildingsColonistOfClass<Building_Throne>().OrderBy(t => t.thingIDNumber))
                {
                    var row = new Obs.RoyalThrone { Thing = new Common.Ref { Id = throne.GetUniqueLoadID() } };
                    if (ProtoBoundary.IsIdentifier(throne.def.defName)) row.DefName = throne.def.defName;
                    var owner = throne.AssignedPawn;
                    if (owner != null) row.Owner = new Common.Ref { Id = owner.GetUniqueLoadID() };
                    facts.Thrones.Add(row);
                }
        }

        // Pending bestowing ceremonies: the bestowing quest of each
        // colonist and royal faction (the offered or ongoing quest, never an
        // ended one). The lord is the bestower's: its Wait toil is the
        // ceremony waiting for the player's command, its job holds the
        // started flag, the ceremony spot and the colonist participants.
        private static void ReadCeremonies(Obs.RoyaltyColonyFacts facts)
        {
            var royalFactions = Find.FactionManager.AllFactionsListForReading
                .Where(f => f?.def != null && f.def.HasRoyalTitles && ProtoBoundary.IsIdentifier(f.def.defName))
                .OrderBy(f => f.def.defName, StringComparer.Ordinal).ToList();
            foreach (var pawn in PawnsFinder.AllMaps_FreeColonists.Where(p => p.royalty != null).OrderBy(p => p.thingIDNumber))
            {
                foreach (var faction in royalFactions)
                {
                    var quest = RoyalTitleUtility.GetCurrentBestowingCeremonyQuest(pawn, faction);
                    if (quest == null || quest.State != QuestState.NotYetAccepted && quest.State != QuestState.Ongoing) continue;
                    var part = quest.PartsListForReading.OfType<QuestPart_BestowingCeremony>().FirstOrDefault();
                    var row = new Obs.BestowingCeremony { Quest = quest.GetUniqueLoadID(), Pawn = new Common.Ref { Id = pawn.GetUniqueLoadID() }, FactionDef = faction.def.defName, Accepted = quest.State == QuestState.Ongoing };
                    var awarded = pawn.royalty.GetTitleAwardedWhenUpdating(faction, pawn.royalty.GetFavor(faction));
                    if (awarded != null && ProtoBoundary.IsIdentifier(awarded.defName)) row.Title = awarded.defName;
                    var bestower = part?.bestower;
                    if (bestower != null) row.Bestower = new Common.Ref { Id = bestower.GetUniqueLoadID() };
                    var lord = bestower?.GetLord();
                    var job = lord?.LordJob as LordJob_BestowingCeremony;
                    row.BestowerWaiting = bestower != null && bestower.Spawned && lord?.CurLordToil is LordToil_BestowingCeremony_Wait;
                    if (job != null)
                    {
                        row.Started = job.ceremonyStarted;
                        var spot = job.Spot;
                        if (spot.IsValid) row.Spot = new Common.Cell { X = spot.x, Z = spot.z };
                        foreach (var attendee in (job.colonistParticipants ?? new List<Pawn>()).Where(p => p != null).OrderBy(p => p.thingIDNumber))
                            row.Attendees.Add(new Common.Ref { Id = attendee.GetUniqueLoadID() });
                    }
                    facts.Ceremonies.Add(row);
                }
            }
        }

        // The psylink neuroformer and every psycast neurotrainer. held counts
        // unforbidden spawned stacks on the player's home maps.
        private static void ReadNeuroformers(Obs.RoyaltyColonyFacts facts)
        {
            var defs = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => ProtoBoundary.IsIdentifier(d.defName) && (d == ThingDefOf.PsychicAmplifier || d.thingCategories?.Contains(ThingCategoryDefOf.NeurotrainersPsycast) == true))
                .OrderBy(d => d.defName, StringComparer.Ordinal);
            var maps = Find.Maps.Where(m => m.IsPlayerHome).ToList();
            foreach (var def in defs)
            {
                var row = new Obs.NeuroformerStock
                {
                    DefName = def.defName,
                    Held = maps.Sum(m => m.listerThings.ThingsOfDef(def).Where(t => !t.IsForbidden(Faction.OfPlayer)).Sum(t => t.stackCount)),
                    Craftable = DefDatabase<RecipeDef>.AllDefsListForReading.Any(r => r.AvailableNow && r.products.Any(p => p.thingDef == def)),
                    Tradeable = def.tradeability.TraderCanSell()
                };
                var teaches = def.comps?.OfType<CompProperties_UseEffect_GainAbility>().FirstOrDefault()?.ability;
                if (teaches != null && ProtoBoundary.IsIdentifier(teaches.defName)) row.TeachesPsycast = teaches.defName;
                facts.Neuroformers.Add(row);
            }
        }
    }
}
