using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Defs = RimGovernor.Contracts.Wire.Defs;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeSuppliesObservationTools
    {
        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Obs.SuppliesSnapshot HandleListSupplies(Obs.ListSuppliesRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                throw NativeFaults.Refused(error);
            var filter = parsed.Filter;
            var player = Faction.OfPlayerSilentFail;
            if (player == null || map.areaManager?.Home == null || map.zoneManager == null || map.reservationManager == null || map.fogGrid == null)
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.NativeComponentMissing, Detail = "Stock ownership/read trackers are unavailable." });
            if (filter.Region != null && (!NativeCell(filter.Region.Minimum).InBounds(map) || !NativeCell(filter.Region.Maximum).InBounds(map)))
                throw NativeFaults.Refused(ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Region must be inside the current map."));
            try
            {
                var entries = new Census(map, player, filter).Read();
                var groups = entries.GroupBy(e => Id(e.Thing.def.defName), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal);
                var selected = groups.Where(g => filter.Ownership == Obs.StockOwnership.All || g.Any(e => e.Ours));
                return new Obs.SuppliesSnapshot { Context = context, Stocks = [.. selected.Select(g => Project(g.ToList(), context))] };
            }
            catch (Exception readError)
            {
                ModLog.Error("observe", "Supplies read failed: " + readError);
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Stock or holder facts could not be read completely." });
            }
        }

        internal static bool Validate(Obs.ListSuppliesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity, supported exact stock filters are required.");
            if (request?.Filter == null || request.Scope?.ExpectedIdentity == null) return false;
            var filter = request.Filter;
            if (filter.DefNames.Count > 256 || filter.DefNames.Any(d => !ProtoBoundary.IsIdentifier(d))
                || filter.DefNames.Distinct(StringComparer.Ordinal).Count() != filter.DefNames.Count) return false;
            if (!Enum.IsDefined(typeof(Obs.StockCategory), filter.Category) || !Enum.IsDefined(typeof(Obs.StockOwnership), filter.Ownership)) return false;
            if (filter.Region == null) return true;
            var min = filter.Region.Minimum;
            var max = filter.Region.Maximum;
            return CellPresent(min) && CellPresent(max)
                && min.X <= max.X && min.Z <= max.Z;
        }

        internal sealed class StockEntry
        {
            internal required Thing Thing;
            internal Thing? Holder;
            internal string? HolderKind;
            internal IntVec3 Position;
            internal bool Ours, Forbidden, PlayerFaction, OtherFaction;
        }
        private sealed class Holder
        {
            internal required Thing Thing;
            internal string Kind = "container";
            internal bool Player, Other, Dead;
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
                var category = filter.Category;
                IEnumerable<Thing> spawned = category == Obs.StockCategory.All ? all : category == Obs.StockCategory.Buildings
                    ? map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial)
                    : map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
                foreach (var thing in spawned) Add(thing, null, thing.Position);
                if (filter.IncludeHeld) foreach (var root in all)
                    if (root is IThingHolder holder) Walk(holder, root, null, 0);
                return result;
            }
            private void Add(Thing thing, Holder? holder, IntVec3 position)
            {
                if (thing == null || thing.Destroyed || thing.def == null || !things.Add(thing)) throw new InvalidOperationException("Invalid or duplicate native stock thing.");
                if (!MatchesCategory(thing.def, filter.Category, holder != null)) return;
                if (filter.Corpses && !(thing is Corpse)) return;
                if (filter.ExcludeChunks && thing.def.defName.StartsWith("Chunk", StringComparison.OrdinalIgnoreCase)) return;
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
                if (filter.ForbiddenOnly && !forbidden) return;
                var fogged = position.Fogged(map);
                var ownFaction = holder?.Player ?? (thing.Faction == player);
                var other = holder?.Other ?? (thing.Faction != null && thing.Faction != player);
                result.Add(new StockEntry { Thing = thing, Holder = holder?.Thing, HolderKind = holder?.Kind,
                    Position = position, Ours = IsOurs(fogged, holder != null, ownFaction, other, holder?.Dead ?? false),
                    Forbidden = forbidden,
                    PlayerFaction = ownFaction, OtherFaction = other });
            }
            private void Walk(IThingHolder holder, Thing root, Holder? inherited, int depth)
            {
                if (depth > 16) throw new InvalidOperationException("Stock holder nesting exceeds 16 levels.");
                if (!holders.Add(holder)) return;
                if (holder is Frame || holder is Blueprint) return; // Delivered construction material is spent stock.
                if (holder is Pawn pawn)
                {
                    var dead = pawn.Dead || inherited?.Dead == true;
                    var info = new Holder { Thing = dead && inherited?.Kind == "corpse" ? inherited.Thing : pawn,
                        Player = pawn.Faction == player, Other = pawn.Faction != null && pawn.Faction != player,
                        Dead = dead };
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
                        Dead = inherited?.Dead == true };
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
                Player = value.Player, Other = value.Other, Dead = value.Dead };
        }

        internal static bool MatchesCategory(ThingDef definition, Obs.StockCategory category, bool held) => category == Obs.StockCategory.All
            || category == Obs.StockCategory.Buildings && !held && definition.category == ThingCategory.Building
            || category == Obs.StockCategory.Food && definition.IsNutritionGivingIngestible
            || category == Obs.StockCategory.Weapons && definition.IsWeapon || category == Obs.StockCategory.Haulable && definition.EverHaulable;

        internal static bool IsOurs(bool fogged, bool held, bool playerFaction, bool otherFaction, bool deadHolder)
            => !fogged && (held ? playerFaction && !deadHolder : !otherFaction);

        internal static Obs.ResourceStock Project(List<StockEntry> entries, Common.ObservationContext context)
        {
            long totalUnits = 0, spawned = 0, ours = 0, oursUnforbidden = 0, forbidden = 0, playerFaction = 0;
            foreach (var entry in entries)
            {
                var units = entry.Thing.stackCount;
                checked
                {
                    totalUnits += units;
                    if (entry.Holder == null) spawned += units;
                    if (entry.Ours) ours += units;
                    if (entry.Ours && !entry.Forbidden) oursUnforbidden += units;
                    if (entry.Forbidden) forbidden += units;
                    if (entry.PlayerFaction) playerFaction += units;
                }
            }
            return new Obs.ResourceStock
            {
                DefName = Id(entries[0].Thing.def.defName),
                Units = totalUnits,
                Stacks = entries.Count,
                Spawned = spawned,
                Ours = ours,
                OursUnforbidden = oursUnforbidden,
                Forbidden = forbidden,
                PlayerFaction = playerFaction,
                Items = [.. entries.Select(entry => Item(entry, context))],
                Holders = [.. entries.Where(entry => entry.Holder != null).Select(entry => new Obs.HeldStock
                    { Holder = Id((entry.Holder ?? throw new System.InvalidOperationException("Required native value is unavailable: entry.Holder.")).GetUniqueLoadID()), HolderKind = NativeEnums.Holder((entry.HolderKind ?? throw new System.InvalidOperationException("Required native value is unavailable: entry.HolderKind."))), Units = entry.Thing.stackCount })],
                WeaponItems = [.. entries.Where(entry => entry.Thing.def.IsWeapon)
                    .Select(entry => NativeGearFacts.Gear(entry.Thing))],
            };
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

        private static Obs.StockItem Item(StockEntry entry, Common.ObservationContext context) => new Obs.StockItem
        {
            Id = Id(entry.Thing.GetUniqueLoadID()),
            Cell = new RimGovernor.Brain.Domain.Cell(entry.Position.x, entry.Position.z),
            Snapshot = entry.Holder == null ? NativeSupplyAllow.Snapshot(entry.Thing, context) : HeldSnapshot(entry, context),
            Packed = entry.Thing is MinifiedThing mini && mini.InnerThing != null ? new Obs.PackedBuilding
            {
                Id = Id(mini.InnerThing.GetUniqueLoadID()),
                DefName = Id(mini.InnerThing.def.defName),
                // Art stock: the trade selector keeps the best pieces.
                Quality = mini.InnerThing.TryGetQuality(out var quality) ? new Obs.PackedQuality.Rated { Category = (Defs.QualityCategory)(int)quality } : new Obs.PackedQuality.Unrated(),
                MarketValue = mini.MarketValue,
            } : null,
        };

        private static bool CellPresent(RimGovernor.Brain.Domain.Cell cell) => cell.X >= 0 && cell.Z >= 0;
        private static IntVec3 NativeCell(RimGovernor.Brain.Domain.Cell cell) => new IntVec3(cell.X, 0, cell.Z);
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
    }
}
