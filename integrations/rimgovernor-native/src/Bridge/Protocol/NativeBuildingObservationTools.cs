#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
    public sealed class NativeBuildingObservationTools
    {
        private const string ToolName = "rimgovernor/observations_list_buildings";

        [Tool(ToolName, Title = "Read typed buildings", Description = "Read complete bounded building, blueprint and frame facts including walls. Exact IDs/definitions, inclusive anchor region; defaults artificial/player-only. No CAS snapshots, detailed settings, bills, inspect text or power-network enumeration yet. changed_since_tick (reads without ids, def_names, statuses, damaged_below_fraction or region) lists the buildings whose row changed at or after that tick, counts the rest in unchanged and names the buildings removed since in removed_ids; an ask older than 2500 ticks is STALE and needs a full read. as_of_tick is always the context tick.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ListBuildingsReply. Unavailable replaces oversized collections; unsupported facts are explicit.", Always = true)]
        public async Task<object> ListBuildings(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ListBuildingsRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ListBuildingsRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Failure = error });
                try { return Encode(Read(map, parsed, context)); }
                catch (ReadLimit errorLimit) { return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, errorLimit.Message) }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        // Read is the list on the main thread under a validated identity: the
        // reply its tool encodes, and the section the bundle carries (#593).
        internal static Obs.ListBuildingsReply Read(Map map, Obs.ListBuildingsRequest parsed, Common.ObservationContext context)
        {
            try
            {
                if ((!parsed.HasPlayerOnly || parsed.PlayerOnly) && Faction.OfPlayerSilentFail == null)
                    return new Obs.ListBuildingsReply { Unavailable = Unavailable(Common.UnavailableReason.NativeComponentMissing, "Player faction unavailable.") };
                if (parsed.Region != null && (!NativeCell(parsed.Region.Minimum).InBounds(map) || !NativeCell(parsed.Region.Maximum).InBounds(map)))
                    return new Obs.ListBuildingsReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Region must be inside the current map.") };
                var source = Source(map, parsed.HasCategory && parsed.Category == "all");
                var matched = source.Where(t => Matches(t, parsed)).OrderBy(t => t.thingIDNumber).ToList();
                var seed = QuerySeed(parsed);
                // Entity tracking (issue #358) follows every unfiltered
                // read: a full one primes the shadow and sweeps the
                // removed, a changed_since one lists only the changed.
                var tracking = Unfiltered(parsed) ? EntityTracking.For(map, ToolName + parsed.PlayerOnly + (parsed.Category ?? "")) : null;
                if (parsed.HasChangedSinceTick && EntityTracking.Expired(parsed.ChangedSinceTick))
                    return new Obs.ListBuildingsReply { Unavailable = Unavailable(Common.UnavailableReason.Stale, "changed_since_tick is older than the tombstone window; read in full.") };
                var snapshot = new Obs.BuildingsSnapshot { Context = context, AsOfTick = context.Tick, Unchanged = 0,
                    NetworksCompleteness = new Obs.Completeness { Page = new Common.PageInfo { Complete = false } } };
                var rows = new Dictionary<string, Obs.BuildingState>();
                var listed = matched;
                if (parsed.HasChangedSinceTick)
                {
                    listed = new List<Thing>();
                    foreach (var thing in matched)
                    {
                        var row = Row(thing, context);
                        if (tracking!.Note(row.Building.Id, row) >= parsed.ChangedSinceTick) { rows[row.Building.Id] = row; listed.Add(thing); }
                        else snapshot.Unchanged++;
                    }
                }
                if (tracking != null)
                {
                    tracking.Sweep(new HashSet<string>(matched.Select(t => Id(t.GetUniqueLoadID()))));
                    if (parsed.HasChangedSinceTick) snapshot.RemovedIds.AddRange(tracking.RemovedSince(parsed.ChangedSinceTick));
                }
                var afterCursor = listed;
                if (parsed.Page != null && parsed.Page.HasCursor && parsed.Page.Cursor.Length != 0)
                {
                    if (!NativeObservationSnapshot.Cursor.TryDecode(context.Identity, seed, parsed.Page.Cursor, out var after))
                        return new Obs.ListBuildingsReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, "Building cursor is stale or does not match this query.") };
                    afterCursor = listed.Where(t => string.CompareOrdinal(Id(t.GetUniqueLoadID()), after) > 0).ToList();
                }
                var page = afterCursor.Take(Limit(parsed)).ToList();
                Require(page.Count <= 256, "Matched building collection exceeds page limit; narrow filters.");
                var truncated = afterCursor.Count > page.Count;
                snapshot.Completeness = Complete(page.Count, source.Count - matched.Count);
                snapshot.Completeness.Page.Complete = !truncated;
                if (truncated) snapshot.Completeness.Page.NextCursor = NativeObservationSnapshot.Cursor.Encode(context.Identity, seed, Id(page[page.Count-1].GetUniqueLoadID()));
                var cells = 0;
                foreach (var thing in page)
                {
                    var id = Id(thing.GetUniqueLoadID());
                    if (!rows.TryGetValue(id, out var row)) { row = Row(thing, context); tracking?.Note(id, row); }
                    cells = checked(cells + row.OccupiedCells.Count);
                    Require(cells <= 4096, "Complete building geometry exceeds 4096 cells.");
                    snapshot.Buildings.Add(row);
                }
                return new Obs.ListBuildingsReply { Observed = snapshot };
            }
            catch (ReadLimit errorLimit) { return new Obs.ListBuildingsReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, errorLimit.Message) }; }
            catch (Exception) { return new Obs.ListBuildingsReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Building facts could not be read completely.") }; }
        }

        // Row is one listed building as the read emits it: the projection
        // with its CAS snapshot and the settings row of the patchable kinds.
        private static Obs.BuildingState Row(Thing thing, Common.ObservationContext context)
        {
            var row = Project(thing);
            row.Snapshot = row.Construction != null
                ? NativeObservationSnapshot.Snapshot("building", context, row.Building.Id, w => {
                    w.Write(row.Status??""); w.Write(row.HitPoints); w.Write(row.Burning);
                    w.Write(row.Construction.PercentComplete); w.Write(row.Construction.ResourcesComplete);
                })
                : Token(thing, context);
            // Only the implemented PatchBuilding fields carry a
            // settings row with their own dedicated CAS snapshot:
            // target_temperature_c (NativeBuildingTemperature), a
            // humanlike bed's medical flag (NativeBedMedical), a
            // plant grower's crop (NativeGrowerCrop) and a claimable
            // building's faction (NativeClaimBuilding, #459). forbidden/
            // power/owner remain the "settings" unsupported issue below.
            var tempControl = thing.TryGetComp<CompTempControl>();
            if (tempControl != null)
                row.Settings = new Obs.BuildingSettings { Snapshot = NativeBuildingTemperature.Snapshot(thing, context), TargetTemperatureC = tempControl.targetTemperature };
            else if (NativeBedMedical.Eligible(thing))
                row.Settings = NativeBedMedical.Settings((Building_Bed)thing, context);
            else if (NativeGrowerCrop.Eligible(thing))
                row.Settings = NativeGrowerCrop.Settings((Building_PlantGrower)thing, context);
            else if (NativeClaimBuilding.Eligible(thing))
                row.Settings = NativeClaimBuilding.Settings((Building)thing, context);
            return row;
        }

        internal static bool Validate(Obs.ListBuildingsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope, exact bounded identifiers, supported filters and page limit1..256 are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            if (request.Page != null && (request.Page.HasLimit && (request.Page.Limit < 1 || request.Page.Limit > 256)
                || request.Page.HasCursor && request.Page.Cursor.Length > 4096)) return false;
            if (!Identifiers(request.Ids) || !Identifiers(request.DefNames) || request.Statuses.Count > 5
                || request.Statuses.Distinct(StringComparer.Ordinal).Count() != request.Statuses.Count
                || request.Statuses.Any(s => s != "all" && s != "built" && s != "blueprint" && s != "frame" && s != "pending")) return false;
            if (request.HasCategory && request.Category != "all" && request.Category != "artificial") return false;
            if (request.HasDamagedBelowFraction && (!Finite(request.DamagedBelowFraction) || request.DamagedBelowFraction < 0 || request.DamagedBelowFraction > 1)) return false;
            if (request.Region != null && (!CellPresent(request.Region.Minimum) || !CellPresent(request.Region.Maximum)
                || request.Region.Minimum.X > request.Region.Maximum.X || request.Region.Minimum.Z > request.Region.Maximum.Z)) return false;
            if (request.Inspect || request.BillIngredients)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Inspect strings and bill ingredient detail are not supported by this read adapter.");
                return false;
            }
            if (request.HasChangedSinceTick && (request.ChangedSinceTick < 0 || !Unfiltered(request)))
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "changed_since_tick needs a non-negative tick and a read without ids, def_names, statuses, damaged_below_fraction or region.");
                return false;
            }
            return true;
        }

        // Unfiltered is a read that enumerates every building of its category
        // and faction, the only shape whose tracker can tell a removed
        // building from a filtered one.
        private static bool Unfiltered(Obs.ListBuildingsRequest request) => request.Ids.Count == 0 && request.DefNames.Count == 0
            && request.Statuses.Count == 0 && !request.HasDamagedBelowFraction && request.Region == null;

        // Match the existing listing's native coverage without its aggregation,
        // safe-read defaults, fuzzy name matching or detailed-row truncation.
        private static List<Thing> Source(Map map, bool all)
        {
            var seen = new HashSet<Thing>();
            foreach (var group in new[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame,
                ThingRequestGroup.BuildingArtificial, ThingRequestGroup.PotentialBillGiver })
                foreach (var thing in map.listerThings.ThingsInGroup(group))
                    if (thing != null && !(thing is Pawn) && !(thing is Corpse) && thing.Spawned && thing.Map == map) seen.Add(thing);
            if (all) foreach (var thing in map.listerThings.AllThings)
                if (thing != null && thing.def.category == ThingCategory.Building && thing.Spawned && thing.Map == map) seen.Add(thing);
            return seen.ToList();
        }

        private static bool Matches(Thing thing, Obs.ListBuildingsRequest request)
        {
            if ((!request.HasPlayerOnly || request.PlayerOnly) && thing.Faction != Faction.OfPlayerSilentFail) return false;
            if (request.Ids.Count != 0 && !request.Ids.Contains(Id(thing.GetUniqueLoadID()))) return false;
            var built = thing is Blueprint_Install install ? InstallTarget(install).def : thing.def.entityDefToBuild;
            if (request.DefNames.Count != 0 && !request.DefNames.Contains(thing.def.defName)
                && (built == null || !request.DefNames.Contains(built.defName))) return false;
            var state = Status(thing);
            if (request.Statuses.Count != 0 && !request.Statuses.Contains("all") && !request.Statuses.Contains(state)
                && !(state != "built" && request.Statuses.Contains("pending"))) return false;
            if (request.Region != null && (thing.Position.x < request.Region.Minimum.X || thing.Position.x > request.Region.Maximum.X
                || thing.Position.z < request.Region.Minimum.Z || thing.Position.z > request.Region.Maximum.Z)) return false;
            if (request.HasDamagedBelowFraction && (!thing.def.useHitPoints || thing.MaxHitPoints <= 0
                || (double)thing.HitPoints / thing.MaxHitPoints >= request.DamagedBelowFraction)) return false;
            return true;
        }

        // Shared with NativeBedAssignOperations: AssignBed's bed precondition
        // reuses this same generic building CAS token (ReadBedTarget/bridge
        // treats a bed like any repairable building), not a bed-specific
        // occupancy token -- the previousBed field already guards the pawn's
        // prior ownership, so this only needs to catch hit points/status/fire
        // changes since the caller last observed.
        internal static Obs.SnapshotRef Token(Thing thing, Common.ObservationContext context) =>
            NativeObservationSnapshot.Snapshot("building", context, Id(thing.GetUniqueLoadID()), w => {
                w.Write(Status(thing) ?? ""); w.Write(thing.HitPoints); w.Write(thing.IsBurning());
            });

        internal static Obs.BuildingState Project(Thing thing)
        {
            if (!thing.Spawned || thing.Map == null) throw new InvalidOperationException("Building is not spawned.");
            var pending = thing is Blueprint || thing is Frame;
            var row = new Obs.BuildingState { Building = new Obs.EntityRef { Id = Id(thing.GetUniqueLoadID()),
                DefName = Id(thing.def.defName), Label = PlacementPreviewOperation.Diagnostic(thing.LabelCap), MapId = thing.Map.uniqueID,
                Position = Cell(thing.Position) }, Status = Status(thing), Rotation = Rotation(thing.Rotation),
                UsesHitPoints = thing.def.useHitPoints, Burning = thing.IsBurning() };
            var stuff = pending && thing is IConstructible constructible ? constructible.EntityToBuildStuff() : thing.Stuff;
            if (stuff != null) row.Stuff = Id(stuff.defName);
            else row.Issues.Add(Issue("stuff", Common.UnavailableReason.NotApplicable, "No material definition applies."));
            if (thing.Faction != null) row.FactionId = Id(thing.Faction.GetUniqueLoadID());
            else row.Issues.Add(Issue("faction_id", Common.UnavailableReason.NotApplicable, "Unowned native thing."));
            var rectangle = thing.OccupiedRect();
            Require((long)rectangle.Width * rectangle.Height <= 4096, "Building footprint exceeds 4096 cells.");
            foreach (var cell in rectangle.Cells)
            {
                if (!cell.InBounds(thing.Map)) throw new InvalidOperationException("Building geometry is outside its map.");
                row.OccupiedCells.Add(Cell(cell));
            }
            if (thing.def.useHitPoints)
            {
                if (thing.HitPoints < 0 || thing.MaxHitPoints <= 0) throw new InvalidOperationException("Invalid native hit points.");
                row.HitPoints = thing.HitPoints; row.MaxHitPoints = thing.MaxHitPoints;
            }
            else row.Issues.Add(Issue("hit_points", Common.UnavailableReason.NotApplicable, "This definition has no hit points."));
            if (pending)
            {
                var buildDef = thing is Blueprint_Install installation ? InstallTarget(installation).def
                    : thing.def.entityDefToBuild ?? throw new InvalidOperationException("Construction definition missing.");
                if (thing is Blueprint_Install) row.InstallOfDefName = Id(buildDef.defName);
                else row.BuildDefName = Id(buildDef.defName);
                row.Construction = Construction(thing, buildDef, stuff);
            }
            else row.Issues.Add(Issue("construction", Common.UnavailableReason.NotApplicable, "Completed building is not a construction site."));
            // "settings" is reported unsupported wholesale only when this thing
            // carries no settings row; a temp-controlled thing gets
            // target_temperature_c, a humanlike bed its medical flag and a
            // plant grower its crop, each with its own snapshot, filled in by
            // the caller below (see NativeBuildingTemperature, NativeBedMedical,
            // NativeGrowerCrop) -- forbidden/power/owner/forPrisoners remain
            // unimplemented either way.
            var fields = thing.TryGetComp<CompTempControl>() != null || NativeBedMedical.Eligible(thing) || NativeGrowerCrop.Eligible(thing) || NativeClaimBuilding.Eligible(thing)
                ? new[] { "service", "thermal_sides", "bills" }
                : new[] { "settings", "service", "thermal_sides", "bills" };
            foreach (var field in fields)
                row.Issues.Add(Issue(field, Common.UnavailableReason.Unsupported, "Typed fact or exact CAS snapshot producer is not implemented."));
            row.Issues.Add(Issue("inspect_text", Common.UnavailableReason.NotRequested, "Inspect strings are not requested."));
            return row;
        }

        private static Obs.ConstructionState Construction(Thing thing, BuildableDef definition, ThingDef? stuff)
        {
            var row = new Obs.ConstructionState();
            if (thing is Blueprint_Install)
            {
                // TotalMaterialCost on installation blueprints logs an error and
                // pauses the simulation. Native installation consumes no material.
                row.ResourcesComplete = true;
                row.Issues.Add(Issue("work", Common.UnavailableReason.Unsupported, "Installation work is not projected by this adapter."));
                row.Issues.Add(Issue("completable_ever", Common.UnavailableReason.Unsupported, "Completion eligibility is not evaluated."));
                return row;
            }
            if (!(thing is IConstructible construction)) throw new InvalidOperationException("Constructible unavailable.");
            if (thing is Frame frame)
            {
                row.TotalWork = Nonnegative(frame.WorkToBuild); row.WorkLeft = Nonnegative(frame.WorkLeft);
                row.PercentComplete = Fraction(frame.PercentComplete);
            }
            else
            {
                row.TotalWork = Nonnegative(definition.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff));
                row.WorkLeft = row.TotalWork; row.PercentComplete = 0;
            }
            var costs = construction.TotalMaterialCost() ?? throw new InvalidOperationException("Cost list unavailable.");
            Require(costs.Count <= 256, "Construction costs exceed 256 rows.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var complete = true;
            foreach (var cost in costs)
            {
                var name = Id(cost.thingDef.defName);
                if (!seen.Add(name) || cost.count < 0) throw new InvalidOperationException("Invalid native construction costs.");
                var needed = construction.ThingCountNeeded(cost.thingDef);
                var have = thing is Frame nativeFrame
                    ? nativeFrame.resourceContainer.TotalStackCountOfDef(cost.thingDef) : cost.count - needed;
                if (needed < 0 || have < 0) throw new InvalidOperationException("Invalid native construction quantities.");
                row.Resources.Add(new Obs.MaterialDeficit { DefName = name, Need = cost.count, Have = have, StillNeeded = needed });
                complete &= needed == 0;
            }
            row.ResourcesComplete = complete;
            row.Issues.Add(Issue("completable_ever", Common.UnavailableReason.Unsupported, "Completion eligibility is not evaluated."));
            return row;
        }

        private static string QuerySeed(Obs.ListBuildingsRequest request) => string.Join("",
            request.PlayerOnly, request.Category??"", request.DamagedBelowFraction,
            string.Join(",", request.Statuses.OrderBy(s=>s,StringComparer.Ordinal)),
            string.Join(",", request.DefNames.OrderBy(s=>s,StringComparer.Ordinal)),
            string.Join(",", request.Ids.OrderBy(s=>s,StringComparer.Ordinal)),
            request.Region == null ? "" : request.Region.Minimum.X+","+request.Region.Minimum.Z+"-"+request.Region.Maximum.X+","+request.Region.Maximum.Z);
        private static bool Identifiers(IEnumerable<string> values) => values.Count() <= 256
            && values.All(ProtoBoundary.IsIdentifier) && values.Distinct(StringComparer.Ordinal).Count() == values.Count();
        private static bool CellPresent(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ;
        private static IntVec3 NativeCell(Common.Cell cell) => new IntVec3(cell.X, 0, cell.Z);
        private static Common.Cell Cell(IntVec3 cell) => new Common.Cell { X = cell.x, Z = cell.z };
        private static int Limit(Obs.ListBuildingsRequest request) => request.Page?.HasLimit == true ? (int)request.Page.Limit : 256;
        private static string Status(Thing thing) => thing is Blueprint ? "blueprint" : thing is Frame ? "frame" : "built";
        private static Thing InstallTarget(Blueprint_Install install)
        {
            var held = install.MiniToInstallOrBuildingToReinstall;
            return (held is MinifiedThing mini ? mini.InnerThing : held) ?? throw new InvalidOperationException("Installation target unavailable.");
        }
        private static string Rotation(Rot4 rotation) => rotation == Rot4.North ? "North" : rotation == Rot4.East ? "East"
            : rotation == Rot4.South ? "South" : rotation == Rot4.West ? "West" : throw new InvalidOperationException("Invalid rotation.");
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native ID unavailable.");
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static double Nonnegative(double value) => Finite(value) && value >= 0 ? value : throw new InvalidOperationException("Invalid native amount.");
        private static double Fraction(double value) => Nonnegative(value) <= 1 ? value : throw new InvalidOperationException("Invalid native fraction.");
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
        private static Obs.Completeness Complete(int count, int filtered) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = (ulong)filtered, Unreadable = 0 };
        internal static object Encode(Obs.ListBuildingsReply reply)
        {
            Require(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) <= 1024 * 1024, "Complete building reply exceeds 1 MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private static void Require(bool value, string detail) { if (!value) throw new ReadLimit(detail); }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) {} }
    }
}
