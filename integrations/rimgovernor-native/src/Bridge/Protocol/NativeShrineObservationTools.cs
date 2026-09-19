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
    /// <summary>
    /// Ancient shrines (#456): one row per cryptosleep casket group. The
    /// group id is the native identity a casket opening shares, so it
    /// survives the fogged-to-breached transition that dissolves the room.
    /// Fogged casket rows, and guards while any casket cell is fogged, stay
    /// hidden: the census reports what the player could see.
    /// </summary>
    public sealed class NativeShrineObservationTools
    {
        private const string ToolName = "rimgovernor/observations_get_ancient_shrines";
        private const int ShrineLimit = 64, CasketLimit = 32, GuardLimit = 256, BreachLimit = 64, RoomWidthLimit = 256;

        [Tool(ToolName, Title = "Read ancient shrines", Description = "Complete bounded census of ancient cryptosleep casket groups: room rectangle, sealed state, Home overlap, visible caskets with hit points and contents, hostile guards once unfogged, and deconstructible breach walls. Read-only; admits nothing.")]
        [ToolResponse("payload", "string", "Official ProtoJSON AncientShrinesReply.", Always = true)]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON AncientShrinesRequest string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.AncientShrinesRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.AncientShrinesReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.AncientShrinesReply { Failure = error });
                try {
                    var player = Faction.OfPlayerSilentFail;
                    if (player == null || map.areaManager?.Home == null || map.listerThings == null || map.roofGrid == null || map.fogGrid == null)
                        return Missing(Common.UnavailableReason.NativeComponentMissing, "Player, Home, thing or fog trackers unavailable.");
                    var groups = new Dictionary<int, List<Building_AncientCryptosleepCasket>>();
                    foreach (var casket in map.listerThings.AllThings.OfType<Building_AncientCryptosleepCasket>()) {
                        if (!casket.Spawned || !casket.Position.InBounds(map)) continue;
                        var key = casket.groupID >= 0 ? casket.groupID : -1 - casket.thingIDNumber;
                        if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Building_AncientCryptosleepCasket>();
                        list.Add(casket);
                    }
                    Require(groups.Count <= ShrineLimit, "Casket groups exceed " + ShrineLimit + ".");
                    var snapshot = new Obs.AncientShrinesSnapshot { Context = context };
                    var home = map.areaManager.Home;
                    foreach (var pair in groups.OrderBy(p => p.Key)) {
                        var caskets = pair.Value.OrderBy(c => c.thingIDNumber).ToList();
                        Require(caskets.Count <= CasketLimit, "Caskets in one shrine exceed " + CasketLimit + ".");
                        var fogged = caskets.Any(c => c.Position.Fogged(map));
                        var rooms = new HashSet<Room>();
                        foreach (var casket in caskets)
                            if (casket.GetRoom() is Room room && room.ProperRoom && !room.TouchesMapEdge && !room.PsychologicallyOutdoors) rooms.Add(room);
                        var cells = rooms.Count > 0 ? rooms.SelectMany(r => r.Cells).ToList() : caskets.Select(c => c.Position).ToList();
                        var rect = CellRect.FromCellList(cells);
                        if (rooms.Count == 0) rect = rect.ExpandedBy(2).ClipInsideMap(map);
                        Require(rect.Width <= RoomWidthLimit && rect.Height <= RoomWidthLimit, "Shrine room exceeds the bounded rectangle.");
                        var sealedRoom = rooms.Count > 0 && fogged && rooms.All(r => r.OpenRoofCount == 0);
                        var row = new Obs.AncientShrine {
                            ShrineId = Id(pair.Key >= 0 ? "AncientShrineGroup_" + pair.Key : caskets[0].GetUniqueLoadID()),
                            Room = new Obs.Rectangle { Minimum = Cell(rect.minX, rect.minZ), Maximum = Cell(rect.maxX, rect.maxZ) },
                            Sealed = sealedRoom, InHome = rect.Cells.Any(c => c.InBounds(map) && home[c]), GuardsKnown = !fogged
                        };
                        foreach (var casket in caskets) {
                            if (casket.Position.Fogged(map)) continue;
                            row.Caskets.Add(new Obs.ShrineCasket {
                                EntityId = Id(casket.GetUniqueLoadID()), Cell = Cell(casket.Position.x, casket.Position.z),
                                HitPoints = (uint)Math.Max(0, casket.HitPoints), MaxHitPoints = (uint)Math.Max(1, casket.MaxHitPoints),
                                HasContents = casket.HasAnyContents, PlayerClaimed = casket.Faction == player,
                                InteractionCell = Cell(casket.InteractionCell.x, casket.InteractionCell.z)
                            });
                        }
                        if (!fogged) {
                            var things = rooms.Count > 0 ? rooms.SelectMany(r => r.ContainedAndAdjacentThings) : rect.Cells.Where(c => c.InBounds(map)).SelectMany(c => c.GetThingList(map));
                            var seen = new HashSet<int>();
                            foreach (var thing in things) {
                                if (!seen.Add(thing.thingIDNumber)) continue;
                                var guard = Guard(thing, player);
                                if (guard == null) continue;
                                Require(row.Guards.Count < GuardLimit, "Shrine guards exceed " + GuardLimit + ".");
                                row.Guards.Add(guard);
                            }
                            foreach (var thing in things) {
                                var occupant = Occupant(thing, player);
                                if (occupant == null || row.Occupants.Any(o => o.EntityId == occupant.EntityId)) continue;
                                Require(row.Occupants.Count < GuardLimit, "Shrine occupants exceed " + GuardLimit + ".");
                                row.Occupants.Add(occupant);
                            }
                        }
                        foreach (var room in rooms.OrderBy(r => r.ID)) {
                            var inside = new HashSet<IntVec3>(room.Cells);
                            foreach (var cell in room.BorderCellsCardinal.Distinct().OrderBy(c => c.x).ThenBy(c => c.z)) {
                                if (!cell.InBounds(map) || cell.Fogged(map) || inside.Contains(cell)) continue;
                                if (!(cell.GetEdifice(map) is Building wall) || wall.Faction == player || wall.def != ThingDefOf.Wall && !(wall is Building_Door) || !wall.DeconstructibleBy(player)) continue;
                                if (row.BreachWalls.Any(w => w.EntityId == wall.GetUniqueLoadID())) continue;
                                if (RoofSupportSafety.Blocker(wall, out _) != null) continue;
                                IntVec3? outside = null;
                                foreach (var offset in GenAdj.CardinalDirections) {
                                    var near = cell + offset;
                                    if (near.InBounds(map) && !inside.Contains(near) && near.Walkable(map) && !near.Fogged(map)) { outside = near; break; }
                                }
                                if (outside == null) continue;
                                Require(row.BreachWalls.Count < BreachLimit, "Shrine breach walls exceed " + BreachLimit + ".");
                                row.BreachWalls.Add(new Obs.ShrineBreachWall { EntityId = Id(wall.GetUniqueLoadID()), DefName = Id(wall.def.defName), Cell = Cell(cell.x, cell.z), Outside = Cell(outside.Value.x, outside.Value.z) });
                            }
                        }
                        snapshot.Shrines.Add(row);
                    }
                    var count = (ulong)snapshot.Shrines.Count;
                    snapshot.Completeness = new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = count, Returned = count, Filtered = 0, Unreadable = 0 };
                    var reply = new Obs.AncientShrinesReply { Observed = snapshot };
                    Require(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) <= 1024 * 1024, "Shrine reply exceeds 1 MiB.");
                    return ProtoBoundary.Encode(reply);
                }
                catch (ReadLimit limit) { return Missing(Common.UnavailableReason.LimitExceeded, limit.Message); }
                catch (Exception) { return Missing(Common.UnavailableReason.ReadFailed, "Shrine facts could not be read completely."); }
            }, cancellationToken).ConfigureAwait(false);
        }

        // Occupant (#460) is any non-player humanlike in the room or its
        // corpse: what the caskets released, read after the opening. Guards
        // stay the hostile-threat census; a woken hostile ancient is both.
        private static Obs.ShrineOccupant? Occupant(Thing thing, Faction player)
        {
            switch (thing) {
                case Pawn pawn when pawn.RaceProps != null && pawn.RaceProps.Humanlike && pawn.Faction != player:
                    return new Obs.ShrineOccupant { EntityId = Id(pawn.GetUniqueLoadID()), Hostile = pawn.Faction != null && pawn.Faction.HostileTo(player), Downed = pawn.Downed, Dead = pawn.Dead, Prisoner = pawn.IsPrisonerOfColony, Faction = pawn.Faction?.def?.defName ?? "" };
                case Corpse corpse when corpse.InnerPawn?.RaceProps != null && corpse.InnerPawn.RaceProps.Humanlike && corpse.InnerPawn.Faction != player:
                    return new Obs.ShrineOccupant { EntityId = Id(corpse.InnerPawn.GetUniqueLoadID()), Hostile = corpse.InnerPawn.Faction != null && corpse.InnerPawn.Faction.HostileTo(player), Downed = false, Dead = true, Prisoner = false, Faction = corpse.InnerPawn.Faction?.def?.defName ?? "" };
                default:
                    return null;
            }
        }

        private static Obs.ShrineGuard? Guard(Thing thing, Faction player)
        {
            switch (thing) {
                case Hive hive when hive.Faction != null && hive.Faction.HostileTo(player):
                    return new Obs.ShrineGuard { EntityId = Id(hive.GetUniqueLoadID()), Kind = Obs.ShrineGuardKind.Hive, Downed = false, Dead = hive.Destroyed };
                case Pawn pawn when pawn.Faction != null && pawn.Faction.HostileTo(player):
                    return new Obs.ShrineGuard { EntityId = Id(pawn.GetUniqueLoadID()), Kind = Kind(pawn), Downed = pawn.Downed, Dead = pawn.Dead };
                case Corpse corpse when corpse.InnerPawn?.Faction != null && corpse.InnerPawn.Faction.HostileTo(player):
                    return new Obs.ShrineGuard { EntityId = Id(corpse.InnerPawn.GetUniqueLoadID()), Kind = Kind(corpse.InnerPawn), Downed = false, Dead = true };
                default:
                    return null;
            }
        }

        private static Obs.ShrineGuardKind Kind(Pawn pawn)
        {
            var race = pawn.RaceProps;
            if (race == null) return Obs.ShrineGuardKind.Other;
            if (race.IsMechanoid) return Obs.ShrineGuardKind.Mechanoid;
            if (race.Humanlike) return Obs.ShrineGuardKind.Human;
            if (race.FleshType == FleshTypeDefOf.Insectoid) return Obs.ShrineGuardKind.Insectoid;
            if (race.FleshType == FleshTypeDefOf.Fleshbeast) return Obs.ShrineGuardKind.Fleshbeast;
            return Obs.ShrineGuardKind.Other;
        }

        private static Common.Cell Cell(int x, int z) => new Common.Cell { X = x, Z = z };
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static object Missing(Common.UnavailableReason reason, string detail) => ProtoBoundary.Encode(new Obs.AncientShrinesReply { Unavailable = new Common.Unavailable { Reason = reason, Detail = detail } });
        private static void Require(bool condition, string message) { if (!condition) throw new ReadLimit(message); }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) {} }
    }
}
