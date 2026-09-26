#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Presentation = RimGovernor.Protocol.Presentation;

namespace HomeBridge.BridgeTools
{
    // The colony layout overlay (#726): the master plan drawn as native 1.6
    // plan designations with text labels. Output only: every call deletes the
    // plans this tool owns (label prefix "RimGovernor") and redraws; nothing
    // reads them back. A cell a player plan already holds is skipped, never
    // overwritten.
    public sealed class ProtoLayoutPlanTools
    {
        private const string ToolName = "rimgovernor/presentation_layout_plan";
        internal const string OwnedPrefix = "RimGovernor";
        private const int MaxCells = 250000;
        private const int MaxLabels = 4096;

        [Tool(ToolName, Title = "Draw the colony layout overlay",
            Description = "Replace RimGovernor-owned plan designations and labels with the given layers, painting existing rooms by role. enabled=false removes them.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.presentation.v1.LayoutPlanReply.", Always = true)]
        public async Task<object> Draw(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON LayoutPlanRequest string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Presentation.LayoutPlanRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Presentation.LayoutPlanReply { Failure = failure });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => Apply(parsed), cancellationToken).ConfigureAwait(false);
        }

        // Call only on the game thread.
        internal static Presentation.LayoutPlanReply Apply(Presentation.LayoutPlanRequest request)
        {
            if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out var context, out var invalid))
                return new Presentation.LayoutPlanReply { Failure = invalid };
            try
            {
                var colors = new Dictionary<string, ColorDef>(StringComparer.Ordinal);
                ColorDef? Color(string name)
                {
                    if (!colors.TryGetValue(name, out var def))
                        colors[name] = def = DefDatabase<ColorDef>.GetNamedSilentFail(name);
                    return def != null && def.colorType == ColorType.Planning ? def : null;
                }
                foreach (var layer in request.Layers)
                    if (!layer.HasColorDef || Color(layer.ColorDef) == null)
                        return Invalid($"Unknown planning color '{layer.ColorDef}'.");
                foreach (var room in request.RoomColors)
                    if (!room.HasRoleDef || !room.HasColorDef || Color(room.ColorDef) == null)
                        return Invalid($"Room role '{room.RoleDef}' needs a planning color.");
                if (request.Labels.Count > MaxLabels) return Invalid($"More than {MaxLabels} labels.");

                var applied = new Presentation.LayoutPlanApplied { Context = context, Removed = Clear(map) };
                var texts = new List<string>();
                var cells = new List<IntVec3>();
                if (request.Enabled)
                {
                    uint budget = MaxCells, plans = 0, painted = 0, skipped = 0, rooms = 0;
                    Plan? Draw(ColorDef color, string label, IEnumerable<IntVec3> area)
                    {
                        Plan? plan = null;
                        foreach (var c in area)
                        {
                            if (!c.InBounds(map)) continue;
                            if (map.planManager.PlanAt(c) != null) { skipped++; continue; }
                            if (budget == 0) throw new InvalidOperationException($"Overlay exceeds {MaxCells} cells.");
                            if (plan == null) { plan = new Plan(color, map.planManager) { label = OwnedPrefix + " " + label }; plans++; }
                            plan.AddCell(c);
                            budget--;
                            painted++;
                        }
                        return plan;
                    }
                    // Existing rooms first: they are solid, and the planned
                    // modules beneath them yield.
                    var roles = request.RoomColors.ToDictionary(r => r.RoleDef, StringComparer.Ordinal);
                    var labelled = new HashSet<Room>();
                    foreach (var room in map.regionGrid.AllRooms)
                    {
                        if (room.PsychologicallyOutdoors || room.TouchesMapEdge || room.IsHuge || room.Role == null) continue;
                        var key = room.Role.defName == "Storeroom" && room.Temperature < 0f ? "Freezer" : room.Role.defName;
                        if (!roles.TryGetValue(key, out var entry)) continue;
                        var label = entry.HasLabel ? entry.Label : room.Role.label;
                        if (Draw(Color(entry.ColorDef)!, label, room.Cells) == null) continue;
                        rooms++;
                        labelled.Add(room);
                        texts.Add(label);
                        cells.Add(Centre(room));
                    }
                    foreach (var layer in request.Layers)
                    {
                        var color = Color(layer.ColorDef)!;
                        Draw(color, layer.HasLabel ? layer.Label : color.label, layer.Rects.SelectMany(Cells));
                    }
                    foreach (var label in request.Labels)
                    {
                        if (!label.HasText || label.Cell == null) continue;
                        var c = new IntVec3(label.Cell.X, 0, label.Cell.Z);
                        if (!c.InBounds(map) || cells.Contains(c)) continue;
                        // A module an existing room already labels stays quiet.
                        if (c.GetRoom(map) is Room r && labelled.Contains(r)) continue;
                        texts.Add(label.Text);
                        cells.Add(c);
                    }
                    applied.Plans = plans;
                    applied.Cells = painted;
                    applied.Skipped = skipped;
                    applied.Rooms = rooms;
                }
                map.GetComponent<LayoutPlanLabels>()?.Replace(texts, cells);
                return new Presentation.LayoutPlanReply { Applied = applied };
            }
            catch (Exception error)
            {
                Clear(map);
                map.GetComponent<LayoutPlanLabels>()?.Replace(new List<string>(), new List<IntVec3>());
                return new Presentation.LayoutPlanReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, error.Message) };
            }
        }

        private static Presentation.LayoutPlanReply Invalid(string detail) =>
            new Presentation.LayoutPlanReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, detail) };

        // Deletes every owned plan, cell by cell from the end (Plan.Delete
        // plays a sound and touches the selector, which headless lacks).
        private static uint Clear(Map map)
        {
            uint removed = 0;
            foreach (var plan in map.planManager.AllPlans.ToList())
            {
                if (plan.label == null || !plan.label.StartsWith(OwnedPrefix, StringComparison.Ordinal)) continue;
                removed++;
                var owned = plan.Cells;
                if (owned.Count == 0) { plan.Deregister(); continue; }
                for (var i = owned.Count - 1; i >= 0; i--) plan.RemoveCell(owned[i]);
            }
            return removed;
        }

        private static IEnumerable<IntVec3> Cells(Presentation.MapRect rect)
        {
            if (rect.MaxX < rect.MinX || rect.MaxZ < rect.MinZ) throw new InvalidOperationException("Inverted overlay rectangle.");
            for (var z = rect.MinZ; z <= rect.MaxZ; z++)
                for (var x = rect.MinX; x <= rect.MaxX; x++)
                    yield return new IntVec3(x, 0, z);
        }

        // The room cell nearest the room's mean cell, so an L-shaped room's
        // label still sits inside it.
        private static IntVec3 Centre(Room room)
        {
            var all = room.Cells.ToList();
            float x = 0, z = 0;
            foreach (var c in all) { x += c.x; z += c.z; }
            x /= all.Count; z /= all.Count;
            return all.OrderBy(c => (c.x - x) * (c.x - x) + (c.z - z) * (c.z - z)).First();
        }
    }
}
