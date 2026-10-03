#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Anomaly facts (#1737): the static defs of the definition catalog
    // (entity and knowledge categories, codex entries, entity, studiable and
    // holder thing defs, anomaly incidents) and the pawn, building and thing
    // row blocks (entity state, held state, study state, containment
    // strength). Everything is read from the game defs and objects, never
    // from name lists, and is absent without Anomaly.
    internal static class NativeAnomalyFacts
    {
        private static string? Id(string? value) => value != null && ProtoBoundary.IsIdentifier(value) ? value : null;
        private static string Label(Def def) => PlacementPreviewOperation.Diagnostic(def.label ?? "");
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static IEnumerable<T> Sorted<T>(IEnumerable<T> defs) where T : Def =>
            defs.Where(d => ProtoBoundary.IsIdentifier(d.defName)).OrderBy(d => d.defName, StringComparer.Ordinal);
        private static IEnumerable<string> Names(IEnumerable<Def>? defs) =>
            (defs ?? Enumerable.Empty<Def>()).Select(d => Id(d?.defName)).Where(n => n != null).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal);

        private static Obs.ReadIssue Failed(string field, Exception ex) => new Obs.ReadIssue { Field = field,
            Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic(ex.Message) } };

        internal static Obs.AnomalyCatalog? Catalog()
        {
            if (!ModsConfig.AnomalyActive) return null;
            var catalog = new Obs.AnomalyCatalog();
            foreach (var def in Sorted(DefDatabase<EntityCategoryDef>.AllDefsListForReading))
                catalog.EntityCategories.Add(new Obs.EntityCategoryRow { DefName = def.defName, Label = Label(def), ListOrder = def.listOrder });
            foreach (var def in Sorted(DefDatabase<KnowledgeCategoryDef>.AllDefsListForReading))
            {
                var row = new Obs.KnowledgeCategoryRow { DefName = def.defName, Label = Label(def) };
                if (Id(def.overflowCategory?.defName) is string overflow) row.OverflowCategory = overflow;
                catalog.KnowledgeCategories.Add(row);
            }
            foreach (var def in Sorted(DefDatabase<EntityCodexEntryDef>.AllDefsListForReading)) catalog.CodexEntries.Add(Codex(def));
            foreach (var def in Sorted(DefDatabase<ThingDef>.AllDefsListForReading.Where(IsAnomalyThing))) catalog.Things.Add(ThingRow(def));
            foreach (var def in Sorted(DefDatabase<IncidentDef>.AllDefsListForReading.Where(i => i.IsAnomalyIncident))) catalog.Incidents.Add(Incident(def));
            return catalog;
        }

        private static bool IsAnomalyThing(ThingDef def) =>
            def.entityCodexEntry != null || def.race != null && def.race.IsAnomalyEntity || def.GetCompProperties<CompProperties_Studiable>() != null
            || def.GetCompProperties<CompProperties_HoldingPlatformTarget>() != null || def.GetCompProperties<CompProperties_EntityHolder>() != null;

        private static Obs.EntityCodexRow Codex(EntityCodexEntryDef def)
        {
            var row = new Obs.EntityCodexRow { DefName = def.defName, Label = Label(def), StartDiscovered = def.startDiscovered,
                DiscoveryType = NativeEnums.Discovery(def.discoveryType), AllowDiscoveryWhileMapGenerating = def.allowDiscoveryWhileMapGenerating,
                OrderInCategory = def.orderInCategory };
            if (Id(def.category?.defName) is string category) row.Category = category;
            row.LinkedThings.Add(Names(def.linkedThings));
            row.ProvocationIncidents.Add(Names(def.provocationIncidents));
            row.DiscoveredResearchProjects.Add(Names(def.discoveredResearchProjects));
            return row;
        }

        private static Obs.AnomalyThingRow ThingRow(ThingDef def)
        {
            var row = new Obs.AnomalyThingRow { DefName = def.defName, Label = Label(def), Entity = def.race?.IsAnomalyEntity ?? false };
            if (Id(def.entityCodexEntry?.defName) is string codex) row.CodexEntry = codex;
            if (def.race != null)
            {
                if (Id(def.race.knowledgeCategory?.defName) is string category) row.RaceKnowledgeCategory = category;
                row.RaceAnomalyKnowledge = def.race.anomalyKnowledge;
            }
            var minimum = def.statBases?.FirstOrDefault(s => s.stat == StatDefOf.MinimumContainmentStrength);
            if (minimum != null && Finite(minimum.value)) row.MinContainmentStrength = minimum.value;
            if (def.GetCompProperties<CompProperties_Studiable>() is CompProperties_Studiable study) row.Studiable = Studiable(study);
            if (def.GetCompProperties<CompProperties_HoldingPlatformTarget>() is CompProperties_HoldingPlatformTarget target)
            {
                var props = new Obs.HoldingTargetProps { LookForTargetOnEscape = target.lookForTargetOnEscape, CanBeExecuted = target.canBeExecuted,
                    GetsColdContainmentBonus = target.getsColdContainmentBonus };
                if (Id(target.heldPawnKind?.defName) is string kind) props.HeldPawnKind = kind;
                if (Finite(target.baseEscapeIntervalMtbDays)) props.BaseEscapeIntervalMtbDays = target.baseEscapeIntervalMtbDays;
                row.HoldingTarget = props;
            }
            if (def.GetCompProperties<CompProperties_EntityHolder>() is CompProperties_EntityHolder holder)
            {
                var props = new Obs.EntityHolderProps { Comp = Id(holder.GetType().Name) };
                if (Finite(holder.containmentFactor)) props.ContainmentFactor = holder.containmentFactor;
                row.Holder = props;
            }
            return row;
        }

        // CompProperties_Studiable keeps these four fields private and offers
        // no def-level accessor. Resolved on first catalog read; a game
        // version without a field fails the read naming it.
        private static class StudiableFields
        {
            private static readonly AccessTools.FieldRef<CompProperties_Studiable, float?> Knowledge = AccessTools.FieldRefAccess<CompProperties_Studiable, float?>("anomalyKnowledge");
            private static readonly AccessTools.FieldRef<CompProperties_Studiable, KnowledgeCategoryDef> Category = AccessTools.FieldRefAccess<CompProperties_Studiable, KnowledgeCategoryDef>("knowledgeCategory");
            private static readonly AccessTools.FieldRef<CompProperties_Studiable, bool> HoldingPlatform = AccessTools.FieldRefAccess<CompProperties_Studiable, bool>("requiresHoldingPlatform");
            private static readonly AccessTools.FieldRef<CompProperties_Studiable, bool> Imprisonment = AccessTools.FieldRefAccess<CompProperties_Studiable, bool>("requiresImprisonment");
            internal static float? AnomalyKnowledge(CompProperties_Studiable props) => Knowledge(props);
            internal static KnowledgeCategoryDef KnowledgeCategory(CompProperties_Studiable props) => Category(props);
            internal static bool RequiresHoldingPlatform(CompProperties_Studiable props) => HoldingPlatform(props);
            internal static bool RequiresImprisonment(CompProperties_Studiable props) => Imprisonment(props);
        }

        private static Obs.StudiableProps Studiable(CompProperties_Studiable props)
        {
            var row = new Obs.StudiableProps { Comp = Id(props.GetType().Name), FrequencyTicks = props.frequencyTicks, ShowToggleGizmo = props.showToggleGizmo,
                StudyEnabledByDefault = props.studyEnabledByDefault, CanBeActivityDeactivated = props.canBeActivityDeactivated,
                MinMonolithLevelForStudy = props.minMonolithLevelForStudy, RequiresHoldingPlatform = StudiableFields.RequiresHoldingPlatform(props),
                RequiresImprisonment = StudiableFields.RequiresImprisonment(props) };
            if (Finite(props.studyAmountToComplete)) row.StudyAmountToComplete = props.studyAmountToComplete;
            if (Finite(props.knowledgeFactorOutdoors)) row.KnowledgeFactorOutdoors = props.knowledgeFactorOutdoors;
            if (StudiableFields.AnomalyKnowledge(props) is float knowledge && Finite(knowledge)) row.AnomalyKnowledge = knowledge;
            if (Id(StudiableFields.KnowledgeCategory(props)?.defName) is string category) row.KnowledgeCategory = category;
            return row;
        }

        private static Obs.AnomalyIncidentRow Incident(IncidentDef def)
        {
            var row = new Obs.AnomalyIncidentRow { DefName = def.defName, Label = Label(def), Threat = def.category == IncidentCategoryDefOf.ThreatBig || def.category == IncidentCategoryDefOf.ThreatSmall,
                EarliestDay = def.earliestDay, PointsScaleable = def.pointsScaleable, MinAnomalyThreatLevel = def.minAnomalyThreatLevel };
            if (Id(def.category?.defName) is string category) row.Category = category;
            if (Id(def.workerClass?.Name) is string worker) row.Worker = worker;
            row.TargetTags.Add(Names(def.targetTags));
            if (Finite(def.baseChance)) row.BaseChance = def.baseChance;
            if (Finite(def.minThreatPoints)) row.MinThreatPoints = def.minThreatPoints;
            if (Id(def.codexEntry?.defName) is string codex) row.CodexEntry = codex;
            return row;
        }

        // The study block of a pawn or building with a CompStudiable; null for
        // one without.
        internal static Obs.StudyState? Study(Thing thing)
        {
            if (!ModsConfig.AnomalyActive || (thing as ThingWithComps)?.GetComp<CompStudiable>() is not CompStudiable study) return null;
            var state = new Obs.StudyState { StudyEnabled = study.studyEnabled, Completed = study.Completed, StudyInteractions = study.studyInteractions,
                EverStudiable = study.EverStudiable(), CurrentlyStudiable = study.CurrentlyStudiable() };
            if (Finite(study.ProgressPercent)) state.ProgressPercent = study.ProgressPercent;
            if (Finite(study.studyPoints)) state.StudyPoints = study.studyPoints;
            if (Finite(study.anomalyKnowledgeGained)) state.AnomalyKnowledgeGained = study.anomalyKnowledgeGained;
            if (Id(study.KnowledgeCategory?.defName) is string category) state.KnowledgeCategory = category;
            if (Finite(study.AnomalyKnowledge)) state.AnomalyKnowledge = study.AnomalyKnowledge;
            return state;
        }

        // The pawn row block; null without Anomaly. A sub-read that throws
        // leaves its block absent and adds a ReadIssue.
        internal static Obs.PawnAnomaly? Pawn(Pawn pawn)
        {
            if (!ModsConfig.AnomalyActive) return null;
            var row = new Obs.PawnAnomaly();
            try
            {
                // Read everything before setting anything: a failed read
                // leaves the whole group absent next to its ReadIssue.
                var entity = pawn.IsEntity;
                var mutant = pawn.IsMutant;
                var shambler = pawn.IsShambler;
                var minimum = entity ? pawn.GetStatValue(StatDefOf.MinimumContainmentStrength) : float.NaN;
                row.Entity = entity;
                row.Mutant = mutant;
                row.Shambler = shambler;
                if (entity && Finite(minimum)) row.MinContainmentStrength = minimum;
            }
            catch (Exception ex) { row.Issues.Add(Failed("entity", ex)); }
            try { row.HiddenFromPlayer = InvisibilityUtility.IsHiddenFromPlayer(pawn); }
            catch (Exception ex) { row.Issues.Add(Failed("hidden_from_player", ex)); }
            // The invoker is the pawn whose role in its lord's psychic ritual
            // is the ritual def's invoker role.
            try
            {
                row.PsychicRitualInvoker = pawn.GetLord()?.LordJob is LordJob_PsychicRitual ritual
                    && ritual.def is PsychicRitualDef_InvocationCircle circle
                    && ritual.assignments.RoleForPawn(pawn, true) == circle.InvokerRole;
            }
            catch (Exception ex) { row.Issues.Add(Failed("psychic_ritual_invoker", ex)); }
            try
            {
                row.MeleeOnly = pawn.CurrentEffectiveVerb is Verb verb && verb.IsMeleeAttack
                    && !(pawn.abilities?.AllAbilitiesForReading.Any(a => a.def.ai_IsOffensive) ?? false);
            }
            catch (Exception ex) { row.Issues.Add(Failed("melee_only", ex)); }
            try
            {
                if (pawn.GetComp<CompHoldingPlatformTarget>() is CompHoldingPlatformTarget target)
                {
                    var held = new Obs.HeldState { Held = target.CurrentlyHeldOnPlatform, Mode = NativeEnums.ContainmentMode(target.containmentMode),
                        Escaping = target.isEscaping, ExtractBioferrite = target.extractBioferrite, CanBeCaptured = target.CanBeCaptured };
                    if (target.HeldPlatform is Building_HoldingPlatform platform) held.Platform = NativeRef.Thing(platform);
                    row.Held = held;
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("held", ex)); }
            try { row.Study = Study(pawn); }
            catch (Exception ex) { row.Issues.Add(Failed("study", ex)); }
            return row;
        }

        // The building row block; null without Anomaly or when the building
        // is neither an entity holder nor studiable.
        internal static Obs.AnomalyBuilding? Building(Thing thing)
        {
            if (!ModsConfig.AnomalyActive) return null;
            var row = new Obs.AnomalyBuilding();
            try
            {
                if ((thing as ThingWithComps)?.GetComp<CompEntityHolder>() is CompEntityHolder holder)
                {
                    var state = new Obs.EntityHolderState { Available = holder.Available };
                    var strength = holder.ContainmentStrength;
                    if (Finite(strength)) state.ContainmentStrength = strength;
                    if (holder.HeldPawn is Pawn held) state.HeldPawn = NativeRef.Thing(held);
                    row.Holder = state;
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("holder", ex)); }
            try { row.Study = Study(thing); }
            catch (Exception ex) { row.Issues.Add(Failed("study", ex)); }
            return row.Holder == null && row.Study == null && row.Issues.Count == 0 ? null : row;
        }
    }
}
