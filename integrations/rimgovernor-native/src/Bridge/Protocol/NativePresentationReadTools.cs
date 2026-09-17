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
using UnityEngine;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Presentation = RimGovernor.Protocol.Presentation;

namespace HomeBridge.BridgeTools
{
    public sealed class NativePresentationReadTools
    {
        [Tool("rimgovernor/presentation_camera", Title = "Read native camera", Description = "Graphical camera facts only; no camera movement or rendering demand. Headless camera is unavailable.")]
        [ToolResponse("payload", "string", "Official ProtoJSON CameraReply.", Always = true)]
        public async Task<object> Camera(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ReadRequest string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_camera", request, Presentation.ReadRequest.Parser, out var parsed, out var failure)
                || !ValidateRead(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.CameraReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error))
                    return ProtoBoundary.Encode(new Presentation.CameraReply { Failure = error });
                try
                {
                    var driver = Find.CameraDriver;
                    if (Application.isBatchMode || Find.Camera == null || driver?.config == null)
                        return ProtoBoundary.Encode(new Presentation.CameraReply { Failure = Unavailable("Graphical map camera unavailable.") });
                    var server = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "RimBridgeServer");
                    if (server == null || !TryZoomExtension(server, out var enabled))
                        return ProtoBoundary.Encode(new Presentation.CameraReply { Failure = Unavailable("Native SDK camera extension state unavailable.") });
                    var position = driver.MapPosition;
                    var size = driver.config.sizeRange;
                    var rect = driver.CurrentViewRect;
                    var state = new Presentation.CameraState { Context = context,
                        MapPosition = new Presentation.MapPoint { X = Finite(position.x), Z = Finite(position.z) },
                        RootSize = Positive(driver.RootSize), ZoomRootSize = Positive(driver.ZoomRootSize),
                        MinimumRootSize = Positive(size.min), MaximumRootSize = Positive(size.max),
                        NativeZoomRange = Id(driver.CurrentZoom.ToString()), ZoomExtensionEnabled = enabled,
                        ViewRect = new Presentation.MapRect { MinX = rect.minX, MinZ = rect.minZ, MaxX = rect.maxX, MaxZ = rect.maxZ } };
                    if (state.MaximumRootSize < state.MinimumRootSize || rect.maxX < rect.minX || rect.maxZ < rect.minZ)
                        throw new InvalidOperationException("Native camera bounds invalid.");
                    return Encode(new Presentation.CameraReply { Camera = state });
                }
                catch (Exception errorRead) { return ProtoBoundary.Encode(new Presentation.CameraReply { Failure = ReadFailure(errorRead) }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/presentation_selection", Title = "Read native selection", Description = "Complete bounded graphical selection IDs from native things, zones and plans. No inspect strings, gizmo enumeration, selection changes or capture authority.")]
        [ToolResponse("payload", "string", "Official ProtoJSON SelectionReply; unproven optional fingerprint/gizmo facts are omitted.", Always = true)]
        public async Task<object> Selection(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ReadRequest string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_selection", request, Presentation.ReadRequest.Parser, out var parsed, out var failure)
                || !ValidateRead(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.SelectionReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error))
                    return ProtoBoundary.Encode(new Presentation.SelectionReply { Failure = error });
                try
                {
                    if (Application.isBatchMode || Find.Selector == null)
                        return ProtoBoundary.Encode(new Presentation.SelectionReply { Failure = Unavailable("Graphical native selection unavailable.") });
                    var objects = Find.Selector.SelectedObjectsListForReading.ToList();
                    Require(objects.Count <= 4096, "Selection exceeds 4096 objects.");
                    var snapshot = new Presentation.SelectionSnapshot { Context = context, Listing = Listing(objects.Count) };
                    foreach (var value in objects) snapshot.SelectedObjects.Add(Selected(value, map));
                    if (snapshot.SelectedObjects.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.SelectedObjects.Count)
                        throw new InvalidOperationException("Selected native IDs are not unique.");
                    return Encode(new Presentation.SelectionReply { Selection = snapshot });
                }
                catch (Exception errorRead) { return ProtoBoundary.Encode(new Presentation.SelectionReply { Failure = ReadFailure(errorRead) }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/presentation_colonists", Title = "Read native colonist roster", Description = "Complete bounded FreeColonistsSpawned roster. Default includes all loaded maps; currentMapOnly narrows it. No world caravan, prisoner, slave or unspawned-pawn claim.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ColonistRosterReply; usable in headless and graphical games.", Always = true)]
        public async Task<object> Colonists(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ColonistRosterRequest string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_colonists", request, Presentation.ColonistRosterRequest.Parser, out var parsed, out var failure)
                || !ValidateColonists(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.ColonistRosterReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var error))
                    return ProtoBoundary.Encode(new Presentation.ColonistRosterReply { Failure = error });
                try
                {
                    var maps = parsed.CurrentMapOnly ? new[] { ProtoBoundary.ResolveMap(context) } : Find.Maps.ToArray();
                    var pawns = maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).Distinct().OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                    Require(pawns.Count <= 256, "Colonist roster exceeds 256 objects.");
                    var roster = new Presentation.ColonistRoster { Context = context, Listing = Listing(pawns.Count) };
                    foreach (var pawn in pawns)
                    {
                        if (!pawn.Spawned || pawn.Map == null || !pawn.Position.InBounds(pawn.Map)) throw new InvalidOperationException("Spawned colonist context unavailable.");
                        roster.Colonists.Add(new Presentation.ColonistReference { PawnId = Id(pawn.GetUniqueLoadID()),
                            Name = Diagnostic(pawn.Name?.ToStringShort ?? pawn.LabelShort), MapId = pawn.Map.uniqueID,
                            Spawned = true, Position = Cell(pawn.Position) });
                    }
                    if (roster.Colonists.Select(p => p.PawnId).Distinct(StringComparer.Ordinal).Count() != roster.Colonists.Count)
                        throw new InvalidOperationException("Native colonist IDs are not unique.");
                    return Encode(new Presentation.ColonistRosterReply { Roster = roster });
                }
                catch (Exception errorRead) { return ProtoBoundary.Encode(new Presentation.ColonistRosterReply { Failure = ReadFailure(errorRead) }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool ValidateRead(Presentation.ReadRequest request, out Common.Failure failure)
        { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity is required."); return request?.Identity != null; }
        internal static bool ValidateColonists(Presentation.ColonistRosterRequest request, out Common.Failure failure)
        { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity is required."); return request?.Identity != null; }

        internal static bool TryZoomExtension(Assembly server, out bool enabled)
        {
            enabled = false;
            var property = server.GetType("RimBridgeServer.RimBridgeCameraConfig", false)?.GetProperty("CameraZoomExtensionEnabled", BindingFlags.Public | BindingFlags.Static);
            if (property == null || property.PropertyType != typeof(bool) || property.GetIndexParameters().Length != 0
                || property.GetGetMethod()?.IsStatic != true) return false;
            enabled = (bool)property.GetValue(null)!; // SDK getter is the audited _enabled field read, never SetZoomExtension.
            return true;
        }

        private static Presentation.SelectedObject Selected(object value, Map expectedMap)
        {
            var row = new Presentation.SelectedObject { NativeType = Id(value.GetType().FullName!) };
            Map map;
            if (value is Thing thing)
            {
                map = thing.Map;
                row.Id = Id(thing.GetUniqueLoadID()); row.NativeKind = thing is Pawn ? "pawn" : "thing";
                row.Label = Diagnostic(thing is Pawn pawn ? pawn.Name?.ToStringShort ?? pawn.LabelShort : thing.LabelCap); row.DefName = Id(thing.def.defName);
                if (map != null && thing.Position.InBounds(map)) row.Position = Cell(thing.Position);
            }
            else if (value is Zone zone)
            {
                map = zone.Map; row.Id = Id(zone.GetUniqueLoadID()); row.NativeKind = "zone"; row.Label = Diagnostic(zone.label);
                if (map != null && zone.Position.InBounds(map)) row.Position = Cell(zone.Position);
            }
            else if (value is Plan plan)
            {
                map = plan.Map; row.Id = Id(plan.GetUniqueLoadID()); row.NativeKind = "plan";
                // Plan has no ordinary cell position; no invented anchor or inspect call.
            }
            else throw new InvalidOperationException("Selected native kind is not supported.");
            if (map != expectedMap) throw new InvalidOperationException("Selected object belongs to another map.");
            row.MapId = map.uniqueID;
            return row;
        }
        private static Common.Cell Cell(IntVec3 cell) => new Common.Cell { X = cell.x, Z = cell.z };
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static string Diagnostic(string value) => PlacementPreviewOperation.Diagnostic(value);
        internal static double Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value) ? value : throw new InvalidOperationException("Native camera value is not finite.");
        private static double Positive(double value) => Finite(value) > 0 ? value : throw new InvalidOperationException("Native camera size is not positive.");
        internal static Presentation.Listing Listing(int count) => new Presentation.Listing { TotalCount = checked((uint)count), ReturnedCount = checked((uint)count), Complete = true, Truncated = false };
        private static Common.Failure Unavailable(string detail) => ProtoBoundary.Fail(Common.FailureCode.Unavailable, detail);
        private static Common.Failure ReadFailure(Exception error) => error is ReadLimit ? ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted, error.Message) : Unavailable("Native presentation facts could not be read completely.");
        internal static object Encode(IMessage reply)
        {
            Require(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) <= 1024 * 1024, "Presentation reply exceeds 1 MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private static void Require(bool value, string detail) { if (!value) throw new ReadLimit(detail); }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) {} }
    }
}
