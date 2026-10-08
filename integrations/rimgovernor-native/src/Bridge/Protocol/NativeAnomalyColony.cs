#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;
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
                facts.Monolith = Monolith(Find.Anomaly);
                return new Obs.AnomalySection { Observed = facts };
            } catch (Exception ex) {
                return new Obs.AnomalySection { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic(ex.Message) } };
            }
        }

        // The monolith and the endgame it gates (#2436); null (unknown) unless
        // it is spawned. Every verdict is the game's own: CanActivate, the next
        // level def's requirement, the Gleaming comp's CanInteract and the
        // spawned void things on the monolith's map. The block reasons repeat
        // CanActivate's two checks so a consumer reads which one holds.
        private static Obs.MonolithState? Monolith(GameComponent_Anomaly anomaly)
        {
            if (!anomaly.MonolithSpawned || anomaly.monolith is not Building_VoidMonolith monolith || monolith.Map is not Map map) return null;
            var row = new Obs.MonolithState { CanActivate = monolith.CanActivate(out _, out _) };
            if (anomaly.NextLevelDef is MonolithLevelDef next) {
                if (Id(next.defName) is string name) row.NextLevelDef = name;
                if (next.entityCatagoryCompletionRequired is EntityCategoryDef category && Id(category.defName) is string categoryName) {
                    row.NextLevelCodexCategory = categoryName;
                    row.NextLevelCodexRequired = (uint)Math.Max(0, next.entityCountCompletionRequired);
                    row.CodexShortfall = (uint)Math.Max(0, next.entityCountCompletionRequired - Find.EntityCodex.DiscoveredCount(category));
                }
                if (next.unreachableDuringConditions != null)
                    foreach (var condition in map.GameConditionManager.ActiveConditions.Select(c => c.def).Distinct().Where(next.unreachableDuringConditions.Contains).OrderBy(d => d.defName, StringComparer.Ordinal))
                        if (Id(condition.defName) is string conditionName) row.BlockingConditions.Add(conditionName);
            }
            row.GleamingInteractionAvailable = monolith.GetComp<CompGleamingMonolith>()?.CanInteract().Accepted ?? false;
            var structures = map.listerThings.ThingsOfDef(ThingDefOf.VoidStructure);
            row.VoidStructures = (uint)structures.Count;
            row.VoidStructuresActivated = (uint)structures.Count(t => t.TryGetComp<CompVoidStructure>()?.Active ?? false);
            row.VoidNodeExists = map.listerThings.ThingsOfDef(ThingDefOf.VoidNode).Count > 0;
            var quest = Find.QuestManager.questsInDisplayOrder.FirstOrDefault(q => !q.Historical && q.root == QuestScriptDefOf.EndGame_VoidAwakening);
            if (quest != null) {
                var prefix = "Quest" + quest.id + ".";
                var stage = 0;
                foreach (var tag in structures.Where(t => t.questTags != null).SelectMany(t => t.questTags))
                    if (tag.StartsWith(prefix, StringComparison.Ordinal) && Regex.Match(tag, @"\.stageStructure\.(\d+)") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var index))
                        stage = Math.Max(stage, index + 1);
                row.VoidAwakeningStage = stage;
            }
            return row;
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
