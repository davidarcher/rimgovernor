#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The FormCaravanIntent arm of Actions/Apply (#942): form and send one
    // crew with its cargo toward a world tile through the game's own
    // Dialog_FormCaravan calculation, without opening the window.
    // TryFormAndSendCaravan starts a LordJob_FormAndSendCaravan that gathers
    // the cargo and walks the crew to the exit over later ticks, so applied
    // means formation started. A crew already forming together, or already
    // one player caravan, is applied again without a second formation.
    internal static class NativeCaravanOperations
    {
        private static bool Valid(Operations.FormCaravanIntent? command) => command != null
            && command.PawnIds.Count > 0
            && command.PawnIds.All(ProtoBoundary.IsIdentifier)
            && command.PawnIds.Distinct().Count() == command.PawnIds.Count
            && command.Cargo.All(c => c.HasDefName && ProtoBoundary.IsIdentifier(c.DefName) && c.HasCount && c.Count > 0)
            && command.Cargo.Select(c => c.DefName).Distinct().Count() == command.Cargo.Count
            && command.HasDestinationTile && command.DestinationTile >= 0;

        private static string[] Sorted(IEnumerable<string> ids) => ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();

        // The crew's existing formation: every member in one formation lord,
        // or the whole crew one player caravan.
        private static bool Formed(Operations.FormCaravanIntent command, out Caravan? caravan)
        {
            var crew = Sorted(command.PawnIds);
            caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => c.IsPlayerControlled
                && Sorted(c.PawnsListForReading.Select(p => p.GetUniqueLoadID())).SequenceEqual(crew));
            if (caravan != null) return true;
            var lords = new HashSet<Lord>();
            foreach (var id in command.PawnIds)
            {
                var pawn = Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).FirstOrDefault(p => p.GetUniqueLoadID() == id);
                var lord = pawn?.GetLord();
                if (lord == null || !(lord.LordJob is LordJob_FormAndSendCaravan)) return false;
                lords.Add(lord);
            }
            return lords.Count == 1;
        }

        // Shelf life left in days; stacks that never rot sort last-to-spoil.
        private static float ShelfDays(TransferableOneWay group) => group.things
            .Select(t => t.TryGetComp<CompRottable>())
            .Where(r => r != null && r.Active)
            .Select(r => Math.Max(0f, r!.PropsRot.TicksToRotStart - r.RotProgress) / 60000f)
            .DefaultIfEmpty(float.MaxValue).Min();

        // Builds the dialog the game would show, selects the crew and fills
        // each cargo definition across its groups (reserve stock first, then
        // longest shelf life), and runs the game's own checks.
        private static Common.Failure? Prepare(Operations.FormCaravanIntent command, Common.ObservationContext context, out Dialog_FormCaravan? dialog)
        {
            dialog = null;
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A loaded map is required.");
            var built = NativeCaravanCatalog.BuildDialog(map);
            var selected = new List<Pawn>();
            foreach (var id in command.PawnIds)
            {
                var group = built.transferables.SingleOrDefault(g => g.AnyThing is Pawn p && p.GetUniqueLoadID() == id);
                if (!(group?.AnyThing is Pawn pawn) || !NativeCaravanCatalog.PawnEligible(pawn))
                    return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact eligible home colonist is unavailable: " + id);
                selected.Add(pawn);
                group.ForceToDestination(1);
            }
            if (map.mapPawns.FreeColonistsSpawned.Count <= selected.Count)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "At least one colonist must remain home.");
            foreach (var item in command.Cargo)
            {
                var remaining = item.Count;
                var groups = built.transferables.Where(g => !(g.AnyThing is Pawn) && g.ThingDef.defName == item.DefName)
                    .OrderByDescending(g => g.things.Any(FoodSupplyFacts.IsReserve)).ThenByDescending(ShelfDays).ToList();
                foreach (var group in groups)
                {
                    if (remaining == 0) break;
                    var take = Math.Min(remaining, group.MaxCount);
                    group.ForceToDestination(take);
                    remaining -= take;
                }
                if (remaining > 0)
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Not enough " + item.DefName + " is available to pack.");
            }
            var tile = new PlanetTile(command.DestinationTile);
            if (!tile.Valid || tile.tileId >= Find.WorldGrid.TilesCount || tile == map.Tile)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Choose a different valid surface tile.");
            var exit = CaravanExitMapUtility.BestExitTileToGoTo(tile, map);
            if (!exit.Valid) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "No native caravan exit route.");
            NativeCaravanCatalog.Set(built, "destinationTile", tile);
            NativeCaravanCatalog.Set(built, "startingTile", exit);
            NativeCaravanCatalog.Call(built, "Notify_TransferablesChanged");
            if (built.MassUsage > built.MassCapacity)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cargo exceeds native carrying capacity.");
            if (NativeCaravanCatalog.FoodDays(built).days < 1f)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "At least one native day of caravan food is required.");
            // Emits refusal messages, but never creates a lord or moves cargo.
            if (!(bool)NativeCaravanCatalog.Call(built, "CheckForErrors", selected)!)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native caravan eligibility refused; inspect game messages.");
            dialog = built;
            return null;
        }

        private static Receipts.EffectEvidence Evidence(Operations.FormCaravanIntent command, Caravan? caravan)
        {
            var effect = new Receipts.CaravanEffect { AssemblyStarted = true, PathStarted = caravan != null && caravan.pather.Moving, DestinationTile = command.DestinationTile };
            if (caravan != null) effect.CaravanId = caravan.GetUniqueLoadID();
            effect.PawnIds.Add(command.PawnIds);
            return new Receipts.EffectEvidence { Caravan = effect };
        }

        internal static Common.Failure? Validate(Operations.FormCaravanIntent? command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Formation requires a unique crew, unique cargo definitions and a valid destination.");
            return Formed(command!, out _) ? null : Prepare(command!, context, out _);
        }

        internal static Receipts.EffectEvidence Apply(Operations.FormCaravanIntent command, Common.ObservationContext context)
        {
            if (Formed(command, out var existing)) return Evidence(command, existing);
            var failure = Prepare(command, context, out var dialog);
            if (failure != null || dialog == null) throw new InvalidOperationException("Caravan formation prerequisites changed before apply: " + failure?.Detail);
            // TryFormAndSendCaravan returns false without forming when the
            // game's CheckForWarnings raises a concern; it then opens a
            // Dialog_MessageBox whose confirm action forms the caravan, which
            // the player would click. There is no player, so accept it.
            var windowsBefore = Find.WindowStack.Windows.ToArray();
            var accepted = (bool)NativeCaravanCatalog.Call(dialog, "TryFormAndSendCaravan")!;
            if (!accepted)
            {
                var confirmation = Find.WindowStack.Windows.Where(w => !windowsBefore.Contains(w)).OfType<Dialog_MessageBox>().SingleOrDefault();
                if (confirmation?.buttonAAction != null)
                {
                    try { confirmation.buttonAAction(); }
                    finally { confirmation.Close(); }
                }
            }
            if (!Formed(command, out var caravan)) throw new InvalidOperationException("Native caravan formation readback did not apply.");
            return Evidence(command, caravan);
        }
    }

    internal sealed class FormCaravanActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeCaravanOperations.Validate(action.FormCaravan, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeCaravanOperations.Apply(action.FormCaravan, context);
    }
}
