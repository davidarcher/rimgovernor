using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimGovernor.Native
{
    // Royalty colony facts, a keyed colony section: the neuroformer
    // stock, the pending bestowing ceremonies and the standing player thrones
    // of the player's home maps. Inactive without Royalty; a failed read throws
    // and fails the colony read.
    internal static class NativeRoyaltyColony
    {
        internal static RimGovernor.Brain.GameFacts.Observations.RoyaltySection Read()
        {
            if (!ModsConfig.RoyaltyActive) return new RimGovernor.Brain.GameFacts.Observations.RoyaltySection.Inactive();
            return new RimGovernor.Brain.GameFacts.Observations.RoyaltySection.Active(new RimGovernor.Brain.GameFacts.Observations.RoyaltyColonyFacts
            {
                Neuroformers = [.. Neuroformers()],
                Ceremonies = [.. Ceremonies()],
                Thrones = [.. Thrones()],
            });
        }

        // Throne ownership: every spawned player throne on the home
        // maps with its assigned colonist (Building_Throne.AssignedPawn), so
        // the planner can tell when a throne's assignment is done.
        private static IEnumerable<RimGovernor.Brain.GameFacts.Observations.RoyalThrone> Thrones()
        {
            foreach (var map in Find.Maps.Where(m => m.IsPlayerHome))
                foreach (var throne in map.listerBuildings.AllBuildingsColonistOfClass<Building_Throne>().OrderBy(t => t.thingIDNumber))
                {
                    var owner = throne.AssignedPawn;
                    yield return new RimGovernor.Brain.GameFacts.Observations.RoyalThrone
                    {
                        Thing = new RimGovernor.Brain.GameFacts.Common.Ref { Id = throne.GetUniqueLoadID() },
                        Owner = owner == null ? null : new RimGovernor.Brain.GameFacts.Common.Ref { Id = owner.GetUniqueLoadID() },
                    };
                }
        }

        // Pending bestowing ceremonies: the bestowing quest of each
        // colonist and royal faction (the offered or ongoing quest, never an
        // ended one). The lord is the bestower's: its Wait toil is the
        // ceremony waiting for the player's command, its job holds the
        // started flag; with no ceremony lord job the ritual has not started.
        private static IEnumerable<RimGovernor.Brain.GameFacts.Observations.BestowingCeremony> Ceremonies()
        {
            var royalFactions = Find.FactionManager.AllFactionsListForReading
                .Where(f => f?.def != null && f.def.HasRoyalTitles && GameThread.IsIdentifier(f.def.defName))
                .OrderBy(f => f.def.defName, StringComparer.Ordinal).ToList();
            foreach (var pawn in PawnsFinder.AllMaps_FreeColonists.Where(p => p.royalty != null).OrderBy(p => p.thingIDNumber))
            {
                foreach (var faction in royalFactions)
                {
                    var quest = RoyalTitleUtility.GetCurrentBestowingCeremonyQuest(pawn, faction);
                    if (quest == null || quest.State != QuestState.NotYetAccepted && quest.State != QuestState.Ongoing) continue;
                    var part = quest.PartsListForReading.OfType<QuestPart_BestowingCeremony>().FirstOrDefault();
                    var awarded = pawn.royalty.GetTitleAwardedWhenUpdating(faction, pawn.royalty.GetFavor(faction));
                    var bestower = part?.bestower;
                    var lord = bestower?.GetLord();
                    var job = lord?.LordJob as LordJob_BestowingCeremony;
                    yield return new RimGovernor.Brain.GameFacts.Observations.BestowingCeremony
                    {
                        Quest = quest.GetUniqueLoadID(),
                        Pawn = new PawnId(pawn.GetUniqueLoadID()),
                        Title = awarded?.defName,
                        Accepted = quest.State == QuestState.Ongoing,
                        BestowerWaiting = bestower != null && bestower.Spawned && lord?.CurLordToil is LordToil_BestowingCeremony_Wait,
                        Started = job != null && job.ceremonyStarted,
                    };
                }
            }
        }

        // The psylink neuroformer and every psycast neurotrainer. held counts
        // unforbidden spawned stacks on the player's home maps.
        private static IEnumerable<RimGovernor.Brain.GameFacts.Observations.NeuroformerStock> Neuroformers()
        {
            var defs = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => GameThread.IsIdentifier(d.defName) && (d == ThingDefOf.PsychicAmplifier || d.thingCategories?.Contains(ThingCategoryDefOf.NeurotrainersPsycast) == true))
                .OrderBy(d => d.defName, StringComparer.Ordinal);
            var maps = Find.Maps.Where(m => m.IsPlayerHome).ToList();
            foreach (var def in defs)
            {
                yield return new RimGovernor.Brain.GameFacts.Observations.NeuroformerStock
                {
                    DefName = def.defName,
                    Held = maps.Sum(m => m.listerThings.ThingsOfDef(def).Where(t => !t.IsForbidden(Faction.OfPlayer)).Sum(t => t.stackCount)),
                    Craftable = DefDatabase<RecipeDef>.AllDefsListForReading.Any(r => r.AvailableNow && r.products.Any(p => p.thingDef == def)),
                };
            }
        }
    }
}
