#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The FormCaravanIntent arm of Actions/Apply: form and send one
    // crew with its cargo toward a world tile through the game's own
    // Dialog_FormCaravan calculation, without opening the window.
    // TryFormAndSendCaravan starts a LordJob_FormAndSendCaravan that gathers
    // the cargo and walks the crew to the exit over later ticks, so applied
    // means formation started. A crew already forming together, or already
    // one player caravan, continues its native route without a second formation.
    // Cleared non-home maps use the vanilla reform dialog and its packing rules.
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
                var pawn = Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).ById(id);
                var lord = pawn?.GetLord();
                if (lord == null || !(lord.LordJob is LordJob_FormAndSendCaravan)) return false;
                lords.Add(lord);
            }
            return lords.Count == 1 && ((PlanetTile)typeof(LordJob_FormAndSendCaravan).GetField("destinationTile", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lords.Single().LordJob)!).tileId == command.DestinationTile;
        }

        private static Common.Failure? Arrival(Operations.FormCaravanIntent command, out CaravanArrivalAction? action)
        {
            action = null;
            var objects = Find.WorldObjects.AllWorldObjects.Where(w => w.Spawned && w.Tile.tileId == command.DestinationTile)
                .Where(w => w is Site || w is PeaceTalks || w is MapParent mp && mp.HasMap && mp.Map.IsPlayerHome).ToArray();
            if (objects.Length > 1)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The destination has multiple native arrival targets.");
            if (objects.Length == 0) return null;
            if (objects[0] is PeaceTalks talks) action = new CaravanArrivalAction_VisitPeaceTalks(talks);
            else if (objects[0] is Site site) action = new CaravanArrivalAction_VisitSite(site);
            else action = new CaravanArrivalAction_Enter((MapParent)objects[0]);
            return null;
        }

        // Route is another application of this same owned intent. Cargo is
        // never added to a world caravan here; reform owns the packing write.
        private static Common.Failure? RouteFailure(Operations.FormCaravanIntent command, Caravan caravan)
        {
            var tile = new PlanetTile(command.DestinationTile);
            if (!tile.Valid || tile.tileId >= Find.WorldGrid.TilesCount)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The destination tile is invalid.");
            var failure = Arrival(command, out var arrival);
            if (failure != null) return failure;
            if (arrival != null && !arrival.StillValid(caravan, tile))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The native destination arrival is unavailable.");
            foreach (var requested in command.Cargo)
                if (caravan.PawnsListForReading.Sum(p => p.inventory.innerContainer.Where(t => t.def.defName == requested.DefName).Sum(t => t.stackCount)) < requested.Count)
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A formed caravan cannot acquire new cargo through routing.");
            return null;
        }

        private static void Route(Operations.FormCaravanIntent command, Caravan caravan)
        {
            var failure = RouteFailure(command, caravan);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            Arrival(command, out var arrival);
            var tile = new PlanetTile(command.DestinationTile);
            var current = caravan.pather.ArrivalAction;
            if (caravan.pather.Moving && caravan.pather.Destination == tile
                && current?.GetType() == arrival?.GetType()
                && (current == null || current.StillValid(caravan, tile))) return;
            if (!caravan.pather.StartPath(tile, arrival, repathImmediately: true))
                throw new InvalidOperationException("The caravan did not take its native destination route.");
        }

        // Shelf life left in days; stacks that never rot sort last-to-spoil.
        private static float ShelfDays(TransferableOneWay group) => group.things
            .Select(t => t.TryGetComp<CompRottable>())
            .Where(r => r != null && r.Active)
            .Select(r => GameTime.Days(Math.Max(0f, r!.PropsRot.TicksToRotStart - r.RotProgress)))
            .DefaultIfEmpty(float.MaxValue).Min();

        // Builds the dialog the game would show, selects the crew and fills
        // each cargo definition across its groups (reserve stock first, then
        // longest shelf life), and runs the game's own checks.
        private static Common.Failure? PreparePacking(Operations.FormCaravanIntent command, Common.ObservationContext context, out Dialog_FormCaravan? dialog)
        {
            dialog = null;
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A loaded map is required.");
            var destinationFailure = Arrival(command, out _);
            if (destinationFailure != null) return destinationFailure;
            var reform = !map.IsPlayerHome;
            if (reform && !(map.Parent.GetComponent<FormCaravanComp>()?.CanReformNow() ?? false))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Vanilla caravan reform is unavailable while site threats remain.");
            var built = NativeCaravanCatalog.BuildDialog(map, reform);
            foreach (var id in command.PawnIds)
            {
                var group = built.transferables.SingleOrDefault(g => g.AnyThing is Pawn p && RefIndex.Is(p, id));
                if (!(group?.AnyThing is Pawn pawn) || !NativeCaravanCatalog.PawnEligible(pawn, reform))
                    return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact eligible colonist is unavailable: " + id);
                group.ForceToDestination(1);
            }
            foreach (var item in command.Cargo)
            {
                var remaining = item.Count;
                var groups = built.transferables.Where(g => !(g.AnyThing is Pawn) && g.ThingDef.defName == item.DefName)
                    .OrderByDescending(g => g.things.Any(t => t.Spawned && t.IsForbidden(Faction.OfPlayer))).ThenByDescending(ShelfDays).ToList();
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

            dialog = built;
            return null;
        }

        private static Common.Failure? Prepare(Operations.FormCaravanIntent command, Common.ObservationContext context, out Dialog_FormCaravan? dialog)
        {
            var failure = PreparePacking(command, context, out dialog);
            if (failure != null || dialog == null) return failure;
            var selected = dialog.transferables.Where(g => g.AnyThing is Pawn && g.CountToTransfer > 0).Select(g => (Pawn)g.AnyThing).ToList();
            if (!(bool)NativeCaravanCatalog.Call(dialog, "CheckForErrors", selected)!)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native caravan eligibility refused; inspect game messages.");
            return null;
        }

        internal static RimGovernor.Protocol.Observations.TradePackEstimate Preview(Operations.FormCaravanIntent command, Common.ObservationContext context)
        {
            var row = new RimGovernor.Protocol.Observations.TradePackEstimate();
            if (!Valid(command)) { row.CanPack = false; row.Reason = "invalid_pack"; return row; }
            var failure = PreparePacking(command, context, out var dialog);
            if (failure != null || dialog == null) { row.CanPack = false; row.Reason = failure?.Detail ?? "pack_unavailable"; return row; }
            row.CanPack = true; row.Reason = "packing_ready";
            row.MassUsage = dialog.MassUsage; row.MassCapacity = dialog.MassCapacity;
            var food = NativeCaravanCatalog.FoodDays(dialog);
            if (!float.IsNaN(food.days) && !float.IsInfinity(food.days)) row.FoodDays = food.days;
            if (!float.IsNaN(food.tillRot) && !float.IsInfinity(food.tillRot)) row.FoodRotDays = food.tillRot;
            var map = ProtoBoundary.ResolveMap(context)!;
            var destination = new PlanetTile(command.DestinationTile);
            var crew = dialog.transferables.Where(g => g.AnyThing is Pawn && g.CountToTransfer > 0).Select(g => (Pawn)g.AnyThing).ToList();
            var ticks = CaravanTicksPerMoveUtility.GetTicksPerMove(crew, (float)row.MassUsage, (float)row.MassCapacity);
            row.Outbound = RouteEstimate(map.Tile, destination, ticks, GenTicks.TicksAbs);
            row.Home = RouteEstimate(destination, map.Tile, ticks, GenTicks.TicksAbs + (int)row.Outbound.EstimatedTicks);
            return row;
        }

        private static RimGovernor.Protocol.Observations.WorldRoute RouteEstimate(PlanetTile from, PlanetTile to, int ticksPerMove, int departure)
        {
            var row = new RimGovernor.Protocol.Observations.WorldRoute { Destination = to.tileId };
            using var path = from.Layer.Pather.FindPath(from, to, null);
            row.Reachable = path.Found;
            if (path.Found) row.EstimatedTicks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(from, to, path, 0, ticksPerMove, departure);
            return row;
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
            if (Formed(command!, out var caravan)) return caravan == null ? null : RouteFailure(command!, caravan);
            return Prepare(command!, context, out _);
        }

        internal static Receipts.EffectEvidence Apply(Operations.FormCaravanIntent command, Common.ObservationContext context)
        {
            if (Formed(command, out var existing))
            {
                if (existing != null) Route(command, existing);
                return Evidence(command, existing);
            }
            var failure = Prepare(command, context, out var dialog);
            if (failure != null || dialog == null) throw new InvalidOperationException("Caravan formation prerequisites changed before apply: " + failure?.Detail);
            // TryFormAndSendCaravan returns false without forming when the
            // game's CheckForWarnings raises a concern; it then opens a
            // Dialog_MessageBox whose confirm action forms the caravan, which
            // the player would click. There is no player, so accept it.
            var windowsBefore = Find.WindowStack.Windows.ToArray();
            var reform = !ProtoBoundary.ResolveMap(context)!.IsPlayerHome;
            var accepted = (bool)NativeCaravanCatalog.Call(dialog, reform ? "TryReformCaravan" : "TryFormAndSendCaravan")!;
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
            if (caravan != null) Route(command, caravan);
            return Evidence(command, caravan);
        }
    }

    internal sealed class FormCaravanActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeCaravanOperations.Validate(action.FormCaravan, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeCaravanOperations.Apply(action.FormCaravan, context);
    }
}
