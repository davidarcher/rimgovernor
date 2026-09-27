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
        internal const string ToolName = "rimgovernor/observations_list_buildings";

        [Tool(ToolName, Title = "Read typed buildings", Description = "Read complete building, blueprint and frame facts including walls. Exact IDs/definitions, inclusive anchor region; defaults artificial/player-only. No CAS snapshots, detailed settings, bills, inspect text or power-network enumeration yet.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ListBuildingsReply. Unsupported facts are explicit.", Always = true)]
        public async Task<object> ListBuildings(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ListBuildingsRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ListBuildingsRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ListBuildingsReply { Failure = error });
                return ProtoBoundary.Encode(Read(map, parsed, context));
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
                var snapshot = new Obs.BuildingsSnapshot { Context = context };
                snapshot.Completeness = Complete(matched.Count, source.Count - matched.Count);
                var keys = ConstructionLineage.Keys(map);
                foreach (var thing in matched)
                {
                    var row = Row(thing, context);
                    if (keys.TryGetValue(thing.GetUniqueLoadID(), out var key)) row.IntentKey = key;
                    snapshot.Buildings.Add(row);
                }
                // The keyed blueprints and frames still standing: the census's
                // open building intents (#856).
                foreach (var open in ConstructionLineage.OpenIntents(map))
                    snapshot.Intents.Add(new Obs.ConstructionIntent { Key = open.Key, Stage = open.Stage });
                return new Obs.ListBuildingsReply { Observed = snapshot };
            }
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
            // Only the implemented BuildingPatchIntent arms carry a
            // settings row with their own dedicated CAS snapshot:
            // target_temperature_c (NativeBuildingTemperature), a
            // humanlike bed's medical flag (NativeBedUse), a
            // plant grower's crop (NativeGrowerCrop) and a claimable
            // building's faction (NativeClaimBuilding, #459). forbidden/
            // power/owner remain the "settings" unsupported issue below.
            // A player storage building (shelf) reports its storage token
            // (NativeStockpilePatch, a StockpileIntent on a building) first.
            var tempControl = thing.TryGetComp<CompTempControl>();
            if (NativeStockpilePatch.StorageEligible(thing))
                row.Settings = NativeStockpilePatch.Settings((Building_Storage)thing, context);
            else if (tempControl != null)
                row.Settings = new Obs.BuildingSettings { Snapshot = NativeBuildingTemperature.Snapshot(thing, context), TargetTemperatureC = tempControl.targetTemperature };
            else if (NativeBedUse.Eligible(thing))
                row.Settings = NativeBedUse.Settings((Building_Bed)thing, context);
            else if (NativeGrowerCrop.Eligible(thing))
                row.Settings = NativeGrowerCrop.Settings((Building_PlantGrower)thing, context);
            else if (NativeClaimBuilding.Eligible(thing))
                row.Settings = NativeClaimBuilding.Settings((Building)thing, context);
            return row;
        }

        internal static bool Validate(Obs.ListBuildingsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope, exact bounded identifiers, supported filters are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
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
            // the caller below (see NativeBuildingTemperature, NativeBedUse,
            // NativeGrowerCrop) -- forbidden/power/owner/forPrisoners remain
            // unimplemented either way.
            var fields = NativeStockpilePatch.StorageEligible(thing) || thing.TryGetComp<CompTempControl>() != null || NativeBedUse.Eligible(thing) || NativeGrowerCrop.Eligible(thing) || NativeClaimBuilding.Eligible(thing)
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

        private static bool Identifiers(IEnumerable<string> values) => values.Count() <= 256
            && values.All(ProtoBoundary.IsIdentifier) && values.Distinct(StringComparer.Ordinal).Count() == values.Count();
        private static bool CellPresent(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ;
        private static IntVec3 NativeCell(Common.Cell cell) => new IntVec3(cell.X, 0, cell.Z);
        private static Common.Cell Cell(IntVec3 cell) => new Common.Cell { X = cell.x, Z = cell.z };
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
        private static Obs.Completeness Complete(int count, int filtered) => new Obs.Completeness { Filtered = (ulong)filtered };
    }
}
