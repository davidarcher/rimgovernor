#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Anomaly colony facts (#1738): the knowledge the research manager holds
    // per category, codex discovery, the entities held on the colony map and
    // the GameComponent_Anomaly state that gates and times Anomaly threats.
    // Per-thing facts ride the pawn and building rows (NativeAnomalyFacts).
    // Every verdict is the game's own; the section is absent without Anomaly.
    internal static class NativeAnomalyColony
    {
        private static string? Id(string? value) => value != null && ProtoBoundary.IsIdentifier(value) ? value : null;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal static Obs.AnomalySection? Read(Map map)
        {
            if (!ModsConfig.AnomalyActive) return null;
            try {
                var facts = new Obs.AnomalyColonyFacts();
                var research = Find.ResearchManager;
                foreach (var category in DefDatabase<KnowledgeCategoryDef>.AllDefsListForReading.Where(d => Id(d.defName) != null).OrderBy(d => d.defName, StringComparer.Ordinal)) {
                    var row = new Obs.KnowledgeProgress { Category = category.defName, ProjectsAvailable = research.AnyProjectsAvailableWithKnowledgeCategory(category) };
                    if (research.GetProject(category) is ResearchProjectDef project) {
                        row.CurrentProject = Id(project.defName);
                        var knowledge = research.GetKnowledge(project);
                        if (Finite(knowledge)) row.Knowledge = knowledge;
                    }
                    facts.Knowledge.Add(row);
                }
                var codex = Find.EntityCodex;
                foreach (var category in DefDatabase<EntityCategoryDef>.AllDefsListForReading.Where(d => Id(d.defName) != null).OrderBy(d => d.defName, StringComparer.Ordinal))
                    facts.Codex.Add(new Obs.CodexProgress { Category = category.defName, Entries = (uint)EntityCodex.EntryCountInCategory(category), Discovered = (uint)codex.DiscoveredCount(category) });
                foreach (var entry in DefDatabase<EntityCodexEntryDef>.AllDefsListForReading.Where(d => Id(d.defName) != null && codex.Discovered(d)).OrderBy(d => d.defName, StringComparer.Ordinal))
                    facts.DiscoveredEntries.Add(entry.defName);
                foreach (var building in map.listerBuildings.allBuildingsColonist.OrderBy(b => b.thingIDNumber))
                    if (building.GetComp<CompEntityHolder>()?.HeldPawn is Pawn held)
                        facts.HeldEntities.Add(new Obs.HeldEntity { PawnId = Id(held.GetUniqueLoadID()), PlatformId = Id(building.GetUniqueLoadID()) });
                facts.HoldingPlatformAvailable = StudyUtility.HoldingPlatformAvailableOnCurrentMap();
                facts.Incidents = Incidents(Find.Anomaly);
                return new Obs.AnomalySection { Observed = facts };
            } catch (Exception ex) {
                return new Obs.AnomalySection { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic(ex.Message) } };
            }
        }

        private static Obs.AnomalyIncidentState Incidents(GameComponent_Anomaly anomaly)
        {
            var row = new Obs.AnomalyIncidentState { MonolithSpawned = anomaly.MonolithSpawned, Level = anomaly.Level, HighestLevelReached = anomaly.HighestLevelReached,
                QuestlineEnded = anomaly.QuestlineEnded, TicksSinceLevelChange = anomaly.TicksSinceLastLevelChange, AmbientHorrorMode = anomaly.AmbientHorrorMode,
                AnomalyStudyEnabled = anomaly.AnomalyStudyEnabled, VoidAwakeningActive = anomaly.VoidAwakeningActive(), AwokenCorpseActive = anomaly.HasActiveAwokenCorpse(),
                MetalhorrorImplantPossible = anomaly.CanNewMetalhorrorBiosignatureImplantOccur };
            if (Id(anomaly.LevelDef?.defName) is string level) row.LevelDef = level;
            if (Finite(anomaly.AnomalyThreatFractionNow)) row.AnomalyThreatFractionNow = anomaly.AnomalyThreatFractionNow;
            return row;
        }
    }
}
