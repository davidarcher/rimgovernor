#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeSuppliesObservationTools
    {
        private const string ToolName = "rimgovernor/observations_list_supplies";
        [Tool(ToolName, Title = "Read typed supply census", Description = "Complete stock by exact native definition. Defaults haulable/ours/includeHeld. Units retain all ownership buckets; ours selects definitions with usable units. Excludes worn gear, orbital stock and delivered construction resources.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ListSuppliesReply. No sampled item/holder/corpse collections or invented CAS snapshots.", Always = true)]
        public async Task<object> ListSupplies(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ListSuppliesRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Obs.ListSuppliesRequest>(ctx, ToolName, request, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.ListSuppliesReply { Outcome = new Obs.ListSuppliesReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleListSupplies(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Obs.ListSuppliesReply HandleListSupplies(Obs.ListSuppliesRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) return new Obs.ListSuppliesReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                return new Obs.ListSuppliesReply { Outcome = error };
            try
            {
                var filter = parsed.Filter ?? new Obs.StockFilter();
                var player = Faction.OfPlayerSilentFail;
                if (player == null || map.areaManager?.Home == null || map.zoneManager == null || map.reservationManager == null || map.fogGrid == null)
                    return new Obs.ListSuppliesReply { Outcome = Unavailable(Common.UnavailableReason.NativeComponentMissing, "Stock ownership/read trackers are unavailable.") };
                if (filter.Region != null && (!NativeCell(filter.Region.Minimum).InBounds(map) || !NativeCell(filter.Region.Maximum).InBounds(map)))
                    return new Obs.ListSuppliesReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Region must be inside the current map.") };
                var entries = new Census(map, player, filter).Read();
                var reserved = new HashSet<Thing>(map.reservationManager.AllReservedThings());
                var groups = entries.GroupBy(e => Id(e.Thing.def.defName), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
                var selected = groups.Where(g => Ownership(filter) == Obs.StockOwnership.All || g.Any(e => e.Ours)).ToList();
                var snapshot = new Obs.SuppliesSnapshot { Context = context, Completeness = Complete(selected.Count) };
                foreach (var group in selected)
                {
                    var entriesForDefinition = group.ToList();
                    var row = Project(entriesForDefinition, reserved, IncludeHeld(filter));
                    var items = row.Items.ToList();
                    for (var index = 0; index < items.Count; index++)
                    {
                        var entry = entriesForDefinition[index];
                        items[index] = items[index] with { Snapshot = entry.Holder == null
                            ? NativeSupplyAllow.Snapshot(entry.Thing, context)
                            : HeldSnapshot(entry, context) };
                    }
                    row = row with { Items = [.. items] };
                    if (items.All(item => item.Snapshot != null))
                    {
                        var snapshotIssue = row.Issues.Single(issue => (issue.Field ?? "") == "items.snapshot");
                        row = row with { Issues = [.. row.Issues.Where(issue => !ReferenceEquals(issue, snapshotIssue))] };
                    }
                    snapshot = snapshot with { Stocks = [.. snapshot.Stocks, row] };
                }
                return new Obs.ListSuppliesReply { Outcome = snapshot };
            }
            catch (Exception) { return new Obs.ListSuppliesReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, "Stock or holder facts could not be read completely.") }; }
        }

        internal static bool Validate(Obs.ListSuppliesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity, supported exact stock filters are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            var filter = request.Filter;
            if (filter == null) return true;
            if (filter.DefNames.Count > 256 || filter.DefNames.Any(d => !ProtoBoundary.IsIdentifier(d))
                || filter.DefNames.Distinct(StringComparer.Ordinal).Count() != filter.DefNames.Count) return false;
            if (filter.Category is not null && (filter.Category.GetValueOrDefault() == Obs.StockCategory.Unspecified || !Enum.IsDefined(typeof(Obs.StockCategory), filter.Category.GetValueOrDefault()))) return false;
            if (filter.Ownership is not null && filter.Ownership.GetValueOrDefault() != Obs.StockOwnership.Ours && filter.Ownership.GetValueOrDefault() != Obs.StockOwnership.All) return false;
            if (filter.Region == null) return true;
            var min = filter.Region.Minimum;
            var max = filter.Region.Maximum;
            return CellPresent(min) && CellPresent(max)
                && min.X.GetValueOrDefault() <= max.X.GetValueOrDefault() && min.Z.GetValueOrDefault() <= max.Z.GetValueOrDefault();
        }

        internal sealed class StockEntry
        {
            internal Thing Thing = null!;
            internal Thing? Holder;
            internal string? HolderKind;
            internal IntVec3 Position;
            internal bool Ours, Forbidden, Fogged, PlayerFaction, OtherFaction, Trader;
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
            internal Census(Map map, Faction player, Obs.StockFilter filter) { this.map = map; this.player = player; this.filter = filter; }
            internal List<StockEntry> Read()
            {
                var all = map.listerThings.AllThings.ToList();
                var category = Category(filter);
                IEnumerable<Thing> spawned = category == Obs.StockCategory.All ? all : category == Obs.StockCategory.Buildings
                    ? map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial)
                    : map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
                foreach (var thing in spawned) Add(thing, null, thing.Position);
                if (IncludeHeld(filter)) foreach (var root in all)
                    if (root is IThingHolder holder) Walk(holder, root, null, 0);
                return result;
            }
            private void Add(Thing thing, Holder? holder, IntVec3 position)
            {
                if (thing == null || thing.Destroyed || thing.def == null || !things.Add(thing)) throw new InvalidOperationException("Invalid or duplicate native stock thing.");
                if (!MatchesCategory(thing.def, Category(filter), holder != null)) return;
                if (filter.Corpses.GetValueOrDefault() && !(thing is Corpse)) return;
                if (filter.ExcludeChunks.GetValueOrDefault() && thing.def.defName.StartsWith("Chunk", StringComparison.OrdinalIgnoreCase)) return;
                if (filter.DefNames.Count != 0 && !filter.DefNames.Contains(thing.def.defName)) return;
                if (!position.InBounds(map)) throw new InvalidOperationException("Stock root position outside map.");
                if (filter.Region != null)
                {
                    var min = NativeCell(filter.Region.Minimum);
                    var max = NativeCell(filter.Region.Maximum);
                    if (position.x < min.x || position.x > max.x || position.z < min.z || position.z > max.z) return;
                }
                if (thing.stackCount <= 0) throw new InvalidOperationException("Invalid native stack quantity.");
                var forbidden = (thing as ThingWithComps)?.GetComp<CompForbiddable>()?.Forbidden ?? false;
                if (filter.ForbiddenOnly.GetValueOrDefault() && !forbidden) return;
                var fogged = position.Fogged(map);
                var ownFaction = holder?.Player ?? (thing.Faction == player);
                var other = holder?.Other ?? (thing.Faction != null && thing.Faction != player);
                result.Add(new StockEntry { Thing = thing, Holder = holder?.Thing, HolderKind = holder?.Kind,
                    Position = position, Ours = IsOurs(fogged, holder != null, ownFaction, other, holder?.Dead ?? false),
                    Forbidden = forbidden, Fogged = fogged,
                    PlayerFaction = ownFaction, OtherFaction = other, Trader = holder?.Trader ?? false });
            }
            private void Walk(IThingHolder holder, Thing root, Holder? inherited, int depth)
            {
                if (depth > 16) throw new InvalidOperationException("Stock holder nesting exceeds 16 levels.");
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

        internal static bool MatchesCategory(ThingDef definition, Obs.StockCategory category, bool held) => category == Obs.StockCategory.All
            || category == Obs.StockCategory.Buildings && !held && definition.category == ThingCategory.Building
            || category == Obs.StockCategory.Food && definition.IsNutritionGivingIngestible
            || category == Obs.StockCategory.Weapons && definition.IsWeapon || category == Obs.StockCategory.Haulable && definition.EverHaulable;

        internal static bool IsOurs(bool fogged, bool held, bool playerFaction, bool otherFaction, bool deadHolder)
            => !fogged && (held ? playerFaction && !deadHolder : !otherFaction);

        internal static Obs.ResourceStock Project(List<StockEntry> entries, HashSet<Thing> reservedThings, bool includeHeld)
        {
            var first = entries[0].Thing.def;
            var row = new Obs.ResourceStock { Definition = new Obs.DefinitionRef { DefName = Id(first.defName) }, Stacks = entries.Count };
            long totalUnits = 0, spawned = 0, carried = 0, inContainer = 0, ours = 0, oursUnforbidden = 0, forbidden = 0,
                playerFaction = 0, otherFaction = 0, traderStock = 0, fogged = 0, reserved = 0;
            if (!includeHeld) foreach (var field in new[] { "carried", "in_container", "trader_stock" })
                row = row with { Issues = [.. row.Issues, Issue(field, Common.UnavailableReason.NotRequested, "Held stock was excluded from this census scope.")] };
            foreach (var entry in entries)
            {
                var units = entry.Thing.stackCount;
                checked
                {
                    totalUnits += units;
                    if (entry.Holder == null) spawned += units;
                    else if (entry.HolderKind == "pawnInventory" || entry.HolderKind == "carried") carried += units;
                    else inContainer += units;
                    if (entry.Ours) ours += units;
                    if (entry.Ours && !entry.Forbidden) oursUnforbidden += units;
                    if (entry.Forbidden) forbidden += units;
                    if (entry.PlayerFaction) playerFaction += units;
                    if (entry.OtherFaction) otherFaction += units;
                    if (entry.Trader) traderStock += units;
                    if (entry.Fogged) fogged += units;
                    if (entry.Ours && reservedThings.Contains(entry.Thing)) reserved += units;
                }
                row = row with { Items = [.. row.Items, Item(entry.Thing, entry.Position)] };
                if (entry.Thing.def.IsWeapon) {
                    var weapon = new Obs.GearItem { Thing = NativeRef.Thing(entry.Thing) };
                    weapon = NativeGearFacts.Biocode(entry.Thing, weapon);
                    row = row with { WeaponItems = [.. row.WeaponItems, weapon] };
                }
                if (entry.Holder != null) row = row with { Holders = [.. row.Holders, new Obs.HeldStock { Holder = NativeRef.Thing(entry.Holder), HolderKind = NativeEnums.Holder(entry.HolderKind!), Units = units }] };
                if (entry.Thing is Corpse corpse)
                {
                    var pawn = corpse.InnerPawn ?? throw new InvalidOperationException("Corpse pawn unavailable.");
                    var detail = new Obs.CorpseState { Corpse = NativeRef.Thing(corpse), InnerPawn = NativeRef.Of(pawn),
                        Race = Id(pawn.def.defName), Humanlike = pawn.RaceProps.Humanlike, WasColonist = pawn.IsColonist };
                    var rot = corpse.GetComp<CompRottable>();
                    if (rot != null) detail = detail with { RotStage = NativeEnums.Rot(rot.Stage) };
                    else row = row with { Issues = [.. row.Issues, Issue("corpses." + Id(corpse.GetUniqueLoadID()) + ".rot_stage", Common.UnavailableReason.NotApplicable, "Corpse has no rot component.")] };
                    row = row with { Corpses = [.. row.Corpses, detail] };
                }
            }
            row = row with { Issues = [.. row.Issues, Issue("items.snapshot", Common.UnavailableReason.Unsupported, "One or more items lack an Allow snapshot; only eligible loose supplies support Allow.")] };
            return row with { Units = totalUnits, Spawned = spawned, Ours = ours, OursUnforbidden = oursUnforbidden, Forbidden = forbidden,
                PlayerFaction = playerFaction, OtherFaction = otherFaction, Fogged = fogged, Reserved = reserved,
                Carried = includeHeld ? carried : null, InContainer = includeHeld ? inContainer : null, TraderStock = includeHeld ? traderStock : null };
        }

        // Held/container/corpse items have no Allow snapshot; give them a stateless CAS token
        // over the same identity-bearing fields the row already exposes (holder + kind + position).
        private static Obs.SnapshotRef HeldSnapshot(StockEntry entry, Common.ObservationContext context)
            => NativeObservationSnapshot.Snapshot("held-item", context, entry.Thing.GetUniqueLoadID(), w =>
            {
                w.Write(entry.Thing.def.defName); w.Write(entry.Thing.stackCount);
                w.Write(entry.Holder?.GetUniqueLoadID() ?? ""); w.Write(entry.HolderKind ?? "");
                w.Write(entry.Position.x); w.Write(entry.Position.z);
            });

        private static Obs.StockItem Item(Thing thing, IntVec3 position)
        {
            var entity = new Obs.StockItem { Item = new Common.Ref { Id = Id(thing.GetUniqueLoadID()) }, Cell = new Common.Cell { X = position.x, Z = position.z } };
            if (thing is MinifiedThing mini && mini.InnerThing != null)
            {
                entity = entity with { Inner = new Common.Ref { Id = Id(mini.InnerThing.GetUniqueLoadID()) } };
                entity = entity with { InnerDefName = Id(mini.InnerThing.def.defName) };
                // Art stock: the trade selector keeps the best pieces.
                if (mini.InnerThing.TryGetQuality(out var quality)) entity = entity with { Quality = (int)quality };
                try { entity = entity with { MarketValue = thing.MarketValue }; } catch { }
            }
            return entity;
        }
        private static Obs.StockCategory Category(Obs.StockFilter filter) => filter.Category is not null ? filter.Category.GetValueOrDefault() : Obs.StockCategory.Haulable;
        private static Obs.StockOwnership Ownership(Obs.StockFilter filter) => filter.Ownership is not null ? filter.Ownership.GetValueOrDefault() : Obs.StockOwnership.Ours;
        private static bool IncludeHeld(Obs.StockFilter filter) => filter.IncludeHeld is null || filter.IncludeHeld.GetValueOrDefault();
        private static bool CellPresent([NotNullWhen(true)] Common.Cell? cell) => cell != null && cell.X is not null && cell.Z is not null;
        private static IntVec3 NativeCell(Common.Cell? cell) => new IntVec3(cell?.X.GetValueOrDefault() ?? 0, 0, cell?.Z.GetValueOrDefault() ?? 0);
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
        private static Obs.Completeness Complete(int count, int filtered = 0) => new Obs.Completeness { Filtered = (ulong)filtered };
    }
}
