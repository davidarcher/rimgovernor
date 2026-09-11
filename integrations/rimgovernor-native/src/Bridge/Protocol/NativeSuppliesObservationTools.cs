#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeSuppliesObservationTools
    {
        private const string ToolName = "rimgovernor/observations_list_supplies";
        [Tool(ToolName, Title = "Read typed supply census", Description = "Complete bounded stock by exact native definition. Defaults haulable/ours/includeHeld. Units retain all ownership buckets; ours selects definitions with usable units. Excludes worn gear, orbital stock and delivered construction resources.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ListSuppliesReply. No sampled item/holder/corpse collections or invented CAS snapshots.", Always = true)]
        public async Task<object> ListSupplies(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ListSuppliesRequest string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ListSuppliesRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ListSuppliesReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ListSuppliesReply { Failure = error });
                try
                {
                    var filter = parsed.Filter ?? new Obs.StockFilter();
                    var player = Faction.OfPlayerSilentFail;
                    if (player == null || map.areaManager?.Home == null || map.zoneManager == null || map.reservationManager == null || map.fogGrid == null)
                        return ProtoBoundary.Encode(new Obs.ListSuppliesReply { Unavailable = Unavailable(Common.UnavailableReason.NativeComponentMissing, "Stock ownership/read trackers are unavailable.") });
                    if (filter.Region != null && (!NativeCell(filter.Region.Minimum).InBounds(map) || !NativeCell(filter.Region.Maximum).InBounds(map)))
                        return ProtoBoundary.Encode(new Obs.ListSuppliesReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Region must be inside the current map.") });
                    var entries = new Census(map, player, filter).Read();
                    var reserved = new HashSet<Thing>(map.reservationManager.AllReservedThings());
                    var groups = entries.GroupBy(e => Id(e.Thing.def.defName), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
                    var selected = groups.Where(g => Ownership(filter) == "all" || g.Any(e => e.Ours)).ToList();
                    Require(selected.Count <= Limit(parsed), "Matched definition collection exceeds page limit; frozen paging is unavailable.");
                    var snapshot = new Obs.SuppliesSnapshot { Context = context, Completeness = Complete(selected.Count, groups.Count - selected.Count) };
                    foreach (var group in selected) snapshot.Stocks.Add(Project(group.ToList(), reserved, IncludeHeld(filter)));
                    return Encode(new Obs.ListSuppliesReply { Observed = snapshot });
                }
                catch (ReadLimit limit) { return ProtoBoundary.Encode(new Obs.ListSuppliesReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, limit.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.ListSuppliesReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Stock or holder facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool Validate(Obs.ListSuppliesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity, supported exact stock filters and page limit1..256 are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            if (request.Page != null && (request.Page.HasLimit && (request.Page.Limit < 1 || request.Page.Limit > 256)
                || request.Page.HasCursor && request.Page.Cursor.Length != 0)) return false;
            var filter = request.Filter;
            if (filter == null) return true;
            if (filter.DefNames.Count > 256 || filter.DefNames.Any(d => !ProtoBoundary.IsIdentifier(d))
                || filter.DefNames.Distinct(StringComparer.Ordinal).Count() != filter.DefNames.Count) return false;
            if (filter.HasCategory && !new[] { "haulable", "food", "weapons", "all", "buildings" }.Contains(filter.Category)) return false;
            if (filter.HasOwnership && filter.Ownership != "ours" && filter.Ownership != "all") return false;
            return filter.Region == null || CellPresent(filter.Region.Minimum) && CellPresent(filter.Region.Maximum)
                && filter.Region.Minimum.X <= filter.Region.Maximum.X && filter.Region.Minimum.Z <= filter.Region.Maximum.Z;
        }

        internal sealed class StockEntry
        {
            internal Thing Thing = null!;
            internal Thing? Holder;
            internal string? HolderKind;
            internal IntVec3 Position;
            internal bool Ours, Forbidden, Fogged, InHome, InStockpile, PlayerFaction, OtherFaction, Trader;
        }
        private sealed class Holder
        {
            internal Thing Thing = null!;
            internal string Kind = "container";
            internal bool Player, Other, Trader, Dead;
        }

        private sealed class Census
        {
            private readonly Map map;
            private readonly Faction player;
            private readonly Obs.StockFilter filter;
            private readonly List<StockEntry> result = new List<StockEntry>();
            private readonly HashSet<Thing> things = new HashSet<Thing>();
            private readonly HashSet<IThingHolder> holders = new HashSet<IThingHolder>();
            private readonly HashSet<ThingOwner> owners = new HashSet<ThingOwner>();
            private int visited;
            internal Census(Map map, Faction player, Obs.StockFilter filter) { this.map = map; this.player = player; this.filter = filter; }
            internal List<StockEntry> Read()
            {
                var all = map.listerThings.AllThings.ToList();
                var category = Category(filter);
                IEnumerable<Thing> spawned = category == "all" ? all : category == "buildings"
                    ? map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial)
                    : map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
                foreach (var thing in spawned) Add(thing, null, thing.Position);
                if (IncludeHeld(filter)) foreach (var root in all)
                    if (root is IThingHolder holder) Walk(holder, root, null, 0);
                return result;
            }
            private void Add(Thing thing, Holder? holder, IntVec3 position)
            {
                Require(++visited <= 65536, "Stock traversal exceeds 65536 things/holders.");
                if (thing == null || thing.Destroyed || thing.def == null || !things.Add(thing)) throw new InvalidOperationException("Invalid or duplicate native stock thing.");
                if (!MatchesCategory(thing.def, Category(filter), holder != null)) return;
                if (filter.Corpses && !(thing is Corpse)) return;
                if (filter.ExcludeChunks && thing.def.defName.StartsWith("Chunk", StringComparison.OrdinalIgnoreCase)) return;
                if (filter.DefNames.Count != 0 && !filter.DefNames.Contains(thing.def.defName)) return;
                if (!position.InBounds(map)) throw new InvalidOperationException("Stock root position outside map.");
                if (filter.Region != null && (position.x < filter.Region.Minimum.X || position.x > filter.Region.Maximum.X
                    || position.z < filter.Region.Minimum.Z || position.z > filter.Region.Maximum.Z)) return;
                if (thing.stackCount <= 0) throw new InvalidOperationException("Invalid native stack quantity.");
                var forbidden = (thing as ThingWithComps)?.GetComp<CompForbiddable>()?.Forbidden ?? false;
                if (filter.ForbiddenOnly && !forbidden) return;
                var fogged = position.Fogged(map);
                var ownFaction = holder?.Player ?? (thing.Faction == player);
                var other = holder?.Other ?? (thing.Faction != null && thing.Faction != player);
                result.Add(new StockEntry { Thing = thing, Holder = holder?.Thing, HolderKind = holder?.Kind,
                    Position = position, Ours = IsOurs(fogged, holder != null, ownFaction, other, holder?.Dead ?? false),
                    Forbidden = forbidden, Fogged = fogged, InHome = map.areaManager.Home[position],
                    InStockpile = holder == null && map.zoneManager.ZoneAt(position) is Zone_Stockpile,
                    PlayerFaction = ownFaction, OtherFaction = other, Trader = holder?.Trader ?? false });
            }
            private void Walk(IThingHolder holder, Thing root, Holder? inherited, int depth)
            {
                Require(depth <= 16 && ++visited <= 65536, "Stock holder traversal exceeds bounded depth or count.");
                if (!holders.Add(holder)) return;
                if (holder is Frame || holder is Blueprint) return; // Delivered construction material is spent stock.
                if (holder is Pawn pawn)
                {
                    var dead = pawn.Dead || inherited?.Dead == true;
                    var role = pawn.GetTraderCaravanRole();
                    var info = new Holder { Thing = dead && inherited?.Kind == "corpse" ? inherited.Thing : pawn,
                        Player = pawn.Faction == player, Other = pawn.Faction != null && pawn.Faction != player,
                        Trader = !dead && pawn.Faction != player && (role == TraderCaravanRole.Trader || role == TraderCaravanRole.Carrier), Dead = dead };
                    if (pawn.inventory != null) Emit(pawn.inventory.innerContainer, root, Copy(info, dead ? "corpse" : "pawnInventory"), depth);
                    if (!dead && pawn.carryTracker != null) Emit(pawn.carryTracker.innerContainer, root, Copy(info, "carried"), depth);
                    return; // Apparel/equipment are worn gear, never available stock.
                }
                if (holder is Corpse corpse)
                {
                    var inner = corpse.InnerPawn ?? throw new InvalidOperationException("Corpse pawn unavailable.");
                    Walk(inner, root, new Holder { Thing = corpse, Kind = "corpse", Dead = true }, depth + 1);
                    return;
                }
                var ownerThing = holder as Thing;
                var infoContainer = ownerThing == null ? inherited ?? throw new InvalidOperationException("Unnamed holder.")
                    : new Holder { Thing = ownerThing, Player = inherited?.Player ?? (ownerThing.Faction == player),
                        Other = inherited?.Other ?? (ownerThing.Faction != null && ownerThing.Faction != player),
                        Trader = inherited?.Trader ?? false, Dead = inherited?.Dead == true };
                var direct = holder.GetDirectlyHeldThings();
                if (direct != null) Emit(direct, root, infoContainer, depth);
                var children = new List<IThingHolder>();
                holder.GetChildHolders(children);
                foreach (var child in children) Walk(child, root, infoContainer, depth + 1);
            }
            private void Emit(ThingOwner owner, Thing root, Holder info, int depth)
            {
                if (!owners.Add(owner)) return;
                for (var index = 0; index < owner.Count; index++)
                {
                    var thing = owner[index];
                    if (thing.Spawned) throw new InvalidOperationException("Held stock is also spawned.");
                    Add(thing, info, root.Position);
                    if (thing is IThingHolder nested) Walk(nested, root, info, depth + 1);
                }
            }
            private static Holder Copy(Holder value, string kind) => new Holder { Thing = value.Thing, Kind = kind,
                Player = value.Player, Other = value.Other, Trader = value.Trader, Dead = value.Dead };
        }

        internal static bool MatchesCategory(ThingDef definition, string category, bool held) => category == "all"
            || category == "buildings" && !held && definition.category == ThingCategory.Building
            || category == "food" && definition.IsNutritionGivingIngestible
            || category == "weapons" && definition.IsWeapon || category == "haulable" && definition.EverHaulable;

        internal static bool IsOurs(bool fogged, bool held, bool playerFaction, bool otherFaction, bool deadHolder)
            => !fogged && (held ? playerFaction && !deadHolder : !otherFaction);

        internal static Obs.ResourceStock Project(List<StockEntry> entries, HashSet<Thing> reserved, bool includeHeld)
        {
            Require(entries.Count <= 256, "Complete stock item collection exceeds 256 rows.");
            var first = entries[0].Thing.def;
            var row = new Obs.ResourceStock { Definition = new Obs.DefinitionRef { DefName = Id(first.defName), Label = PlacementPreviewOperation.Diagnostic(first.LabelCap) },
                Units = 0, Stacks = entries.Count, Spawned = 0, Ours = 0, OursUnforbidden = 0, Forbidden = 0,
                PlayerFaction = 0, OtherFaction = 0, Fogged = 0, Reserved = 0, InStockpile = 0, InHomeArea = 0 };
            if (includeHeld) { row.Carried = 0; row.InContainer = 0; row.TraderStock = 0; }
            else foreach (var field in new[] { "carried", "in_container", "trader_stock" })
                row.Issues.Add(Issue(field, Common.UnavailableReason.NotRequested, "Held stock was excluded from this census scope."));
            foreach (var entry in entries)
            {
                var units = entry.Thing.stackCount;
                checked
                {
                    row.Units += units;
                    if (entry.Holder == null) row.Spawned += units;
                    else if (entry.HolderKind == "pawnInventory" || entry.HolderKind == "carried") row.Carried += units;
                    else row.InContainer += units;
                    if (entry.Ours) row.Ours += units;
                    if (entry.Ours && !entry.Forbidden) row.OursUnforbidden += units;
                    if (entry.Forbidden) row.Forbidden += units;
                    if (entry.PlayerFaction) row.PlayerFaction += units;
                    if (entry.OtherFaction) row.OtherFaction += units;
                    if (entry.Trader) row.TraderStock += units;
                    if (entry.Fogged) row.Fogged += units;
                    if (entry.InHome) row.InHomeArea += units;
                    if (entry.InStockpile) row.InStockpile += units;
                    if (entry.Ours && reserved.Contains(entry.Thing)) row.Reserved += units;
                }
                row.Items.Add(Entity(entry.Thing, entry.Position));
                if (entry.Holder != null) row.Holders.Add(new Obs.HeldStock { Holder = Entity(entry.Holder, entry.Position), HolderKind = entry.HolderKind!, Units = units });
                if (entry.Thing is Corpse corpse)
                {
                    var pawn = corpse.InnerPawn ?? throw new InvalidOperationException("Corpse pawn unavailable.");
                    var detail = new Obs.CorpseState { Corpse = Entity(corpse, entry.Position), InnerPawn = Entity(pawn, entry.Position),
                        Race = Id(pawn.def.defName), Humanlike = pawn.RaceProps.Humanlike, WasColonist = pawn.IsColonist };
                    var rot = corpse.GetComp<CompRottable>();
                    if (rot != null) detail.RotStage = rot.Stage.ToString();
                    else row.Issues.Add(Issue("corpses." + Id(corpse.GetUniqueLoadID()) + ".rot_stage", Common.UnavailableReason.NotApplicable, "Corpse has no rot component."));
                    row.Corpses.Add(detail);
                }
            }
            row.ItemsCompleteness = Complete(row.Items.Count);
            row.HoldersCompleteness = includeHeld ? Complete(row.Holders.Count) : new Obs.Completeness { Page = new Common.PageInfo { Complete = false } };
            row.CorpsesCompleteness = Complete(row.Corpses.Count);
            row.Issues.Add(Issue("items.snapshot", Common.UnavailableReason.Unsupported, "Exact entity CAS snapshots are not implemented."));
            return row;
        }

        private static Obs.EntityRef Entity(Thing thing, IntVec3 position) => new Obs.EntityRef { Id = Id(thing.GetUniqueLoadID()),
            DefName = Id(thing.def.defName), Label = PlacementPreviewOperation.Diagnostic(thing.LabelCap),
            MapId = thing.MapHeld?.uniqueID ?? throw new InvalidOperationException("Stock map unavailable."), Position = new Common.Cell { X = position.x, Z = position.z } };
        private static string Category(Obs.StockFilter filter) => filter.HasCategory ? filter.Category : "haulable";
        private static string Ownership(Obs.StockFilter filter) => filter.HasOwnership ? filter.Ownership : "ours";
        private static bool IncludeHeld(Obs.StockFilter filter) => !filter.HasIncludeHeld || filter.IncludeHeld;
        private static int Limit(Obs.ListSuppliesRequest request) => request.Page?.HasLimit == true ? (int)request.Page.Limit : 256;
        private static bool CellPresent(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ;
        private static IntVec3 NativeCell(Common.Cell cell) => new IntVec3(cell.X, 0, cell.Z);
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
        private static Obs.Completeness Complete(int count, int filtered = 0) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = (ulong)filtered, Unreadable = 0 };
        internal static object Encode(Obs.ListSuppliesReply reply)
        {
            Require(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) <= 1024 * 1024, "Complete supplies reply exceeds 1 MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new ReadLimit(message); }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) {} }
    }
}
