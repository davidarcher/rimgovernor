#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeResearchObservationTools
    {
        private const string ToolName = "rimgovernor/observations_read_research";
        [Tool(ToolName, Title = "Read typed research", Description = "Bounded research projects, native requirement gates and optional bench/researcher capability. Reads existing progress and slots without initializing saved research state. No research selection or UI changes.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ResearchReply; no invented CAS snapshot or frozen cursor.", Always = true)]
        public async Task<object> ReadResearch(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ResearchRequest string.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ResearchRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ResearchReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ResearchReply { Failure = error });
                try
                {
                    var manager = Find.ResearchManager;
                    var player = Faction.OfPlayerSilentFail;
                    if (manager == null || player?.def == null)
                        return ProtoBoundary.Encode(new Obs.ResearchReply { Unavailable = Missing(Common.UnavailableReason.NativeComponentMissing, "Research manager or player faction is unavailable.") });
                    return Encode(new Obs.ResearchReply { Observed = Read(parsed, context, map, manager, player) });
                }
                catch (ReadLimit e) { return ProtoBoundary.Encode(new Obs.ResearchReply { Unavailable = Missing(Common.UnavailableReason.LimitExceeded, e.Message) }); }
                catch (StaleCursor) { return ProtoBoundary.Encode(new Obs.ResearchReply { Unavailable = Missing(Common.UnavailableReason.LimitExceeded, "Research cursor is stale or does not match this query.") }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.ResearchReply { Unavailable = Missing(Common.UnavailableReason.ReadFailed, "Research state or required native eligibility facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool Validate(Obs.ResearchRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity and page limit 1..256 required; cursor must fit the caller's current filters.");
            return request?.Scope?.ExpectedIdentity != null
                && (request.Page == null || (!request.Page.HasLimit || request.Page.Limit >= 1 && request.Page.Limit <= 256)
                    && (!request.Page.HasCursor || request.Page.Cursor.Length <= 4096))
                && (!request.HasNameContains || request.NameContains.Length == 0 || ProtoBoundary.IsIdentifier(request.NameContains));
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
            var progress = Backing<Dictionary<ResearchProjectDef, float>>(manager, "progress");
            var knowledge = Backing<Dictionary<ResearchProjectDef, float>>(manager, "anomalyKnowledge");
            var anomaly = ModsConfig.AnomalyActive;
            var current = manager.GetProject(); // Null-category overload reads only currentProj.
            var selected = new HashSet<ResearchProjectDef>();
            var snapshot = new Obs.ResearchSnapshot { Context = context, AnomalyActive = anomaly, PlayerTechLevel = Id(player.def.techLevel.ToString()) };
            var ordinary = new Obs.ResearchSlot();
            if (current != null) { ordinary.CurrentProject = Id(current.defName); selected.Add(current); }
            snapshot.Slots.Add(ordinary);
            if (anomaly)
            {
                var field = typeof(ResearchManager).GetField("currentAnomalyKnowledgeProjects", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null || field.FieldType != typeof(List<ResearchManager.KnowledgeCategoryProject>)) throw new InvalidOperationException();
                var slots = (List<ResearchManager.KnowledgeCategoryProject>?)field.GetValue(manager);
                var categories = DefDatabase<KnowledgeCategoryDef>.AllDefsListForReading;
                Bound(categories.Count);
                foreach (var category in categories.OrderBy(d => d.defName, StringComparer.Ordinal))
                {
                    var matches = slots?.Where(s => s.category == category).ToList();
                    if (matches != null && matches.Count > 1) throw new InvalidOperationException();
                    var project = matches?.SingleOrDefault()?.project;
                    var slot = new Obs.ResearchSlot { Category = Id(category.defName) };
                    if (project != null) { slot.CurrentProject = Id(project.defName); selected.Add(project); }
                    snapshot.Slots.Add(slot);
                }
                if (slots != null && slots.Any(s => s == null || !categories.Contains(s.category))) throw new InvalidOperationException();
            }
            var definitions = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            if (definitions.Count > 65536) throw new ReadLimit("Research definition census exceeds 65536.");
            var filtered = 0;
            var built = new List<Obs.ResearchProject>();
            foreach (var def in definitions.OrderBy(d => d.defName, StringComparer.Ordinal))
            {
                var points = Progress(def, progress, knowledge, anomaly);
                var cost = def.Cost;
                var finished = Finished(points, cost);
                var hidden = !finished && anomaly && Find.EntityCodex.Hidden(def);
                if (hidden || finished && !request.IncludeFinished || request.HasNameContains && request.NameContains.Length != 0
                    && def.defName.IndexOf(request.NameContains, StringComparison.OrdinalIgnoreCase) < 0
                    && (def.label ?? "").IndexOf(request.NameContains, StringComparison.OrdinalIgnoreCase) < 0) { filtered++; continue; }
                var row = Project(def, manager, progress, knowledge, anomaly, player, points, cost, finished, selected.Contains(def));
                if (!finished && !row.CanStart && !request.IncludeLocked) { filtered++; continue; }
                if (request.IncludeUnlocks)
                {
                    var unlocks = def.UnlockedDefs; Bound(unlocks.Count);
                    foreach (var unlocked in unlocks)
                    {
                        var item = new Obs.ResearchUnlock { DefName = Id(unlocked.defName), NativeType = Id(unlocked.GetType().Name) };
                        if (unlocked.label != null) item.Label = PlacementPreviewOperation.Diagnostic(unlocked.label);
                        row.Unlocks.Add(item);
                    }
                    row.UnlocksCompleteness = Complete(unlocks.Count, 0);
                }
                built.Add(row);
            }
            var seed = QuerySeed(request);
            var afterCursor = built;
            if (request.Page != null && request.Page.HasCursor && request.Page.Cursor.Length != 0)
            {
                if (!NativeObservationSnapshot.Cursor.TryDecode(context.Identity, seed, request.Page.Cursor, out var after))
                    throw new StaleCursor();
                afterCursor = built.Where(p => string.CompareOrdinal(p.Project.DefName, after) > 0).ToList();
            }
            var limit = request.Page?.HasLimit == true ? (int)request.Page.Limit : 256;
            var page = afterCursor.Take(limit).ToList();
            if (page.Count > 256) throw new ReadLimit("Matched research projects exceed page limit; narrow filters.");
            var truncated = afterCursor.Count > page.Count;
            snapshot.Projects.Add(page);
            snapshot.Completeness = Complete(page.Count, filtered);
            snapshot.Completeness.Page.Complete = !truncated;
            if (truncated) snapshot.Completeness.Page.NextCursor = NativeObservationSnapshot.Cursor.Encode(context.Identity, seed, page[page.Count-1].Project.DefName);
            if (request.IncludeCapability) Capability(snapshot, map);
            snapshot.Snapshot = Token(context, snapshot);
            return snapshot;
        }

        private static Obs.ResearchProject Project(ResearchProjectDef def, ResearchManager manager,
            IDictionary<ResearchProjectDef, float> progress, IDictionary<ResearchProjectDef, float> knowledge,
            bool anomaly, Faction player, float points, float cost, bool finished, bool current)
        {
            var factor = def.CostFactor(player.def.techLevel); Number(factor); Number(cost * factor); Number(def.baseCost);
            var row = new Obs.ResearchProject { Project = Definition(def), TechLevel = Id(def.techLevel.ToString()),
                BaseCost = def.baseCost, ApparentCost = cost * factor, CostFactor = factor, Progress = points, Finished = finished, Current = current };
            if (cost > 0) row.ProgressFraction = Fraction(points, cost);
            else row.Issues.Add(Issue("progress_fraction", "Zero-cost project has no finite native progress fraction."));
            if (def.tab != null) row.Tab = Id(def.tab.defName);
            if (def.knowledgeCategory != null) row.Category = Id(def.knowledgeCategory.defName);
            foreach (var prerequisite in def.prerequisites ?? new List<ResearchProjectDef>())
            {
                row.Prerequisites.Add(Id(prerequisite.defName));
                if (!Finished(Progress(prerequisite, progress, knowledge, anomaly), prerequisite.Cost)) row.LockReasons.Add("prerequisite:" + Id(prerequisite.defName));
            }
            foreach (var prerequisite in def.hiddenPrerequisites ?? new List<ResearchProjectDef>())
            {
                row.HiddenPrerequisites.Add(Id(prerequisite.defName));
                if (!Finished(Progress(prerequisite, progress, knowledge, anomaly), prerequisite.Cost)) row.LockReasons.Add("hidden_prerequisite:" + Id(prerequisite.defName));
            }
            Bound(row.Prerequisites.Count); Bound(row.HiddenPrerequisites.Count);
            var applied = manager.GetTechprints(def); var needed = def.TechprintCount;
            if (applied < 0 || needed < 0) throw new InvalidOperationException();
            row.TechprintsApplied = (uint)applied; row.TechprintsNeeded = (uint)needed;
            if (applied < needed) row.LockReasons.Add("techprints");
            if (def.requiredResearchBuilding != null)
            {
                row.RequiredBuilding = Id(def.requiredResearchBuilding.defName);
                if (!def.PlayerHasAnyAppropriateResearchBench) row.LockReasons.Add("research_building_or_facilities");
            }
            foreach (var facility in def.requiredResearchFacilities ?? new List<ThingDef>()) row.RequiredFacilities.Add(Id(facility.defName));
            Bound(row.RequiredFacilities.Count);
            if (!def.PlayerMechanitorRequirementMet) row.LockReasons.Add("mechanitor");
            if (!def.AnalyzedThingsRequirementsMet) row.LockReasons.Add("analysis");
            if (!def.InspectionRequirementsMet) row.LockReasons.Add("inspection");
            if (finished) row.LockReasons.Add("finished");
            // Same predicates as native CanStartNow, with dictionary-only progress reads.
            row.CanStart = row.LockReasons.Count == 0; row.Available = row.CanStart;
            Bound(row.LockReasons.Count);
            return row;
        }

        private static void Capability(Obs.ResearchSnapshot snapshot, Map map)
        {
            var benches = map.listerBuildings.allBuildingsColonist.OfType<Building_ResearchBench>().ToList(); Bound(benches.Count);
            foreach (var bench in benches)
            {
                var row = new Obs.ResearchBench { Building = NativeBuildingObservationTools.Project(bench) };
                var affected = bench.GetComp<CompAffectedByFacilities>();
                if (affected != null)
                {
                    var facilities = affected.LinkedFacilitiesListForReading; Bound(facilities.Count);
                    foreach (var facility in facilities) row.Facilities.Add(new Obs.ResearchFacility { Definition = Definition(facility.def), Active = affected.IsFacilityActive(facility) });
                }
                snapshot.Benches.Add(row);
            }
            var pawns = map.mapPawns.FreeColonistsSpawned; Bound(pawns.Count);
            if (WorkTypeDefOf.Research == null || SkillDefOf.Intellectual == null) throw new InvalidOperationException();
            foreach (var pawn in pawns)
            {
                var row = new Obs.Researcher { Pawn = new Obs.EntityRef { Id = Id(pawn.GetUniqueLoadID()), DefName = Id(pawn.def.defName),
                    MapId = map.uniqueID, Position = new Common.Cell { X = pawn.Position.x, Z = pawn.Position.z } } };
                var settings = pawn.workSettings;
                row.EverWork = settings != null && settings.EverWork;
                row.Disabled = pawn.WorkTypeIsDisabled(WorkTypeDefOf.Research);
                if (row.EverWork && !row.Disabled) { row.Priority = settings!.GetPriority(WorkTypeDefOf.Research); row.Active = row.Priority > 0; }
                else row.Active = false;
                var skill = pawn.skills?.skills?.SingleOrDefault(s => s.def == SkillDefOf.Intellectual);
                if (skill != null) row.Intellectual = skill.Level;
                snapshot.Researchers.Add(row);
            }
        }
        internal static object Encode(Obs.ResearchReply reply)
        {
            if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) > 1024 * 1024)
                return ProtoBoundary.Encode(new Obs.ResearchReply { Unavailable = Missing(Common.UnavailableReason.LimitExceeded, "Research reply exceeds 1 MiB.") });
            return ProtoBoundary.Encode(reply);
        }
        private static void Number(double value) { if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) throw new InvalidOperationException("Invalid research quantity."); }
        private static void Bound(int count) { if (count > 256) throw new ReadLimit("Research child collection exceeds 256."); }
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Invalid research identifier.");
        private static Obs.DefinitionRef Definition(Def def)
        {
            var value = new Obs.DefinitionRef { DefName = Id(def.defName) };
            if (def.label != null) value.Label = PlacementPreviewOperation.Diagnostic(def.label);
            return value;
        }
        private static Common.Unavailable Missing(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.ReadIssue Issue(string field, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Missing(Common.UnavailableReason.ReadFailed, detail) };
        private static Obs.Completeness Complete(int count, int filtered) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = (ulong)filtered, Unreadable = 0 };
        private static string QuerySeed(Obs.ResearchRequest request) => string.Join("",
            request.IncludeLocked, request.IncludeFinished, request.IncludeUnlocks, request.IncludeCapability, request.NameContains ?? "");
        // Stateless hash over the fields this reply actually returned; recomputed
        // fresh each call, same pattern as NativeObservationSnapshot's other tokens.
        private static Obs.SnapshotRef Token(Common.ObservationContext context, Obs.ResearchSnapshot snapshot)
            => NativeObservationSnapshot.Snapshot("research", context, "research-manager", w => {
                w.Write(snapshot.AnomalyActive); w.Write(snapshot.PlayerTechLevel ?? "");
                foreach (var slot in snapshot.Slots) { w.Write(slot.Category ?? ""); w.Write(slot.CurrentProject ?? ""); }
                foreach (var project in snapshot.Projects.OrderBy(p => p.Project.DefName, StringComparer.Ordinal))
                { w.Write(project.Project.DefName); w.Write(project.Progress); w.Write(project.Finished); w.Write(project.Current); }
            });
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) { } }
        private sealed class StaleCursor : Exception { }
    }
}
