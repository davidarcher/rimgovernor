#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Placement = RimGovernor.Protocol.Placement;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeConstructionPlan
    {
        internal readonly Map Map;
        internal readonly BuildableDef Definition;
        // Terrain is set when the plan lays a floor (a TerrainDef): the frame
        // completes into terrain at the cell, never into a successor Thing.
        internal readonly TerrainDef? Terrain;
        internal readonly ThingDef? Stuff; // Null unless the definition is made from stuff.
        internal readonly IntVec3 Cell;
        internal readonly Rot4 Rotation;
        internal readonly Faction Player;
        internal readonly bool Instant;
        internal NativeConstructionPlan(Map map, BuildableDef definition, ThingDef? stuff, IntVec3 cell, Rot4 rotation, Faction player)
        {
            Map = map; Definition = definition; Terrain = definition as TerrainDef; Stuff = stuff; Cell = cell; Rotation = rotation; Player = player;
            Instant = definition.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff) == 0f;
        }

        internal static bool Prepare(Map map, Placement.PlacementCandidate? candidate, Common.ObservationContext context,
            [NotNullWhen(true)] out NativeConstructionPlan? plan, [NotNullWhen(true)] out Placement.PlacementEvaluated? preview, [NotNullWhen(false)] out Common.Failure? failure)
        {
            plan = null; preview = null;
            var validation = new Placement.PlacementRequest { Identity = context.Identity };
            if (candidate != null) validation.Placements.Add(candidate);
            if (!PlacementProtocol.Validate(validation, out failure)) return false;
            if (candidate == null) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Construction requires one placement candidate."); return false; }
            var query = new PlacementQuery(candidate.DefName, candidate.X, candidate.Z,
                PlacementProtocol.RotationName(candidate.Rotation), candidate.HasStuff ? candidate.Stuff : null);
            var result = PlacementProtocol.Map(PlacementPreviewOperation.Evaluate(map, query), context);
            if (result.Failure != null) { failure = result.Failure; return false; }
            preview = result.Evaluated;
            if (candidate.Rotation == Placement.Rotation.All)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Construction requires one cardinal rotation.");
                return false;
            }
            if (!PlacementPreviewOperation.TryResolveBuildable(candidate.DefName, out var definition, out _) || definition == null)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "This construction adapter requires a ThingDef building or a TerrainDef floor.");
                return false;
            }
            if (definition is TerrainDef && definition.GetStatValueAbstract(StatDefOf.WorkToBuild) == 0f)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "This construction adapter lays floors through an ordinary blueprint and frame only.");
                return false;
            }
            if (!preview.CanPlace)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                    PlacementPreviewOperation.Diagnostic(preview.Rotations[0].Reason));
                return false;
            }
            var player = Faction.OfPlayerSilentFail;
            if (player == null)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "No player faction is available.");
                return false;
            }
            var stuff = preview.MadeFromStuff ? (candidate.Stuff.Length == 0 ? GenStuff.DefaultStuffFor(definition)
                : PlacementPreviewOperation.ResolveThingDef(candidate.Stuff)) : null;
            plan = new NativeConstructionPlan(map, definition, stuff, new IntVec3(candidate.X, 0, candidate.Z),
                new Rot4((int)candidate.Rotation - 1), player);
            failure = null;
            return true;
        }

        // Call inside the admitted native authority scope. Every mutation uses ordinary architect behavior.
        internal Thing Place(Receipts.ConstructionEffect observed, bool replaceWall = false)
        {
            // A wall replacement keeps the built wall standing: vanilla wipes it in the
            // same call that turns the blueprint into a Frame, so the cell is never open.
            if (replaceWall) return GenConstruct.PlaceBlueprintForBuild(Definition, Cell, Map, Rotation, Player, Stuff);
            var blueprint = Definition.blueprintDef;
            foreach (var frame in Map.thingGrid.ThingsListAt(Cell).OfType<Frame>().ToArray())
            {
                if (frame.Destroyed || blueprint.replaceTags == null || frame.def.replaceTags == null
                    || !blueprint.replaceTags.Intersect(frame.def.replaceTags).Any()) continue;
                var id = frame.GetUniqueLoadID();
                frame.Destroy(DestroyMode.Cancel);
                if (frame.Destroyed) observed.CancelledFrameIds.Add(id);
            }
            if (!Instant)
            {
                var wiped = GenAdj.CellsOccupiedBy(Cell, Rotation, blueprint.Size).Where(cell => cell.InBounds(Map))
                    .SelectMany(cell => Map.thingGrid.ThingsListAt(cell)).Distinct()
                    .Where(thing => !thing.Destroyed && GenSpawn.SpawningWipes(blueprint, thing.def)).ToArray();
                var ids = wiped.Select(thing => thing.GetUniqueLoadID()).ToArray();
                GenSpawn.WipeExistingThings(Cell, Rotation, blueprint, Map, DestroyMode.Deconstruct);
                for (var index = 0; index < wiped.Length; index++)
                    if (wiped[index].Destroyed) observed.WipedThingIds.Add(ids[index]);
            }
            if (!Instant) return GenConstruct.PlaceBlueprintForBuild(Definition, Cell, Map, Rotation, Player, Stuff);
            var building = ThingMaker.MakeThing((ThingDef)Definition, Stuff);
            building.SetFactionDirect(Player);
            return GenSpawn.Spawn(building, Cell, Map, Rotation);
        }

        internal Receipts.ConstructionEffect Proposed() => new Receipts.ConstructionEffect
        {
            DefName = Definition.defName, Stuff = Stuff?.defName ?? "", Cell = new Common.Cell { X = Cell.x, Z = Cell.z },
            Rotation = (Placement.Rotation)(Rotation.AsInt + 1)
        };
    }
}
