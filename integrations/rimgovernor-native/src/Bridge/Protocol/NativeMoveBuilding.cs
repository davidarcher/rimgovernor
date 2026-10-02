#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Placement = RimGovernor.Protocol.Placement;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // RelocateIntent on Actions/Apply (#808, #830, #843, #940). A reinstall
    // places the game's reinstall blueprint at the destination through
    // GenConstruct.PlaceBlueprintForReinstall, the Reinstall gizmo's write,
    // without its WipeExistingThings (a blocked cell is a refusal). Ordinary
    // construction work then uninstalls and carries the piece; it needs
    // CanReserve on it, so a pawn sleeping in a bed or working a bench is
    // never interrupted. A packed (minified) item named by its own id or its
    // inner building's gets the game's install blueprint instead. An
    // uninstall (NativeUninstallBuilding) places the Uninstall designation.
    // Applied means ordered: a blueprint already standing for the same
    // placement applies again, and the next building read decides progress.
    internal static class NativeMoveBuilding
    {
        internal const string Kind = "Move building";

        internal static Building? Find(Map map, string id) => RefIndex.Thing<Building>(map, id);

        private static MinifiedThing? FindPacked(Map map, string id) => 
            RefIndex.Thing(map, id) as MinifiedThing ?? RefIndex.ThingOrMinified(map, id)?.ParentHolder as MinifiedThing;

        // Mover: someone with construction enabled must be able to reach
        // the piece now; whether it is free is the game's reservation, later.
        internal static bool Mover(Pawn p, Thing piece) => p.workSettings?.Initialized == true
            && p.workSettings.GetPriority(WorkTypeDefOf.Construction) > 0 && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)
            && !p.Downed && !p.InMentalState && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
            && p.CanReach(piece, PathEndMode.ClosestTouch, Danger.None);

        private static bool Valid(Operations.RelocateIntent intent) => ProtoBoundary.IsIdentifier(intent.ThingId)
            && intent.Destination != null && intent.Destination.HasX && intent.Destination.HasZ && intent.HasRotation
            && intent.Rotation >= Placement.Rotation.North && intent.Rotation <= Placement.Rotation.West;

        private static Rot4 Rotation(Operations.RelocateIntent intent) => new Rot4((int)intent.Rotation - 1);

        private static bool PreparePacked(Operations.RelocateIntent intent, Map map, MinifiedThing mini, out Common.Failure failure)
        {
            var cell = new IntVec3(intent.Destination.X, 0, intent.Destination.Z);
            var rotation = Rotation(intent);
            var inner = mini.InnerThing as Building;
            var rules = new ApplyPreconditions(Kind)
                .Present(() => inner != null && !mini.Destroyed && mini.Spawned && ProtoBoundary.IsLoaded(mini.Map), "the exact packed building is not on this map")
                .Require(() => inner!.Faction == null || inner.Faction == Faction.OfPlayer, "the packed building is not the player's")
                .Require(() => !mini.Position.Fogged(map) && !mini.IsForbidden(Faction.OfPlayer), "the packed building is fogged or forbidden")
                .Require(() => inner!.def.rotatable || rotation == Rot4.North, "the building is not rotatable; only north is valid")
                .Require(() => cell.InBounds(map) && !cell.Fogged(map), "the destination is out of bounds or fogged")
                .Require(() => GenConstruct.CanPlaceBlueprintAt(inner!.def, cell, rotation, map, false, mini, inner).Accepted, "the game refuses an install blueprint at the destination")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Mover(p, mini)), "no free colonist with construction enabled can reach the packed building");
            failure = rules.Holds ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "") : rules.Failure();
            return rules.Holds;
        }

        // Resolve is the apply-time precondition list, one rule at a time so
        // a refusal names the fact that moved. piece is the packed item for a
        // packed install, the building for a reinstall; queued is a blueprint
        // already standing for this exact placement.
        private static Common.Failure? Resolve(Operations.RelocateIntent intent, Common.ObservationContext context, out Thing? piece, out Building? building, out Blueprint_Install? queued)
        {
            piece = null; building = null; queued = null;
            if (!Valid(intent)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A move requires an exact building, a destination cell and a cardinal rotation.");
            var map = ProtoBoundary.LoadedMap(context);
            var cell = new IntVec3(intent.Destination.X, 0, intent.Destination.Z);
            var rotation = Rotation(intent);
            var found = Find(map, intent.ThingId);
            if (found == null && FindPacked(map, intent.ThingId) is MinifiedThing mini)
            {
                piece = mini;
                building = mini.InnerThing as Building;
                queued = InstallBlueprintUtility.ExistingBlueprintFor(mini) as Blueprint_Install;
                if (queued != null) return queued.Position == cell && queued.Rotation == rotation ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Move building refused: the packed building already has an install blueprint elsewhere.");
                return PreparePacked(intent, map, mini, out var packedFailure) ? null : packedFailure;
            }
            if (found != null)
            {
                queued = InstallBlueprintUtility.ExistingBlueprintFor(found) as Blueprint_Install;
                if (queued != null)
                {
                    piece = building = found;
                    return queued.Position == cell && queued.Rotation == rotation ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Move building refused: the building already has a reinstall blueprint elsewhere.");
                }
            }
            var rules = new ApplyPreconditions(Kind)
                .Present(() => found != null && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact building is not installed on this map")
                .Require(() => found!.Faction == Faction.OfPlayer, "the building is not the player's")
                .Require(() => found!.def.Minifiable, "the building cannot be uninstalled")
                .Require(() => found!.def.rotatable || rotation == Rot4.North, "the building is not rotatable; only north is valid")
                .Require(() => !(found!.Position == cell && found.Rotation == rotation), "the building already stands at the destination")
                .Require(() => map.designationManager.DesignationOn(found!, DesignationDefOf.Uninstall) == null
                    && map.designationManager.DesignationOn(found!, DesignationDefOf.Deconstruct) == null, "the building is designated for uninstall or deconstruction")
                .Require(() => cell.InBounds(map) && !cell.Fogged(map), "the destination is out of bounds or fogged")
                .Require(() => GenConstruct.CanPlaceBlueprintAt(found!.def, cell, rotation, map, false, found, found).Accepted, "the game refuses a reinstall blueprint at the destination")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Mover(p, found!)), "no free colonist with construction enabled can reach the building");
            if (!rules.Holds) return rules.Failure();
            piece = building = found;
            return null;
        }

        internal static Common.Failure? Validate(Operations.RelocateIntent? intent, Common.ObservationContext context) =>
            intent == null ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Relocate intent missing.")
            : intent.Uninstall ? NativeUninstallBuilding.Validate(intent, context) : Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.RelocateIntent? intent, Common.ObservationContext context)
        {
            if (intent == null) throw new InvalidOperationException("Relocate intent missing.");
            if (intent.Uninstall) return NativeUninstallBuilding.Apply(intent, context);
            var failure = Resolve(intent, context, out var piece, out var building, out var queued);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            var cell = new IntVec3(intent.Destination.X, 0, intent.Destination.Z);
            var blueprint = queued ?? (piece is MinifiedThing mini
                ? GenConstruct.PlaceBlueprintForInstall(mini, cell, mini.Map, Rotation(intent), Faction.OfPlayer)
                : GenConstruct.PlaceBlueprintForReinstall(building!, cell, building!.Map, Rotation(intent), Faction.OfPlayer));
            if (blueprint == null || !blueprint.Spawned) throw new InvalidOperationException("Native install blueprint was not observed.");
            return new Receipts.EffectEvidence { Installation = new Receipts.InstallationEffect {
                InnerThingId = building!.GetUniqueLoadID(), DefName = building.def.defName, Stuff = building.Stuff?.defName ?? "",
                Cell = new Common.Cell { X = intent.Destination.X, Z = intent.Destination.Z }, Rotation = intent.Rotation,
                Stage = Receipts.InstallationStage.Queued, BlueprintId = blueprint.GetUniqueLoadID() } };
        }
    }

    internal sealed class RelocateActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeMoveBuilding.Validate(action.Relocate, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeMoveBuilding.Apply(action.Relocate, context);
    }
}
