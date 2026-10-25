using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    internal static class NativeResearchObservationTools
    {
        // On the game thread: the typed core of the tool, shared with the in-process facade. A refused request
        // or a research state that cannot be read completely throws.
        internal static RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot HandleReadResearch(RimGovernor.Brain.GameOrders.Observations.ResearchRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!GameThread.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out _, out var context, out var error))
                throw NativeFaults.Refused(error);
            var manager = Find.ResearchManager;
            if (manager == null || Faction.OfPlayerSilentFail?.def == null)
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NativeComponentMissing, Detail = "Research manager or player faction is unavailable." });
            try { return Read(parsed, context, manager); }
            catch (Exception thrown)
            {
                ModLog.Error("observe", "Research read failed: " + thrown);
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                    Detail = PlacementPreviewOperation.Diagnostic("Research state or required native eligibility facts could not be read completely: " + thrown.GetType().Name + ": " + thrown.Message) });
            }
        }

        // The research state as a frame section: a read that throws fails the frame.
        internal static RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot Section(Map map, RimGovernor.Brain.GameOrders.Observations.ResearchRequest request, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var manager = Find.ResearchManager;
            var player = Faction.OfPlayerSilentFail;
            if (manager == null || player?.def == null) throw new InvalidOperationException("Research manager or player faction unavailable.");
            return Read(request, context, manager);
        }

        internal static bool Validate(RimGovernor.Brain.GameOrders.Observations.ResearchRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Expected identity required; name_contains must be an identifier.");
            return request?.Scope?.ExpectedIdentity != null
                && (request.NameContains is null || request.NameContains.Length == 0 || GameThread.IsIdentifier(request.NameContains));
        }

        // These exact backing fields avoid GetProgress's saved zero insertion and
        // CurrentAnomalyKnowledgeProjects' saved slot initialization.
        internal static T Backing<T>(ResearchManager manager, string name) where T : class
        {
            var field = typeof(ResearchManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(T)) throw new InvalidOperationException("Research backing field unavailable.");
            return field.GetValue(manager) as T ?? throw new InvalidOperationException("Research backing state unavailable.");
        }
        internal static float Progress(ResearchProjectDef def, IDictionary<ResearchProjectDef, float> progress,
            IDictionary<ResearchProjectDef, float> knowledge, bool anomaly)
        {
            var source = def.baseCost > 0 ? progress : anomaly && def.knowledgeCost > 0 ? knowledge : null;
            var value = source != null && source.TryGetValue(def, out var found) ? found : 0;
            Number(value); return value;
        }
        internal static bool Finished(double progress, double cost) { Number(progress); Number(cost); return progress >= cost; }
        // The researched share of the cost; a zero-cost project has nothing left to research (Finished holds).
        internal static double Fraction(double progress, double cost)
        {
            Number(progress); Number(cost);
            return cost <= 0 ? 1 : Math.Min(1, progress / cost);
        }

        private static RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot Read(RimGovernor.Brain.GameOrders.Observations.ResearchRequest request, RimGovernor.Brain.GameFacts.Common.ObservationContext context, ResearchManager manager)
        {
            var progress = Backing<Dictionary<ResearchProjectDef, float>>(manager, "progress");
            var knowledge = Backing<Dictionary<ResearchProjectDef, float>>(manager, "anomalyKnowledge");
            var anomaly = ModsConfig.AnomalyActive;
            var current = manager.GetProject(); // Null-category overload reads only currentProj.
            var selected = new HashSet<ResearchProjectDef>();
            var slots = new List<RimGovernor.Brain.GameFacts.Observations.ResearchSlot>();
            if (current != null) selected.Add(current);
            slots.Add(new RimGovernor.Brain.GameFacts.Observations.ResearchSlot { Category = "", CurrentProject = current != null ? Id(current.defName) : "" });
            if (anomaly)
            {
                var field = typeof(ResearchManager).GetField("currentAnomalyKnowledgeProjects", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null || field.FieldType != typeof(List<ResearchManager.KnowledgeCategoryProject>)) throw new InvalidOperationException();
                var saved = (List<ResearchManager.KnowledgeCategoryProject>?)field.GetValue(manager);
                var categories = DefDatabase<KnowledgeCategoryDef>.AllDefsListForReading;
                foreach (var category in categories.OrderBy(d => d.defName, StringComparer.Ordinal))
                {
                    var matches = saved?.Where(s => s.category == category).ToList();
                    if (matches != null && matches.Count > 1) throw new InvalidOperationException();
                    var project = matches?.SingleOrDefault()?.project;
                    if (project != null) selected.Add(project);
                    slots.Add(new RimGovernor.Brain.GameFacts.Observations.ResearchSlot { Category = Id(category.defName), CurrentProject = project != null ? Id(project.defName) : "" });
                }
                if (saved != null && saved.Any(s => s == null || !categories.Contains(s.category))) throw new InvalidOperationException();
            }
            var finishedNames = new List<string>();
            var built = new List<RimGovernor.Brain.GameFacts.Observations.ResearchProject>();
            foreach (var def in DefDatabase<ResearchProjectDef>.AllDefsListForReading.OrderBy(d => d.defName, StringComparer.Ordinal))
            {
                var reached = Progress(def, progress, knowledge, anomaly);
                var cost = def.Cost;
                var finished = Finished(reached, cost);
                if (finished) finishedNames.Add(Id(def.defName));
                var hidden = !finished && anomaly && Find.EntityCodex.Hidden(def);
                var isSelected = selected.Contains(def);
                if (request.ProgressOnly)
                {
                    // The current projects, every unfinished anomaly knowledge
                    // project (the knowledge slots are filled from this list)
                    // and every started one, hidden or not, each with its lock
                    // reasons; the static fields are the catalog's. A project
                    // left out is unfinished with no progress, or finished.
                    var knowledgeRow = anomaly && def.knowledgeCategory != null && !finished;
                    if (finished && !isSelected || !isSelected && !knowledgeRow && reached <= 0) continue;
                    built.Add(Project(def, manager, progress, knowledge, anomaly, reached, cost, finished, isSelected));
                    continue;
                }
                if (hidden || finished && !request.IncludeFinished || request.NameContains is { Length: > 0 } name
                    && def.defName.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0
                    && (def.label ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var row = Project(def, manager, progress, knowledge, anomaly, reached, cost, finished, isSelected);
                if (!finished && row.LockReasons.Count != 0 && !request.IncludeLocked) continue;
                built.Add(row);
            }
            return new RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot { Context = context, Slots = [.. slots], Projects = [.. built], Finished = [.. finishedNames] };
        }

        private static RimGovernor.Brain.GameFacts.Observations.ResearchProject Project(ResearchProjectDef def, ResearchManager manager,
            IDictionary<ResearchProjectDef, float> progress, IDictionary<ResearchProjectDef, float> knowledge,
            bool anomaly, float points, float cost, bool finished, bool current)
        {
            Number(cost); Number(def.baseCost);
            var locks = new List<string>();
            foreach (var prerequisite in def.prerequisites ?? new List<ResearchProjectDef>())
                if (!Finished(Progress(prerequisite, progress, knowledge, anomaly), prerequisite.Cost)) locks.Add("prerequisite:" + Id(prerequisite.defName));
            foreach (var prerequisite in def.hiddenPrerequisites ?? new List<ResearchProjectDef>())
                if (!Finished(Progress(prerequisite, progress, knowledge, anomaly), prerequisite.Cost)) locks.Add("hidden_prerequisite:" + Id(prerequisite.defName));
            var applied = manager.GetTechprints(def); var needed = def.TechprintCount;
            if (applied < 0 || needed < 0) throw new InvalidOperationException();
            if (applied < needed) locks.Add("techprints");
            // Native CanStartNow only demands a bench for a project that names
            // one, yet no project progresses without a bench the researcher can
            // work at: the lock is reported whenever none stands.
            // A knowledge project accrues from study, not bench work, so it
            // needs a bench only when it names one (native CanStartNow).
            if ((def.knowledgeCategory == null || def.requiredResearchBuilding != null) && !def.PlayerHasAnyAppropriateResearchBench) locks.Add("research_building_or_facilities");
            // The entity codex hides a project until its entity is discovered
            // (or the monolith reaches level 1); native CanStartNow refuses it.
            if (anomaly && !finished && Find.EntityCodex.Hidden(def)) locks.Add("hidden");
            if (!def.PlayerMechanitorRequirementMet) locks.Add("mechanitor");
            if (!def.AnalyzedThingsRequirementsMet) locks.Add("analysis");
            if (!def.InspectionRequirementsMet) locks.Add("inspection");
            if (finished) locks.Add("finished");
            return new RimGovernor.Brain.GameFacts.Observations.ResearchProject
            {
                Project = Definition(def),
                ProgressFraction = Fraction(points, cost),
                Finished = finished,
                Current = current,
                LockReasons = [.. locks],
            };
        }

        private static void Number(double value) { if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) throw new InvalidOperationException("Invalid research quantity."); }
        private static string Id(string value) => GameThread.IsIdentifier(value) ? value : throw new InvalidOperationException("Invalid research identifier.");
        private static RimGovernor.Brain.GameFacts.Observations.DefinitionRef Definition(Def def) => new RimGovernor.Brain.GameFacts.Observations.DefinitionRef { DefName = Id(def.defName) };
    }
}
