#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeResearchObservationTools
    {
        private const string ToolName = "rimgovernor/observations_read_research";
        [Tool(ToolName, Title = "Read typed research", Description = "Complete research projects, native requirement gates and optional bench/researcher capability. Reads existing progress and slots without initializing saved research state. No research selection or UI changes.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ResearchReply; no invented CAS snapshot or frozen cursor.", Always = true)]
        public async Task<object> ReadResearch(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ResearchRequest string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, out Obs.ResearchRequest? parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.ResearchReply { Outcome = new Obs.ResearchReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleReadResearch(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Obs.ResearchReply HandleReadResearch(Obs.ResearchRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) return new Obs.ResearchReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                return new Obs.ResearchReply { Outcome = error };
            try
            {
                var manager = Find.ResearchManager;
                var player = Faction.OfPlayerSilentFail;
                if (manager == null || player?.def == null)
                    return new Obs.ResearchReply { Outcome = Missing(Common.UnavailableReason.NativeComponentMissing, "Research manager or player faction is unavailable.") };
                return new Obs.ResearchReply { Outcome = Read(parsed, context, map, manager, player) };
            }
            catch (Exception thrown) { ModLog.Error("observe", "Research read failed: " + thrown); return new Obs.ResearchReply { Outcome = Missing(Common.UnavailableReason.ReadFailed, PlacementPreviewOperation.Diagnostic("Research state or required native eligibility facts could not be read completely: " + thrown.GetType().Name + ": " + thrown.Message)) }; }
        }

        // The research state as a frame section: a read that throws fails the frame.
        internal static Obs.ResearchSnapshot Section(Map map, Obs.ResearchRequest request, Common.ObservationContext context)
        {
            var manager = Find.ResearchManager;
            var player = Faction.OfPlayerSilentFail;
            if (manager == null || player?.def == null) throw new InvalidOperationException("Research manager or player faction unavailable.");
            return Read(request, context, map, manager, player);
        }

        internal static bool Validate(Obs.ResearchRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity required; name_contains must be an identifier.");
            return request?.Scope?.ExpectedIdentity != null
                && (request.NameContains is null || (request.NameContains ?? "").Length == 0 || ProtoBoundary.IsIdentifier(request.NameContains));
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
        internal static double Fraction(double progress, double cost)
        {
            Number(progress); Number(cost);
            if (cost <= 0) throw new InvalidOperationException("Nonpositive research cost.");
            return Math.Min(1, progress / cost);
        }

        private static Obs.ResearchSnapshot Read(Obs.ResearchRequest request, Common.ObservationContext context, Map map, ResearchManager manager, Faction player)
        {
            var points = new Dictionary<string, double>(StringComparer.Ordinal);
            var progress = Backing<Dictionary<ResearchProjectDef, float>>(manager, "progress");
            var knowledge = Backing<Dictionary<ResearchProjectDef, float>>(manager, "anomalyKnowledge");
            var anomaly = ModsConfig.AnomalyActive;
            var current = manager.GetProject(); // Null-category overload reads only currentProj.
            var selected = new HashSet<ResearchProjectDef>();
            var snapshot = new Obs.ResearchSnapshot { Context = context, AnomalyActive = anomaly, PlayerTechLevel = Id(player.def.techLevel.ToString()) };
            var ordinary = new Obs.ResearchSlot();
            if (current != null) { ordinary = ordinary with { CurrentProject = Id(current.defName) }; selected.Add(current); }
            snapshot = snapshot with { Slots = [.. snapshot.Slots, ordinary] };
            if (anomaly)
            {
                var field = typeof(ResearchManager).GetField("currentAnomalyKnowledgeProjects", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null || field.FieldType != typeof(List<ResearchManager.KnowledgeCategoryProject>)) throw new InvalidOperationException();
                var slots = (List<ResearchManager.KnowledgeCategoryProject>?)field.GetValue(manager);
                var categories = DefDatabase<KnowledgeCategoryDef>.AllDefsListForReading;
                foreach (var category in categories.OrderBy(d => d.defName, StringComparer.Ordinal))
                {
                    var matches = slots?.Where(s => s.category == category).ToList();
                    if (matches != null && matches.Count > 1) throw new InvalidOperationException();
                    var project = matches?.SingleOrDefault()?.project;
                    var slot = new Obs.ResearchSlot { Category = Id(category.defName) };
                    if (project != null) { slot = slot with { CurrentProject = Id(project.defName) }; selected.Add(project); }
                    snapshot = snapshot with { Slots = [.. snapshot.Slots, slot] };
                }
                if (slots != null && slots.Any(s => s == null || !categories.Contains(s.category))) throw new InvalidOperationException();
            }
            var definitions = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            var filtered = 0;
            var built = new List<Obs.ResearchProject>();
            foreach (var def in definitions.OrderBy(d => d.defName, StringComparer.Ordinal))
            {
                var reached = Progress(def, progress, knowledge, anomaly);
                var cost = def.Cost;
                var finished = Finished(reached, cost);
                if (finished) snapshot = snapshot with { Finished = [.. snapshot.Finished, Id(def.defName)] };
                var hidden = !finished && anomaly && Find.EntityCodex.Hidden(def);
                if (request.ProgressOnly.GetValueOrDefault())
                {
                    // The current projects, with their lock reasons, and the
                    // started ones; the static fields are the catalog's. Every
                    // unfinished anomaly knowledge project is listed with its
                    // lock reasons too (hidden ones carry "hidden"): the
                    // knowledge slots are filled from this list.
                    var selectedRow = selected.Contains(def);
                    var knowledgeRow = anomaly && def.knowledgeCategory != null && !finished;
                    if (hidden && !knowledgeRow || finished && !selectedRow || !selectedRow && !knowledgeRow && reached <= 0) { filtered++; continue; }
                    var slim = Project(def, manager, progress, knowledge, anomaly, reached, cost, finished, selectedRow);
                    var kept = new Obs.ResearchProject { Project = slim.Project, Finished = slim.Finished, Current = slim.Current, Progress = slim.Progress };
                    if (slim.ProgressFraction is not null) kept = kept with { ProgressFraction = slim.ProgressFraction };
                    if (selectedRow || knowledgeRow) { kept = kept with { LockReasons = [.. kept.LockReasons, .. slim.LockReasons] }; kept = kept with { CanStart = slim.CanStart }; kept = kept with { Available = slim.Available }; kept = kept with { TechprintsApplied = slim.TechprintsApplied }; }
                    points[def.defName] = reached;
                    built.Add(kept);
                    continue;
                }
                if (hidden || finished && !request.IncludeFinished.GetValueOrDefault() || request.NameContains is not null && (request.NameContains ?? "").Length != 0
                    && def.defName.IndexOf((request.NameContains ?? ""), StringComparison.OrdinalIgnoreCase) < 0
                    && (def.label ?? "").IndexOf((request.NameContains ?? ""), StringComparison.OrdinalIgnoreCase) < 0) { filtered++; continue; }
                var row = Project(def, manager, progress, knowledge, anomaly, reached, cost, finished, selected.Contains(def));
                points[def.defName] = reached;
                if (!finished && !row.CanStart.GetValueOrDefault() && !request.IncludeLocked.GetValueOrDefault()) { filtered++; continue; }
                built.Add(row);
            }
            snapshot = snapshot with { Projects = [.. snapshot.Projects, .. built] };
            snapshot = snapshot with { Completeness = Complete(built.Count, filtered) };
            if (request.IncludeCapability.GetValueOrDefault()) snapshot = Capability(snapshot, map);
            snapshot = snapshot with { Snapshot = Token(context, snapshot, points) };
            return snapshot;
        }

        private static Obs.ResearchProject Project(ResearchProjectDef def, ResearchManager manager,
            IDictionary<ResearchProjectDef, float> progress, IDictionary<ResearchProjectDef, float> knowledge,
            bool anomaly, float points, float cost, bool finished, bool current)
        {
            var row = new Obs.ResearchProject { Project = Definition(def), Finished = finished, Current = current };
            Number(cost); Number(def.baseCost);
            row = row with { Progress = points };
            if (cost > 0) row = row with { ProgressFraction = Fraction(points, cost) };
            else row = row with { Issues = [.. row.Issues, Issue("progress_fraction", "Zero-cost project has no finite native progress fraction.")] };
            foreach (var prerequisite in def.prerequisites ?? new List<ResearchProjectDef>())
                if (!Finished(Progress(prerequisite, progress, knowledge, anomaly), prerequisite.Cost)) row = row with { LockReasons = [.. row.LockReasons, "prerequisite:" + Id(prerequisite.defName)] };
            foreach (var prerequisite in def.hiddenPrerequisites ?? new List<ResearchProjectDef>())
                if (!Finished(Progress(prerequisite, progress, knowledge, anomaly), prerequisite.Cost)) row = row with { LockReasons = [.. row.LockReasons, "hidden_prerequisite:" + Id(prerequisite.defName)] };
            var applied = manager.GetTechprints(def); var needed = def.TechprintCount;
            if (applied < 0 || needed < 0) throw new InvalidOperationException();
            row = row with { TechprintsApplied = (uint)applied };
            if (applied < needed) row = row with { LockReasons = [.. row.LockReasons, "techprints"] };
            // Native CanStartNow only demands a bench for a project that names
            // one, yet no project progresses without a bench the researcher can
            // work at: the lock is reported whenever none stands.
            // A knowledge project accrues from study, not bench work, so it
            // needs a bench only when it names one (native CanStartNow).
            if ((def.knowledgeCategory == null || def.requiredResearchBuilding != null) && !def.PlayerHasAnyAppropriateResearchBench) row = row with { LockReasons = [.. row.LockReasons, "research_building_or_facilities"] };
            // The entity codex hides a project until its entity is discovered
            // (or the monolith reaches level 1); native CanStartNow refuses it.
            if (anomaly && !finished && Find.EntityCodex.Hidden(def)) row = row with { LockReasons = [.. row.LockReasons, "hidden"] };
            if (!def.PlayerMechanitorRequirementMet) row = row with { LockReasons = [.. row.LockReasons, "mechanitor"] };
            if (!def.AnalyzedThingsRequirementsMet) row = row with { LockReasons = [.. row.LockReasons, "analysis"] };
            if (!def.InspectionRequirementsMet) row = row with { LockReasons = [.. row.LockReasons, "inspection"] };
            if (finished) row = row with { LockReasons = [.. row.LockReasons, "finished"] };
            // Native CanStartNow's predicates with dictionary-only progress reads,
            // plus the bench: a selectable project nobody can research is not
            // available to the controller.
            row = row with { CanStart = row.LockReasons.Count == 0 }; row = row with { Available = row.CanStart };
            return row;
        }

        private static Obs.ResearchSnapshot Capability(Obs.ResearchSnapshot snapshot, Map map)
        {
            var benches = map.listerBuildings.allBuildingsColonist.OfType<Building_ResearchBench>().ToList(); 
            foreach (var bench in benches)
            {
                var row = new Obs.ResearchBench { Building = NativeBuildingObservationTools.Ref(bench) };
                var affected = bench.GetComp<CompAffectedByFacilities>();
                if (affected != null)
                {
                    var facilities = affected.LinkedFacilitiesListForReading; 
                    foreach (var facility in facilities) row = row with { Facilities = [.. row.Facilities, new Obs.ResearchFacility { Definition = Definition(facility.def), Active = affected.IsFacilityActive(facility) }] };
                }
                snapshot = snapshot with { Benches = [.. snapshot.Benches, row] };
            }
            var pawns = map.mapPawns.FreeColonistsSpawned; 
            if (WorkTypeDefOf.Research == null || SkillDefOf.Intellectual == null) throw new InvalidOperationException();
            foreach (var pawn in pawns)
            {
                var row = new Obs.Researcher { Pawn = NativeRef.Thing(pawn) };
                var settings = pawn.workSettings;
                row = row with { EverWork = settings != null && settings.EverWork };
                row = row with { Disabled = pawn.WorkTypeIsDisabled(WorkTypeDefOf.Research) };
                if (row.EverWork.GetValueOrDefault() && !row.Disabled.GetValueOrDefault()) { row = row with { Priority = settings!.GetPriority(WorkTypeDefOf.Research) }; row = row with { Active = row.Priority.GetValueOrDefault() > 0 }; }
                else row = row with { Active = false };
                var skill = pawn.skills?.skills?.SingleOrDefault(s => s.def == SkillDefOf.Intellectual);
                if (skill != null) row = row with { Intellectual = skill.Level };
                snapshot = snapshot with { Researchers = [.. snapshot.Researchers, row] };
            }
            return snapshot;
        }
        private static void Number(double value) { if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) throw new InvalidOperationException("Invalid research quantity."); }
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Invalid research identifier.");
        private static Obs.DefinitionRef Definition(Def def) => new Obs.DefinitionRef { DefName = Id(def.defName) };
        private static Common.Unavailable Missing(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.ReadIssue Issue(string field, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Missing(Common.UnavailableReason.ReadFailed, detail) };
        private static Obs.Completeness Complete(int count, int filtered) => new Obs.Completeness { Filtered = (ulong)filtered };
        // Stateless hash over the fields this reply actually returned; recomputed
        // fresh each call, same pattern as NativeObservationSnapshot's other tokens.
        private static Obs.SnapshotRef Token(Common.ObservationContext context, Obs.ResearchSnapshot snapshot, IDictionary<string, double> points)
            => NativeObservationSnapshot.Snapshot("research", context, "research-manager", w => {
                w.Write(snapshot.AnomalyActive.GetValueOrDefault()); w.Write(snapshot.PlayerTechLevel ?? "");
                foreach (var name in snapshot.Finished) w.Write(name);
                foreach (var slot in snapshot.Slots) { w.Write(slot.Category ?? ""); w.Write(slot.CurrentProject ?? ""); }
                foreach (var project in snapshot.Projects.OrderBy(p => (p.Project?.DefName ?? ""), StringComparer.Ordinal))
                { w.Write((project.Project?.DefName ?? "")); w.Write(points[(project.Project?.DefName ?? "")]); w.Write(project.Finished.GetValueOrDefault()); w.Write(project.Current.GetValueOrDefault()); }
            });
    }
}
