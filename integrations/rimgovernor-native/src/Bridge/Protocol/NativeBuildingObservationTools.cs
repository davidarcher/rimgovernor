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

        [Tool(ToolName, Title = "Read typed buildings", Description = "Read complete bounded building, blueprint and frame facts including walls. Exact IDs/definitions, inclusive anchor region; defaults artificial/player-only. No CAS snapshots, detailed settings, bills, inspect text or power-network enumeration yet.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ListBuildingsReply. Unavailable replaces oversized collections; unsupported facts are explicit.", Always = true)]
        public async Task<object> ListBuildings(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ListBuildingsRequest string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ListBuildingsRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                var map = ProtoBoundary.ResolveMap(parsed.Scope.ExpectedIdentity);
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Failure = error });
                try
                {
                    if ((!parsed.HasPlayerOnly || parsed.PlayerOnly) && Faction.OfPlayerSilentFail == null)
                        return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Unavailable = Unavailable(Common.UnavailableReason.NativeComponentMissing, "Player faction unavailable.") });
                    if (parsed.Region != null && (!NativeCell(parsed.Region.Minimum).InBounds(map) || !NativeCell(parsed.Region.Maximum).InBounds(map)))
                        return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Region must be inside the current map.") });
                    var source = Source(map, parsed.HasCategory && parsed.Category == "all");
                    var matched = source.Where(t => Matches(t, parsed)).OrderBy(t => t.thingIDNumber).ToList();
                    var seed = QuerySeed(parsed);
                    var afterCursor = matched;
                    if (parsed.Page != null && parsed.Page.HasCursor && parsed.Page.Cursor.Length != 0)
                    {
                        if (!NativeObservationSnapshot.Cursor.TryDecode(context.Identity, seed, parsed.Page.Cursor, out var after))
                            return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, "Building cursor is stale or does not match this query.") });
                        afterCursor = matched.Where(t => string.CompareOrdinal(Id(t.GetUniqueLoadID()), after) > 0).ToList();
                    }
                    var page = afterCursor.Take(Limit(parsed)).ToList();
                    Require(page.Count <= 256, "Matched building collection exceeds page limit; narrow filters.");
                    var truncated = afterCursor.Count > page.Count;
                    var snapshot = new Obs.BuildingsSnapshot { Context = context,
                        Completeness = Complete(page.Count, source.Count - matched.Count),
                        NetworksCompleteness = new Obs.Completeness { Page = new Common.PageInfo { Complete = false } } };
                    snapshot.Completeness.Page.Complete = !truncated;
                    if (truncated) snapshot.Completeness.Page.NextCursor = NativeObservationSnapshot.Cursor.Encode(context.Identity, seed, Id(page[page.Count-1].GetUniqueLoadID()));
                    var cells = 0;
                    foreach (var thing in page)
                    {
                        var row = Project(thing);
                        row.Snapshot = row.Construction != null
                            ? NativeObservationSnapshot.Snapshot("building", context, row.Building.Id, w => {
                                w.Write(row.Status??""); w.Write(row.HitPoints); w.Write(row.Burning);
                                w.Write(row.Construction.PercentComplete); w.Write(row.Construction.ResourcesComplete);
                            })
                            : Token(thing, context);
                        // Only target_temperature_c and its own dedicated CAS
                        // snapshot are populated here; forbidden/power/medical/
                        // owner/forPrisoners remain the "settings" unsupported
                        // issue below. See NativeBuildingTemperature, the
                        // PatchBuilding write this snapshot is read for.
                        var tempControl = thing.TryGetComp<CompTempControl>();
                        if (tempControl != null)
                            row.Settings = new Obs.BuildingSettings { Snapshot = NativeBuildingTemperature.Snapshot(thing, context), TargetTemperatureC = tempControl.targetTemperature };
                        cells = checked(cells + row.OccupiedCells.Count);
                        Require(cells <= 4096, "Complete building geometry exceeds 4096 cells.");
                        snapshot.Buildings.Add(row);
                    }
                    return Encode(new Obs.ListBuildingsReply { Observed = snapshot });
                }
                catch (ReadLimit errorLimit) { return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, errorLimit.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Building facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
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
            return true;
        }

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
            // "settings" is reported unsupported wholesale only when this thing has
            // no CompTempControl; a temp-controlled thing gets target_temperature_c
            // and its snapshot filled in by the caller below instead (see
            // NativeBuildingTemperature) -- forbidden/power/medical/owner/
            // forPrisoners remain unimplemented either way.
            var fields = thing.TryGetComp<CompTempControl>() != null
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
