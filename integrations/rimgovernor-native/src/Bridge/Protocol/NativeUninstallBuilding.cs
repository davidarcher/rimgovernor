#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Placement = RimGovernor.Protocol.Placement;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The uninstall half of RelocateIntent (#843, #940): the game's
    // Uninstall designation on one exact installed player building, the
    // write Designator_Uninstall makes. Ordinary construction work
    // (WorkGiver_Uninstall) then minifies the piece where it stands; it needs
    // CanReserve on the piece, so a pawn sleeping in a bed is never
    // interrupted. Vanilla hauling takes the packed item to storage. A
    // designation already on the building applies again. The effect is the
    // InstallationEffect at the building's current placement.
    internal static class NativeUninstallBuilding
    {
        internal const string Kind = "Uninstall building";

        private static Common.Failure? Resolve(Operations.RelocateIntent intent, Common.ObservationContext context, out Building? building)
        {
            building = null;
            if (!ProtoBoundary.IsIdentifier(intent.ThingId)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Uninstall requires an exact building.");
            var map = ProtoBoundary.LoadedMap(context);
            var found = NativeMoveBuilding.Find(map, intent.ThingId);
            var rules = new ApplyPreconditions(Kind)
                .Present(() => found != null && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact building is not installed on this map")
                .Require(() => found!.Faction == Faction.OfPlayer, "the building is not the player's")
                .Require(() => found!.def.Minifiable, "the building cannot be uninstalled")
                .Require(() => InstallBlueprintUtility.ExistingBlueprintFor(found!) == null, "the building has a reinstall blueprint")
                .Require(() => map.designationManager.DesignationOn(found!, DesignationDefOf.Deconstruct) == null, "the building is designated for deconstruction")
                .Require(() => map.designationManager.DesignationOn(found!, DesignationDefOf.Uninstall) != null
                    || map.mapPawns.FreeColonistsSpawned.Any(p => NativeMoveBuilding.Mover(p, found!)), "no free colonist with construction enabled can reach the building");
            if (!rules.Holds) return rules.Failure();
            building = found;
            return null;
        }

        internal static Common.Failure? Validate(Operations.RelocateIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static Receipts.EffectEvidence Apply(Operations.RelocateIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var building);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            var designations = building!.Map.designationManager;
            if (designations.DesignationOn(building, DesignationDefOf.Uninstall) == null)
                designations.AddDesignation(new Designation(building, DesignationDefOf.Uninstall));
            if (designations.DesignationOn(building, DesignationDefOf.Uninstall) == null) throw new InvalidOperationException("Native uninstall designation was not observed.");
            return new Receipts.EffectEvidence { Installation = new Receipts.InstallationEffect {
                InnerThingId = building.GetUniqueLoadID(), DefName = building.def.defName, Stuff = building.Stuff?.defName ?? "",
                Cell = new Common.Cell { X = building.Position.x, Z = building.Position.z },
                Rotation = (Placement.Rotation)(building.Rotation.AsInt + 1), Stage = Receipts.InstallationStage.UninstallQueued } };
        }
    }
}
