using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimGovernor.Native
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
        private static bool Valid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent? command) => command != null
            && Enum.IsDefined(typeof(RimGovernor.Brain.GameOrders.Operations.CaravanMode), command.Mode)
            && command.PawnIds.Count > 0
            && command.PawnIds.All(GameThread.IsIdentifier)
            && command.PawnIds.Distinct().Count() == command.PawnIds.Count
            && command.Cargo.All(c => c.DefName is not null && GameThread.IsIdentifier(c.DefName) && c.Count > 0)
            && command.Cargo.Select(c => (c.DefName ?? "")).Distinct().Count() == command.Cargo.Count
            && command.DestinationTile >= 0;

        private static string[] Sorted(IEnumerable<string> ids) => ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();

        // The crew's existing formation: every member in one formation lord,
        // or the whole crew one player caravan.
        private static bool Formed(RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent command, out Caravan? caravan)
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
            return lords.Count == 1 && ((PlanetTile)((typeof(LordJob_FormAndSendCaravan).GetField("destinationTile", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable.")).GetValue(lords.Single().LordJob) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."))).tileId == command.DestinationTile;
        }

        private static RimGovernor.Brain.GameFacts.Common.Failure? Arrival(RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent command, out CaravanArrivalAction? action)
        {
            action = null;
            var objects = Find.WorldObjects.AllWorldObjects.Where(w => w.Spawned && w.Tile.tileId == command.DestinationTile)
                .Where(w => w is Site || w is PeaceTalks || w is MapParent mp && mp.HasMap && mp.Map.IsPlayerHome).ToArray();
            if (objects.Length > 1)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The destination has multiple native arrival targets.");
            if (objects.Length == 0) return null;
            if (objects[0] is PeaceTalks talks) action = new CaravanArrivalAction_VisitPeaceTalks(talks);
            else if (objects[0] is Site site) action = new CaravanArrivalAction_VisitSite(site);
            else action = new CaravanArrivalAction_Enter((MapParent)objects[0]);
            return null;
        }

        // Route is another application of this same owned intent. Cargo is
        // never added to a world caravan here; reform owns the packing write.
        private static RimGovernor.Brain.GameFacts.Common.Failure? RouteFailure(RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent command, Caravan caravan)
        {
            var tile = new PlanetTile(command.DestinationTile);
            if (!tile.Valid || tile.tileId >= Find.WorldGrid.TilesCount)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The destination tile is invalid.");
            var failure = Arrival(command, out var arrival);
            if (failure != null) return failure;
            if (arrival != null && !arrival.StillValid(caravan, tile))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The native destination arrival is unavailable.");
            foreach (var requested in command.Cargo)
                if (caravan.PawnsListForReading.Sum(p => p.inventory.innerContainer.Where(t => t.def.defName == (requested.DefName ?? "")).Sum(t => t.stackCount)) < requested.Count)
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A formed caravan cannot acquire new cargo through routing.");
            return null;
        }

        private static void Route(RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent command, Caravan caravan)
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
            .Select(r => GameTime.Days(Math.Max(0f, r.PropsRot.TicksToRotStart - r.RotProgress)))
            .DefaultIfEmpty(float.MaxValue).Min();

        // Builds the dialog the game would show, selects the crew and fills
        // each cargo definition across its groups (reserve stock first, then
        // longest shelf life), and runs the game's own checks.
        private static RimGovernor.Brain.GameFacts.Common.Failure? PreparePacking(RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent command, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Dialog_FormCaravan? dialog)
        {
            dialog = null;
            var map = GameThread.ResolveMap(context) ?? throw new InvalidOperationException("The caravan map is unavailable.");
            if (map == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "A loaded map is required.");
            var destinationFailure = Arrival(command, out _);
            if (destinationFailure != null) return destinationFailure;
            var reform = !map.IsPlayerHome;
            if (command.Mode == RimGovernor.Brain.GameOrders.Operations.CaravanMode.ExistingRoute || (command.Mode == RimGovernor.Brain.GameOrders.Operations.CaravanMode.SiteReform) != reform)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Caravan mode does not match the observed map; routing cannot create a formation.");
            if (reform && !(map.Parent.GetComponent<FormCaravanComp>()?.CanReformNow() ?? false))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Vanilla caravan reform is unavailable while site threats remain.");
            var built = NativeCaravanCatalog.BuildDialog(map, reform);
            foreach (var id in command.PawnIds)
            {
                var group = built.transferables.SingleOrDefault(g => g.AnyThing is Pawn p && RefIndex.Is(p, id));
                if (!(group?.AnyThing is Pawn pawn) || !NativeCaravanCatalog.PawnEligible(pawn, reform))
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact eligible colonist is unavailable: " + id);
                group.ForceToDestination(1);
            }
            foreach (var item in command.Cargo)
            {
                var remaining = item.Count;
                var groups = built.transferables.Where(g => !(g.AnyThing is Pawn) && g.ThingDef.defName == (item.DefName ?? ""))
                    .OrderByDescending(g => g.things.Any(t => t.Spawned && t.IsForbidden(Faction.OfPlayer))).ThenByDescending(ShelfDays).ToList();
                foreach (var group in groups)
                {
                    if (remaining == 0) break;
                    var take = Math.Min(remaining, group.MaxCount);
                    group.ForceToDestination(take);
                    remaining -= take;
                }
                if (remaining > 0)
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Not enough " + (item.DefName ?? "") + " is available to pack.");
            }
            var tile = new PlanetTile(command.DestinationTile);
            if (!tile.Valid || tile.tileId >= Find.WorldGrid.TilesCount || tile == map.Tile)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Choose a different valid surface tile.");
            var exit = CaravanExitMapUtility.BestExitTileToGoTo(tile, map);
            if (!exit.Valid) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "No native caravan exit route.");
            NativeCaravanCatalog.Set(built, "destinationTile", tile);
            NativeCaravanCatalog.Set(built, "startingTile", exit);
            NativeCaravanCatalog.Call(built, "Notify_TransferablesChanged");

            dialog = built;
            return null;
        }

        private static RimGovernor.Brain.GameFacts.Common.Failure? Prepare(RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent command, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Dialog_FormCaravan? dialog)
        {
            var failure = PreparePacking(command, context, out dialog);
            if (failure != null || dialog == null) return failure;
            var selected = dialog.transferables.Where(g => g.AnyThing is Pawn && g.CountToTransfer > 0).Select(g => (Pawn)g.AnyThing).ToList();
            if (!(bool)(NativeCaravanCatalog.Call(dialog, "CheckForErrors", selected) ?? throw new InvalidOperationException("The caravan error check returned no result.")))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Native caravan eligibility refused; inspect game messages.");
            return null;
        }

        // The pack is built whole: a pack the game refuses is Unpackable, and a mass or food figure the dialog cannot
        // give as a finite number is a read failure, never an omitted field.
        internal static RimGovernor.Brain.GameFacts.Observations.TradePackEstimate Preview(RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent command, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            if (!Valid(command)) return new RimGovernor.Brain.GameFacts.Observations.TradePackEstimate.Unpackable();
            var failure = PreparePacking(command, context, out var dialog);
            if (failure != null || dialog == null) return new RimGovernor.Brain.GameFacts.Observations.TradePackEstimate.Unpackable();
            var massUsage = Finite(dialog.MassUsage, "mass usage");
            var massCapacity = Finite(dialog.MassCapacity, "mass capacity");
            var food = NativeCaravanCatalog.FoodDays(dialog);
            var map = GameThread.ResolveMap(context) ?? throw new InvalidOperationException("The caravan map is unavailable.");
            var destination = new PlanetTile(command.DestinationTile);
            var crew = dialog.transferables.Where(g => g.AnyThing is Pawn && g.CountToTransfer > 0).Select(g => (Pawn)g.AnyThing).ToList();
            var ticks = CaravanTicksPerMoveUtility.GetTicksPerMove(crew, massUsage, massCapacity);
            var outbound = RouteEstimate(map.Tile, destination, ticks, GenTicks.TicksAbs);
            var home = RouteEstimate(destination, map.Tile, ticks, GenTicks.TicksAbs + (outbound is RimGovernor.Brain.GameFacts.Observations.PackRoute.Reachable reach ? (int)reach.EstimatedTicks : 0));
            return new RimGovernor.Brain.GameFacts.Observations.TradePackEstimate.Packed(massUsage, massCapacity, Finite(food.days, "food days"), Finite(food.tillRot, "food rot days"), outbound, home)
            {
                SettlementTrade = SettlementPreview(destination, crew.Single(p => RefIndex.Is(p, command.PawnIds[0]))),
            };
        }

        // Goods/StockListForReading would generate stock. Read the held pool without mutating the world instead.
        private static RimGovernor.Brain.GameFacts.Observations.SettlementTradePreview? SettlementPreview(PlanetTile destination, Pawn negotiator)
        {
            var settlement = Find.WorldObjects.Settlements.SingleOrDefault(s => s.Tile == destination && !s.Faction.IsPlayer);
            if (settlement?.TraderKind is not { } kind) return null;
            var held = settlement.trader.GetDirectlyHeldThings();
            var fresh = held == null || held.Count == 0;
            var gain = negotiator.GetStatValue(StatDefOf.TradePriceImprovement);
            var rows = fresh ? Array.Empty<RimGovernor.Brain.GameFacts.Observations.SettlementStockItem>()
                : held!.Cast<Thing>().Where(t => t is not Pawn && kind.WillTrade(t.def) && t.def.tradeability.TraderCanSell())
                    .Select(t => new RimGovernor.Brain.GameFacts.Observations.SettlementStockItem(t.def.defName, t.stackCount,
                        TradeUtility.GetPricePlayerBuy(t, kind.PriceTypeFor(t.def, TradeAction.PlayerBuys).PriceMultiplier(), 1, gain, settlement.TradePriceImprovementOffsetForPlayer))).ToArray();
            return new(settlement.GetUniqueLoadID(), kind.defName, fresh,
                fresh ? 0 : Math.Max(0, (long)settlement.NextRestockTick + 1 - Find.TickManager.TicksGame),
                (1 + Find.Storyteller.difficulty.tradePriceFactorLoss) * (1 - gain - settlement.TradePriceImprovementOffsetForPlayer), [.. rows]);
        }

        private static float Finite(float value, string what) =>
            float.IsNaN(value) || float.IsInfinity(value) ? throw new InvalidOperationException("The caravan pack " + what + " is not a finite number.") : value;

        private static RimGovernor.Brain.GameFacts.Observations.PackRoute RouteEstimate(PlanetTile from, PlanetTile to, int ticksPerMove, int departure)
        {
            using var path = from.Layer.Pather.FindPath(from, to, null);
            return path.Found
                ? new RimGovernor.Brain.GameFacts.Observations.PackRoute.Reachable(CaravanArrivalTimeEstimator.EstimatedTicksToArrive(from, to, path, 0, ticksPerMove, departure))
                : new RimGovernor.Brain.GameFacts.Observations.PackRoute.Unreachable();
        }
        private static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Evidence(RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent command, Caravan? caravan)
        {
            var effect = new RimGovernor.Brain.GameFacts.Receipts.CaravanEffect { AssemblyStarted = true, DestinationTile = command.DestinationTile };
            effect = effect with { PawnIds = [.. effect.PawnIds, .. command.PawnIds] };
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = effect };
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent? command, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            if (!Valid(command))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Formation requires a unique crew, unique cargo definitions and a valid destination.");
            if (Formed(command, out var caravan))
                return caravan != null ? RouteFailure(command, caravan)
                    : command.Mode == RimGovernor.Brain.GameOrders.Operations.CaravanMode.Formation ? null
                    : GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Requested route requires a formed world caravan.");
            return Prepare(command, context, out _);
        }

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent command, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var currentFailure = Validate(command, context);
            if (currentFailure != null) throw new InvalidOperationException(currentFailure.Detail);
            if (Formed(command, out var existing))
            {
                if (existing != null) Route(command, existing);
                return Evidence(command, existing);
            }
            var failure = Prepare(command, context, out var dialog);
            if (failure != null || dialog == null) throw new InvalidOperationException("Caravan formation prerequisites changed before apply: " + (failure?.Detail ?? ""));
            // TryFormAndSendCaravan returns false without forming when the
            // game's CheckForWarnings raises a concern; it then opens a
            // Dialog_MessageBox whose confirm action forms the caravan, which
            // the player would click. There is no player, so accept it.
            var windowsBefore = Find.WindowStack.Windows.ToArray();
            var reform = !(GameThread.ResolveMap(context) ?? throw new InvalidOperationException("The caravan map is unavailable.")).IsPlayerHome;
            var accepted = (bool)(NativeCaravanCatalog.Call(dialog, reform ? "TryReformCaravan" : "TryFormAndSendCaravan") ?? throw new InvalidOperationException("The caravan formation returned no result."));
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
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeCaravanOperations.Validate(action.Intent?.FormCaravan, context);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeCaravanOperations.Apply(action.Intent?.FormCaravan ?? throw new InvalidOperationException("Caravan intent is missing."), context);
    }
}
