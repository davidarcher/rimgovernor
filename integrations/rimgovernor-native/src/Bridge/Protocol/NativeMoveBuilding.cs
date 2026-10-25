using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // RelocateIntent on Actions/Apply. A reinstall
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

        private static bool Valid(RimGovernor.Brain.GameOrders.Operations.RelocateIntent intent) => GameThread.IsIdentifier(intent.ThingId)
            && intent.Mode is RimGovernor.Brain.GameOrders.Operations.RelocationMode.Move
            && ((RimGovernor.Brain.GameOrders.Operations.RelocationMode.Move)intent.Mode).Value >= RimGovernor.Brain.GameFacts.Placement.Rotation.North && ((RimGovernor.Brain.GameOrders.Operations.RelocationMode.Move)intent.Mode).Value <= RimGovernor.Brain.GameFacts.Placement.Rotation.West;

        private static RimGovernor.Brain.Domain.Cell Destination(RimGovernor.Brain.GameOrders.Operations.RelocateIntent intent) => intent.Mode is RimGovernor.Brain.GameOrders.Operations.RelocationMode.Move move ? move.Destination : throw new InvalidOperationException("Relocation is not a move.");

        private static Rot4 Rotation(RimGovernor.Brain.GameOrders.Operations.RelocateIntent intent) => new Rot4((int)((RimGovernor.Brain.GameOrders.Operations.RelocationMode.Move)intent.Mode).Value - 1);

        private static bool PreparePacked(RimGovernor.Brain.GameOrders.Operations.RelocateIntent intent, Map map, MinifiedThing mini, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            var cell = new IntVec3(Destination(intent).X, 0, Destination(intent).Z);
            var rotation = Rotation(intent);
            var inner = mini.InnerThing;
            var rules = new ApplyPreconditions(Kind)
                .Present(() => inner != null && !mini.Destroyed && mini.Spawned && GameThread.IsLoaded(mini.Map), "the exact packed building is not on this map")
                .Require(() => inner.Faction == null || inner.Faction == Faction.OfPlayer, "the packed building is not the player's")
                .Require(() => GenConstruct.CanPlaceBlueprintAt(inner.def, cell, rotation, map, false, mini, inner).Accepted, "the game refuses an install blueprint at the destination");
            failure = rules.Holds ? GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "") : rules.Failure();
            return rules.Holds;
        }

        // Resolve is the apply-time precondition list, one rule at a time so
        // a refusal names the fact that moved. piece is the packed item for a
        // packed install, the building for a reinstall; queued is a blueprint
        // already standing for this exact placement.
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.RelocateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Thing? piece, out Thing? building, out Blueprint_Install? queued)
        {
            piece = null; building = null; queued = null;
            if (!Valid(intent)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A move requires an exact building, a destination cell and a cardinal rotation.");
            var map = GameThread.LoadedMap(context);
            var cell = new IntVec3(Destination(intent).X, 0, Destination(intent).Z);
            var rotation = Rotation(intent);
            var found = Find(map, (intent.ThingId ?? ""));
            if (found == null && FindPacked(map, (intent.ThingId ?? "")) is MinifiedThing mini)
            {
                piece = mini;
                building = mini.InnerThing;
                queued = InstallBlueprintUtility.ExistingBlueprintFor(mini) as Blueprint_Install;
                if (queued != null) return queued.Position == cell && queued.Rotation == rotation ? null : GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Move building refused: the packed building already has an install blueprint elsewhere.");
                return PreparePacked(intent, map, mini, out var packedFailure) ? null : packedFailure;
            }
            if (found != null)
            {
                queued = InstallBlueprintUtility.ExistingBlueprintFor(found) as Blueprint_Install;
                if (queued != null)
                {
                    piece = building = found;
                    return queued.Position == cell && queued.Rotation == rotation ? null : GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Move building refused: the building already has a reinstall blueprint elsewhere.");
                }
            }
            var rules = new ApplyPreconditions(Kind)
                .Present(() => found != null && !found.Destroyed && found.Spawned && GameThread.IsLoaded(found.Map), "the exact building is not installed on this map")
                .Require(() => found != null && (found.Faction == Faction.OfPlayer), "the building is not the player's")
                .Require(() => found != null && (found.def.Minifiable), "the building cannot be uninstalled")
                .Require(() => map.designationManager.DesignationOn(found, DesignationDefOf.Uninstall) == null
                    && map.designationManager.DesignationOn(found, DesignationDefOf.Deconstruct) == null, "the building is designated for uninstall or deconstruction")
                .Require(() => found != null && (GenConstruct.CanPlaceBlueprintAt(found.def, cell, rotation, map, false, found, found).Accepted), "the game refuses a reinstall blueprint at the destination");
            if (!rules.Holds) return rules.Failure();
            piece = building = found;
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.RelocateIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) =>
            intent == null ? GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Relocate intent missing.")
            : intent.Mode is RimGovernor.Brain.GameOrders.Operations.RelocationMode.Uninstall ? NativeUninstallBuilding.Validate(intent, context) : Resolve(intent, context, out _, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.RelocateIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            if (intent == null) throw new InvalidOperationException("Relocate intent missing.");
            if (intent.Mode is RimGovernor.Brain.GameOrders.Operations.RelocationMode.Uninstall) return NativeUninstallBuilding.Apply(intent, context);
            var failure = Resolve(intent, context, out var piece, out var building, out var queued);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            var cell = new IntVec3(Destination(intent).X, 0, Destination(intent).Z);
            var blueprint = queued ?? (piece is MinifiedThing mini
                ? GenConstruct.PlaceBlueprintForInstall(mini, cell, mini.Map, Rotation(intent), Faction.OfPlayer)
                : GenConstruct.PlaceBlueprintForReinstall((Building)(building ?? throw new System.InvalidOperationException("Required native value is unavailable: building.")), cell, (building ?? throw new System.InvalidOperationException("Required native value is unavailable: building.")).Map, Rotation(intent), Faction.OfPlayer));
            if (blueprint == null || !blueprint.Spawned) throw new InvalidOperationException("Native install blueprint was not observed.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect.InstallationCase() };
        }
    }

    internal sealed class RelocateActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeMoveBuilding.Validate(action.Intent?.Relocate, context);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeMoveBuilding.Apply(action.Intent?.Relocate, context);
    }
}
