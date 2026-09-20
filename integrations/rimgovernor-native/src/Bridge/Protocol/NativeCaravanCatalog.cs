#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Shared native FormCaravan dialog calculation used by both the
    // CaravanCatalog observation read and NativeCaravanOperations'
    // FormCaravan execute/preview. Ports legacy CaravanTools.Run's
    // Dialog_FormCaravan-without-UI pattern (home/caravan) behind the typed
    // boundary: same private CalculateAndRecacheTransferables call, no
    // window ever opens, no camera ownership is taken. Stateless: every
    // read recomputes from current native state, per native-static-state.md.
    internal static class NativeCaravanCatalog
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal const string ToolName = "rimgovernor/observations_read_caravan_catalog";

        internal static object? Call(Dialog_FormCaravan dialog, string name, params object[] args) =>
            typeof(Dialog_FormCaravan).GetMethod(name, Private)!.Invoke(dialog, args);
        internal static void Set(Dialog_FormCaravan dialog, string name, object value) =>
            typeof(Dialog_FormCaravan).GetField(name, Private)!.SetValue(dialog, value);
        internal static (float days, float tillRot) FoodDays(Dialog_FormCaravan dialog) =>
            ((float, float))typeof(Dialog_FormCaravan).GetProperty("DaysWorthOfFood", Private)!.GetValue(dialog)!;

        // A stable ordinal-smallest constituent ThingID; used as both
        // FormCaravan.Cargo's group_id and this catalog's CargoGroup.group_id.
        // RimWorld's TransferableOneWay grouping merges non-pawn rows by def,
        // stuff, quality, ingredients, rot stage and hit points (ten apart), so
        // one def may span several rows; only the group id is unique.
        internal static string GroupId(TransferableOneWay group) =>
            group.things.Select(t => t.ThingID).OrderBy(id => id, StringComparer.Ordinal).First();

        // Native dialog calculation without opening UI or taking camera
        // ownership; mirrors legacy CaravanTools.Execute.
        internal static Dialog_FormCaravan BuildDialog(Map map)
        {
            var dialog = new Dialog_FormCaravan(map);
            Set(dialog, "autoSelectTravelSupplies", false);
            Call(dialog, "CalculateAndRecacheTransferables");
            foreach (var group in dialog.transferables) group.ForceToDestination(0);
            return dialog;
        }

        // Mirrors legacy CaravanTools.Execute's per-pawn eligibility check.
        internal static bool PawnEligible(Pawn pawn) => pawn.IsFreeColonist && !pawn.Downed && !pawn.Dead
            && !pawn.Drafted && !pawn.InMentalState && pawn.GetLord() == null;

        internal static List<Obs.CaravanPawnEligibility> PawnRows(Map map, Common.ObservationContext context)
        {
            var rows = new List<Obs.CaravanPawnEligibility>();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                var eligible = PawnEligible(pawn);
                rows.Add(new Obs.CaravanPawnEligibility
                {
                    Pawn = NativeObservationTools.PawnRow(pawn, false, context),
                    Available = eligible,
                    Reason = eligible ? "" : "Colonist is unavailable, drafted, dead, downed or already assigned.",
                });
            }
            return rows;
        }

        internal static List<Obs.CargoGroup> CargoGroups(Map map, Dialog_FormCaravan dialog)
        {
            var rows = new List<Obs.CargoGroup>();
            var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => p.needs?.food != null).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            foreach (var group in dialog.transferables.Where(g => !(g.AnyThing is Pawn)))
            {
                var row = new Obs.CargoGroup { GroupId = GroupId(group), DefName = group.ThingDef.defName, Count = group.MaxCount };
                try { row.Mass = group.AnyThing.GetStatValue(StatDefOf.Mass) * group.MaxCount; }
                catch (Exception) { /* Mass is a diagnostic extra; omission does not affect catalog freshness. */ }
                FoodFacts(row, group, colonists);
                rows.Add(row);
            }
            return rows;
        }

        // Food facts (#464) feed Go's SelectCaravanFood: per-unit nutrition,
        // the shortest unrefrigerated shelf life across the group's stacks
        // (rot progresses at full rate above 10 C, which is what a caravan
        // carrying the food sees), the reserve flag FoodSupplyFacts.IsReserve
        // defines, and the colonists whose diet and food restriction admit the
        // food. The group merges stacks by def, so a group holding any held
        // reserve stack reports reserve. None of these join the catalog token:
        // rot advances every tick and would refuse every admission.
        private static void FoodFacts(Obs.CargoGroup row, TransferableOneWay group, List<Pawn> colonists)
        {
            var thing = group.AnyThing;
            var def = thing.def;
            if (def.category != ThingCategory.Item || !def.IsNutritionGivingIngestible || def.IsDrug || def.IsCorpse) return;
            var nutrition = thing.GetStatValue(StatDefOf.Nutrition);
            if (float.IsNaN(nutrition) || float.IsInfinity(nutrition) || nutrition <= 0f) return;
            row.Nutrition = nutrition;
            var perishable = false;
            float? rotDays = null;
            var reserve = false;
            foreach (var stack in group.things)
            {
                var rot = stack.TryGetComp<CompRottable>();
                if (rot != null && rot.Active)
                {
                    perishable = true;
                    var remaining = Math.Max(0f, rot.PropsRot.TicksToRotStart - rot.RotProgress) / 60000f;
                    if (rotDays == null || remaining < rotDays.Value) rotDays = remaining;
                }
                if (FoodSupplyFacts.IsReserve(stack)) reserve = true;
            }
            row.Perishable = perishable;
            if (perishable && rotDays.HasValue && !float.IsNaN(rotDays.Value) && !float.IsInfinity(rotDays.Value)) row.RotDays = rotDays.Value;
            row.Reserve = reserve;
            foreach (var pawn in colonists)
                if (pawn.WillEat(thing) && FoodSupplyFacts.PolicyAllows(pawn, thing)) row.EaterIds.Add(pawn.GetUniqueLoadID());
        }

        // Freshness token covering both home-colonist eligibility and cargo
        // availability -- FormCaravan carries no per-pawn precondition (unlike
        // ImproveGear's EntityPrecondition), so the catalog token is the only
        // CAS guard a formation admission gets.
        internal static string Token(Common.ObservationContext context, List<Obs.CargoGroup> cargo, List<Obs.CaravanPawnEligibility> pawns)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(context.Identity.ColonyId); writer.Write(context.Identity.LoadToken); writer.Write(context.Identity.MapId);
                    foreach (var group in cargo.OrderBy(g => g.GroupId, StringComparer.Ordinal))
                    { writer.Write(group.GroupId); writer.Write(group.DefName); writer.Write(group.Count); }
                    foreach (var row in pawns.OrderBy(p => p.Pawn.Pawn.Id, StringComparer.Ordinal))
                    { writer.Write(row.Pawn.Pawn.Id); writer.Write(row.Available); }
                }
                using (var hash = SHA256.Create())
                    return "caravan-catalog-" + BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        // Mirrors legacy CaravanTools.RouteFacts, without a caravan object and
        // using the catalog dialog's baseline (unselected) pack; no crew/cargo
        // selection exists yet at catalog time, so the ticks-per-move estimate
        // is best-effort and omitted (not fabricated) if the native utility
        // cannot compute it from an empty pack.
        internal static (Obs.WorldRoute route, Settlement? settlement) RouteFacts(Map map, Dialog_FormCaravan dialog, int destinationTile)
        {
            var target = new PlanetTile(destinationTile);
            var route = new Obs.WorldRoute { Destination = destinationTile };
            if (!target.Valid || target.tileId >= Find.WorldGrid.TilesCount || target == map.Tile)
            {
                route.Reachable = false;
                route.Reason = "Destination is invalid or is the current map's own tile.";
                return (route, null);
            }
            var settlement = Find.WorldObjects.Settlements.SingleOrDefault(s => s.Tile == target);
            using (var path = map.Tile.Layer.Pather.FindPath(map.Tile, target, null))
            {
                route.Reachable = path.Found;
                if (path.Found)
                {
                    try
                    {
                        var ticksPerMove = CaravanTicksPerMoveUtility.GetTicksPerMove(new CaravanTicksPerMoveUtility.CaravanInfo(dialog));
                        route.EstimatedTicks = (long)CaravanArrivalTimeEstimator.EstimatedTicksToArrive(map.Tile, target, path, 0f, ticksPerMove, Find.TickManager.TicksAbs);
                    }
                    catch (Exception) { /* No formed pack yet; ticks estimate is unavailable until crew/cargo are selected. */ }
                }
                else route.Reason = "No native path to this destination.";
                // Mirrors legacy CaravanTool.RouteFacts' temperature/hostile/goodwill/
                // factionId semantics exactly: hostile always reports (false absent a
                // faction), factionId is set for any settlement faction including the
                // player's own, and goodwill is only meaningful (and only set) for a
                // non-player faction.
                route.TemperatureC = GenTemperature.GetTemperatureFromSeasonAtTile(Find.TickManager.TicksAbs, target);
                if (settlement != null)
                {
                    route.SettlementId = settlement.GetUniqueLoadID();
                    var faction = settlement.Faction;
                    route.Hostile = faction != null && faction.HostileTo(Faction.OfPlayer);
                    if (faction != null)
                    {
                        route.FactionId = faction.GetUniqueLoadID();
                        if (!faction.IsPlayer) route.Goodwill = faction.PlayerGoodwill;
                    }
                }
                return (route, settlement);
            }
        }

        internal static Obs.Settlement SettlementRow(Settlement settlement)
        {
            var faction = settlement.Faction;
            var row = new Obs.Settlement
            {
                Id = settlement.GetUniqueLoadID(), Label = settlement.Label ?? "", Tile = settlement.Tile.tileId,
                Player = faction != null && faction.IsPlayer,
            };
            if (faction != null)
            {
                row.FactionId = faction.GetUniqueLoadID();
                row.FactionDefName = faction.def.defName;
                row.Relation = faction.IsPlayer ? "player" : faction.HostileTo(Faction.OfPlayer) ? "hostile" : "neutral";
                if (!faction.IsPlayer) row.Goodwill = faction.PlayerGoodwill;
            }
            return row;
        }

        internal static Obs.Completeness Complete(int count) => new Obs.Completeness
        { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = 0, Unreadable = 0 };
    }

    public sealed class NativeCaravanObservationTools
    {
        [Tool(NativeCaravanCatalog.ToolName, Title = "Read native caravan formation catalog",
            Description = "Official CaravanCatalogRequest ProtoJSON. Recomputes the native Dialog_FormCaravan pack (no UI, no camera) for the current paused map: home-colonist eligibility, available cargo groups and one requested destination's route/settlement. Read-only; no orders. Page1..256; unavailable instead of truncation.")]
        [ToolResponse("payload", "string", "Official observations CaravanCatalogReply ProtoJSON.", Always = true)]
        public async Task<object> ReadCaravanCatalog(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a CaravanCatalogRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, NativeCaravanCatalog.ToolName, request!, Obs.CaravanCatalogRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.CaravanCatalogReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.CaravanCatalogReply { Failure = failure });
                if (map == null || !Find.TickManager.Paused)
                    return ProtoBoundary.Encode(new Obs.CaravanCatalogReply { Unavailable =
                        new Common.Unavailable { Reason = Common.UnavailableReason.NotApplicable, Detail = "A loaded paused map is required." } });
                try
                {
                    var dialog = NativeCaravanCatalog.BuildDialog(map);
                    var pawns = NativeCaravanCatalog.PawnRows(map, context);
                    var cargo = NativeCaravanCatalog.CargoGroups(map, dialog);
                    var (route, settlement) = NativeCaravanCatalog.RouteFacts(map, dialog, parsed.Destination);
                    var token = NativeCaravanCatalog.Token(context, cargo, pawns);
                    var catalog = new Obs.CaravanCatalog
                    {
                        Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = "caravan-catalog-" + map.uniqueID, Token = token },
                        Completeness = NativeCaravanCatalog.Complete(pawns.Count + cargo.Count),
                    };
                    catalog.Pawns.Add(pawns);
                    catalog.CargoGroups.Add(cargo);
                    catalog.Routes.Add(route);
                    if (settlement != null) catalog.Settlements.Add(NativeCaravanCatalog.SettlementRow(settlement));
                    var reply = new Obs.CaravanCatalogReply { Observed = catalog };
                    if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) > ProtoBoundary.MaximumEnvelopeBytes)
                        return ProtoBoundary.Encode(new Obs.CaravanCatalogReply { Unavailable =
                            new Common.Unavailable { Reason = Common.UnavailableReason.LimitExceeded, Detail = "Caravan catalog exceeds 1MiB." } });
                    return ProtoBoundary.Encode(reply);
                }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.CaravanCatalogReply { Unavailable =
                    new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native caravan catalog could not be read completely." } }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static bool Validate(Obs.CaravanCatalogRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity, a nonnegative destination tile and page1..256 are required.");
            return request?.Scope?.ExpectedIdentity != null && request.HasDestination && request.Destination >= 0
                && (request.Page == null || (!request.Page.HasLimit || request.Page.Limit >= 1 && request.Page.Limit <= 256)
                    && (!request.Page.HasCursor || request.Page.Cursor.Length == 0));
        }
    }
}
