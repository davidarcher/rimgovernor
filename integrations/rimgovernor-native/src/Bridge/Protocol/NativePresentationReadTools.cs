#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using UnityEngine;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Presentation = RimGovernor.Contracts.Wire.Presentation;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativePresentationReadTools
    {
        [Tool("rimgovernor/presentation_camera", Title = "Read native camera", Description = "Graphical camera facts only; no camera movement or rendering demand. Headless camera is unavailable.")]
        [ToolResponse("payload", "string", "Official ProtoJSON CameraReply.", Always = true)]
        public async Task<object> Camera(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ReadRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Presentation.ReadRequest>(ctx, "rimgovernor/presentation_camera", request, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Presentation.CameraReply { Outcome = new Presentation.CameraReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleCamera(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Presentation.CameraReply HandleCamera(Presentation.ReadRequest parsed)
        {
            if (!ValidateRead(parsed, out var invalidRequest)) return new Presentation.CameraReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error))
                return new Presentation.CameraReply { Outcome = error };
            try
            {
                var driver = Find.CameraDriver;
                if (HeadlessRim.HeadlessMode.Active || Find.Camera == null || driver?.config == null)
                    return new Presentation.CameraReply { Outcome = Unavailable("Graphical map camera unavailable.") };
                var position = driver.MapPosition;
                var size = driver.config.sizeRange;
                var rect = driver.CurrentViewRect;
                var state = new Presentation.CameraState { Context = context,
                    MapPosition = new Presentation.MapPoint { X = Finite(position.x), Z = Finite(position.z) },
                    RootSize = Positive(driver.RootSize), ZoomRootSize = Positive(driver.ZoomRootSize),
                    MinimumRootSize = Positive(size.min), MaximumRootSize = Positive(size.max),
                    NativeZoomRange = Id(driver.CurrentZoom.ToString()), ZoomExtensionEnabled = false,
                    ViewRect = new Presentation.MapRect { MinX = rect.minX, MinZ = rect.minZ, MaxX = rect.maxX, MaxZ = rect.maxZ } };
                if (state.MaximumRootSize.GetValueOrDefault() < state.MinimumRootSize.GetValueOrDefault() || rect.maxX < rect.minX || rect.maxZ < rect.minZ)
                    throw new InvalidOperationException("Native camera bounds invalid.");
                return new Presentation.CameraReply { Outcome = state };
            }
            catch (Exception errorRead) { return new Presentation.CameraReply { Outcome = ReadFailure(errorRead) }; }
        }

        [Tool("rimgovernor/presentation_selection", Title = "Read native selection", Description = "Complete bounded graphical selection IDs from native things, zones and plans. No inspect strings, gizmo enumeration, selection changes or capture authority.")]
        [ToolResponse("payload", "string", "Official ProtoJSON SelectionReply; unproven optional fingerprint/gizmo facts are omitted.", Always = true)]
        public async Task<object> Selection(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ReadRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Presentation.ReadRequest>(ctx, "rimgovernor/presentation_selection", request, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Presentation.SelectionReply { Outcome = new Presentation.SelectionReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleSelection(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Presentation.SelectionReply HandleSelection(Presentation.ReadRequest parsed)
        {
            if (!ValidateRead(parsed, out var invalidRequest)) return new Presentation.SelectionReply { Outcome = invalidRequest };
            var map = Find.CurrentMap;
            if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error))
                return new Presentation.SelectionReply { Outcome = error };
            try
            {
                if (HeadlessRim.HeadlessMode.Active || Find.Selector == null)
                    return new Presentation.SelectionReply { Outcome = Unavailable("Graphical native selection unavailable.") };
                var objects = Find.Selector.SelectedObjectsListForReading.ToList();
                var snapshot = new Presentation.SelectionSnapshot { Context = context, Listing = Listing(objects.Count) };
                foreach (var value in objects) snapshot = snapshot with { SelectedObjects = [.. snapshot.SelectedObjects, Selected(value, map)] };
                if (snapshot.SelectedObjects.Select(s => (s.Id ?? "")).Distinct(StringComparer.Ordinal).Count() != snapshot.SelectedObjects.Count)
                    throw new InvalidOperationException("Selected native IDs are not unique.");
                return new Presentation.SelectionReply { Outcome = snapshot };
            }
            catch (Exception errorRead) { return new Presentation.SelectionReply { Outcome = ReadFailure(errorRead) }; }
        }

        [Tool("rimgovernor/presentation_colonists", Title = "Read native colonist roster", Description = "Complete bounded FreeColonistsSpawned roster. Default includes all loaded maps; currentMapOnly narrows it; the controller joins includeDossier from its bundle pawn table. No world caravan, prisoner, slave or unspawned-pawn claim.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ColonistRosterReply; usable in headless and graphical games.", Always = true)]
        public async Task<object> Colonists(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ColonistRosterRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Presentation.ColonistRosterRequest>(ctx, "rimgovernor/presentation_colonists", request, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Presentation.ColonistRosterReply { Outcome = new Presentation.ColonistRosterReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleColonists(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Presentation.ColonistRosterReply HandleColonists(Presentation.ColonistRosterRequest parsed)
        {
            if (!ValidateColonists(parsed, out var invalidRequest)) return new Presentation.ColonistRosterReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var error))
                return new Presentation.ColonistRosterReply { Outcome = error };
            try
            {
                var maps = parsed.CurrentMapOnly.GetValueOrDefault() ? new[] { ProtoBoundary.LoadedMap(context) } : Find.Maps.ToArray();
                var pawns = maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).Distinct().OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                var roster = new Presentation.ColonistRoster { Context = context, Listing = Listing(pawns.Count) };
                foreach (var pawn in pawns)
                {
                    if (!pawn.Spawned || pawn.Map == null || !pawn.Position.InBounds(pawn.Map)) throw new InvalidOperationException("Spawned colonist context unavailable.");
                    var reference = new Presentation.ColonistReference { PawnId = Id(pawn.GetUniqueLoadID()),
                        Name = Diagnostic(pawn.Name?.ToStringShort ?? pawn.LabelShort), MapId = pawn.Map.uniqueID,
                        Spawned = true, Position = Cell(pawn.Position) };
                    roster = roster with { Colonists = [.. roster.Colonists, reference] };
                }
                if (roster.Colonists.Select(p => (p.PawnId ?? "")).Distinct(StringComparer.Ordinal).Count() != roster.Colonists.Count)
                    throw new InvalidOperationException("Native colonist IDs are not unique.");
                return new Presentation.ColonistRosterReply { Outcome = roster };
            }
            catch (Exception errorRead) { return new Presentation.ColonistRosterReply { Outcome = ReadFailure(errorRead) }; }
        }

        internal static bool ValidateRead(Presentation.ReadRequest request, out Common.Failure failure)
        { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity is required."); return request?.Identity != null; }
        internal static bool ValidateColonists(Presentation.ColonistRosterRequest request, out Common.Failure failure)
        { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity is required."); return request?.Identity != null; }

        private static Presentation.SelectedObject Selected(object value, Map expectedMap)
        {
            var row = new Presentation.SelectedObject { NativeType = Id(value.GetType().FullName!) };
            Map map;
            if (value is Thing thing)
            {
                map = thing.Map;
                row = row with { Id = Id(thing.GetUniqueLoadID()) }; row = row with { NativeKind = thing is Pawn ? "pawn" : "thing" };
                row = row with { Label = Diagnostic(thing is Pawn pawn ? pawn.Name?.ToStringShort ?? pawn.LabelShort : thing.LabelCap) }; row = row with { DefName = Id(thing.def.defName) };
                if (map != null && thing.Position.InBounds(map)) row = row with { Position = Cell(thing.Position) };
            }
            else if (value is Zone zone)
            {
                map = zone.Map; row = row with { Id = Id(zone.GetUniqueLoadID()) }; row = row with { NativeKind = "zone" }; row = row with { Label = Diagnostic(zone.label) };
                if (map != null && zone.Position.InBounds(map)) row = row with { Position = Cell(zone.Position) };
            }
            else if (value is Plan plan)
            {
                map = plan.Map; row = row with { Id = Id(plan.GetUniqueLoadID()) }; row = row with { NativeKind = "plan" };
                // Plan has no ordinary cell position; no invented anchor or inspect call.
            }
            else throw new InvalidOperationException("Selected native kind is not supported.");
            if (map != expectedMap) throw new InvalidOperationException("Selected object belongs to another map.");
            row = row with { MapId = map.uniqueID };
            return row;
        }
        private static Common.Cell Cell(IntVec3 cell) => new Common.Cell { X = cell.x, Z = cell.z };
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static string Diagnostic(string value) => PlacementPreviewOperation.Diagnostic(value);
        internal static double Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value) ? value : throw new InvalidOperationException("Native camera value is not finite.");
        private static double Positive(double value) => Finite(value) > 0 ? value : throw new InvalidOperationException("Native camera size is not positive.");
        internal static Presentation.Listing Listing(int count) => new Presentation.Listing { TotalCount = checked((uint)count), ReturnedCount = checked((uint)count), Complete = true, Truncated = false };
        private static Common.Failure Unavailable(string detail) => ProtoBoundary.Fail(Common.FailureCode.Unavailable, detail);
        private static Common.Failure ReadFailure(Exception error) => error is ReadLimit || error is NativePawnObservationTools.ReadLimit ? ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted, error.Message) : Unavailable("Native presentation facts could not be read completely.");
        internal static object Encode<T>(T reply) where T : class
        {
            return ProtoBoundary.Encode(reply);
        }
        private static void Require(bool value, string detail) { if (!value) throw new ReadLimit(detail); }
        internal sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) {} }
    }
}
