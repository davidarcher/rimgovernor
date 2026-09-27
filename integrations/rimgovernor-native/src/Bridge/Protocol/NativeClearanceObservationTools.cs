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
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeClearanceObservationTools
    {
        private const string ToolName = "rimgovernor/observations_get_clearance_targets";
        [Tool(ToolName, Title = "Read clearance targets", Description = "Complete bounded census of visible, deconstructible non-player buildings across the map, plus the rock and slag chunk stacks standing in Home with their storage state and a free outdoor footprint for a dumping stockpile. Includes exact footprints, roof-support blockers, sealed ancient danger and deconstruction ownership. Read-only; does not admit removal.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ClearanceTargetsReply.", Always = true)]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ClearanceTargetsRequest string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ClearanceTargetsRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.ClearanceTargetsReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ClearanceTargetsReply { Failure = error });
                try {
                    var player = Faction.OfPlayerSilentFail;
                    if (player == null || map.areaManager?.Home == null || map.listerThings == null || map.designationManager == null || map.roofGrid == null || map.roofCollapseBuffer == null)
                        return Missing(Common.UnavailableReason.NativeComponentMissing, "Player, Home, building or roof trackers unavailable.");
                    // Scoped to Home: a hilly map carries tens of thousands of
                    // natural-rock buildings map-wide (#414), so the census walks
                    // the Home cells (bounded by the grid) and collects the
                    // non-player buildings standing in them.
                    var home = map.areaManager.Home;
                    var began = Now();
                    var buildings = new Dictionary<int, Building>();
                    var chunks = new Dictionary<int, Thing>();
                    foreach (var cell in home.ActiveCells)
                        foreach (var thing in cell.GetThingList(map)) {
                            if (thing is Building b && b.Spawned && b.Faction != player) buildings[b.thingIDNumber] = b;
                            // Chunks are hauls, not deconstructions (#394): a
                            // dumping stockpile clears them, so the census
                            // reports them beside the buildings.
                            else if (thing.Spawned && thing.def.category == ThingCategory.Item && thing.def.IsWithinCategory(ThingCategoryDefOf.Chunks)) chunks[thing.thingIDNumber] = thing;
                        }
                    foreach (var b in map.listerThings.AllThings.OfType<Building>())
                        if (b.Spawned && b.Faction != player && !b.def.IsNonResourceNaturalRock && !b.def.mineable && b.DeconstructibleBy(player)) buildings[b.thingIDNumber] = b;
                    ObservationWork.Captured("clearanceScan", Now() - began, buildings.Count + chunks.Count);
                    began = Now();
                    var snapshot = new Obs.ClearanceTargetsSnapshot { Context = context };
                    var haulers = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)).ToList();
                    var salvage = parsed.IncludeSalvage ? SalvageRead.Begin(map) : null;
                    var undelivered = new List<Thing>();
                    foreach (var chunk in chunks.Values.OrderBy(t => t.thingIDNumber)) {
                        if (!chunk.Position.InBounds(map) || chunk.Position.Fogged(map)) continue;
                        var forbidden = chunk.IsForbidden(player);
                        var stored = chunk.IsInValidStorage();
                        var destination = !stored && StoreUtility.TryFindBestBetterStoreCellFor(chunk, null, map, StoreUtility.CurrentStoragePriorityOf(chunk), player, out _, false);
                        snapshot.Chunks.Add(new Obs.ClearanceChunk { EntityId = Id(chunk.GetUniqueLoadID()), DefName = Id(chunk.def.defName), Cell = Cell(chunk.Position.x, chunk.Position.z), Forbidden = forbidden, Stored = stored, Destination = destination });
                        if (!forbidden && !stored && !destination) undelivered.Add(chunk);
                    }
                    ObservationWork.Captured("clearanceChunks", Now() - began, snapshot.Chunks.Count, chunks.Count);
                    began = Now();
                    if (undelivered.Count > 0) snapshot.DumpSites.AddRange(DumpSites(map, home, undelivered, haulers).Select(c => Cell(c.x, c.z)));
                    ObservationWork.Captured("clearanceDumpSites", Now() - began, snapshot.DumpSites.Count);
                    long dangerTicks = 0, roofTicks = 0, salvageTicks = 0, salvageRows = 0;
                    began = Now();
                    var triggers = TempleTriggers(map);
                    dangerTicks += Now() - began;
                    foreach (var building in buildings.Values.OrderBy(b => b.thingIDNumber)) {
                        var rect = building.OccupiedRect();
                        if (rect.Any(c => !c.InBounds(map) || c.Fogged(map))) continue;
                        // OfPlayerSilentFail was checked above: the native method
                        // cannot reach its missing-player Log.Error/pause branch.
                        if (!building.DeconstructibleBy(player)) continue;
                        var designated = map.designationManager.DesignationOn(building, DesignationDefOf.Deconstruct) != null;
                        var row = new Obs.ClearanceTarget {
                            EntityId = Id(building.GetUniqueLoadID()), DefName = Id(building.def.defName),
                            Occupied = new Obs.Rectangle { Minimum = Cell(rect.minX, rect.minZ), Maximum = Cell(rect.maxX, rect.maxZ) },
                            Deconstructible = true, Class = Classify(building), InHome = rect.All(c => home[c]),
                            Designated = designated
                        };
                        var phase = Now();
                        row.AncientDanger = AncientDanger(map, building, player, triggers);
                        dangerTicks += Now() - phase;
                        if (building.Faction != null) row.Faction = Id(building.Faction.GetUniqueLoadID());
                        phase = Now();
                        var blocker = RoofSupportSafety.Blocker(building, out _);
                        roofTicks += Now() - phase;
                        if (blocker != null) row.RoofBlocker = blocker;
                        if (!row.InHome && salvage != null) {
                            phase = Now();
                            var evidence = salvage.Evidence(building);
                            if (evidence != null) row.Salvage = evidence;
                            salvageTicks += Now() - phase;
                            salvageRows++;
                        }
                        snapshot.Targets.Add(row);
                    }
                    salvage?.End();
                    // #984: per-phase main-thread cost of the target rows; the
                    // row remainder is fog/footprint checks, designations and
                    // proto construction.
                    var targetTicks = Now() - began;
                    ObservationWork.Captured("clearanceAncientDanger", dangerTicks, snapshot.Targets.Count);
                    ObservationWork.Captured("clearanceRoofBlocker", roofTicks, snapshot.Targets.Count);
                    ObservationWork.Captured("clearanceSalvage", salvageTicks, salvageRows);
                    ObservationWork.Captured("clearanceTargetRows", targetTicks - dangerTicks - roofTicks - salvageTicks, snapshot.Targets.Count, buildings.Count);
                    var count = (ulong)snapshot.Targets.Count;
                    var reply = new Obs.ClearanceTargetsReply { Observed = snapshot };
                    return ProtoBoundary.Encode(reply);
                }
                catch (Exception) { return Missing(Common.UnavailableReason.ReadFailed, "Clearance facts could not be read completely."); }
            }, cancellationToken).ConfigureAwait(false);
        }

        // DumpSites floods outward from the free outdoor Home cell nearest the
        // undelivered chunks' centroid over the cells a dumping stockpile can
        // take (in Home, psychologically outdoors, standable, unzoned, no
        // building, blueprint, frame or item, not marked to collapse, reachable
        // and unforbidden for an eligible hauler) and returns the first
        // connected footprint of up to dumpSiteCells cells, or nothing when no
        // hauler exists or no cell qualifies. The flood is bounded so a wide
        // Home costs a bounded number of reachability checks.
        // yields memoizes, per def within one read, the probe item and its storage
        // headroom: neither depends on the building, so repeat ruins are cheap.
        private static Obs.SalvageEvidence Salvage(Map map, Building building, EventLootFacts.HaulingSafety safety, Dictionary<ThingDef, (Thing item, long headroom)> yields)
        {
            var safe = !building.IsForbidden(Faction.OfPlayer) && !building.IsBurning() && safety.Safe(building) == true;
            var result = new Obs.SalvageEvidence { Safe = safe, PathLength = Math.Max(0, safety.PathLength), Labor = building.GetStatValue(StatDefOf.WorkToBuild) };
            foreach (var cost in building.def.CostListAdjusted(building.Stuff, false)) {
                var count = (long)Math.Floor(cost.count * building.def.resourcesFractionWhenDeconstructed);
                if (count <= 0) continue;
                if (!yields.TryGetValue(cost.thingDef, out var probe)) {
                    var made = ThingMaker.MakeThing(cost.thingDef);
                    yields[cost.thingDef] = probe = (made, EventLootFacts.StorageHeadroom(map, made));
                }
                var item = probe.item;
                var headroom = probe.headroom;
                // A yield nothing stores stays on the ground; only a yield
                // haulers will carry home needs a safe return route. A source
                // no hauler reaches reports false (Safe=false, a route hold);
                // only a reached source with no store cell zeroes headroom.
                if (headroom > 0) {
                    var route = safety.SalvageReturn(building, item);
                    if (route == null) headroom = 0;
                    else result.Safe = result.Safe && route.Value;
                }
                result.Yields.Add(new Obs.SalvageYield { DefName = cost.thingDef.defName, Count = count, UnitValue = item.MarketValue, StorageHeadroom = headroom });
            }
            return result;
        }
        // Cross-read salvage cache (#984): salvage evidence costs ~1 ms per
        // out-of-Home ruin (colonist path searches, return routes, storage
        // headroom), so it is kept per building thingIDNumber for one map of
        // one game and refreshed over frames by RefreshSalvage (driven from
        // ObservationFrameHook, main thread) instead of inside a Read. An
        // entry is stale when its per-building signature (forbidden,
        // burning, hit points, position) or the colony signature it was
        // computed under (visible hazard count, free colonists'
        // identity/drafted/hauling/area, storage groups) changed, or it is
        // older than salvageMaxAgeTicks (four game hours: path lengths and
        // headroom drift with colonist positions and stockpile fill, which
        // no signature tracks); ages differ per entry, so they come due a
        // few at a time rather than all at once. A stale
        // entry keeps serving its old value until the refresher reaches it,
        // oldest first, within salvageFrameBudgetMs per update. Read serves
        // every cached entry and computes only rows with no entry, capped at
        // salvageInlineBudgetMs except on a Read that starts with an empty
        // cache; a row past the cap carries no Salvage (remote salvage holds
        // it as salvage_unknown) and the refresher fills it for the next
        // Read. The refresher only walks targets a Read listed, so a colony
        // that never asks pays nothing.
        private const int salvageMaxAgeTicks = 4 * GenDate.TicksPerHour;
        private const double salvageFrameBudgetMs = 1.5;
        private const double salvageInlineBudgetMs = 5;
        private const int salvageSignatureEveryTicks = 60;
        private sealed class SalvageEntry { internal long Local, Colony, Stamp; internal int Computed; internal Obs.SalvageEvidence Evidence = null!; }
        private static readonly Dictionary<int, SalvageEntry> salvageCache = new Dictionary<int, SalvageEntry>();
        private static readonly Dictionary<int, Building> salvageTargets = new Dictionary<int, Building>();
        private static Game? salvageGame;
        private static int salvageMap = -1;
        private static long salvageSignature, salvageStamp;
        // The shared hauling-safety pass: rebuilt, and the colony signature
        // recomputed from it, at most every salvageSignatureEveryTicks game ticks,
        // so a paused game never rebuilds it (a paused edit lands on resume).
        private static EventLootFacts.HaulingSafety? passSafety;
        private static readonly Dictionary<ThingDef, (Thing item, long headroom)> passYields = new Dictionary<ThingDef, (Thing, long)>();
        private static int passBuilt;
        private static long backgroundRows, backgroundTicks;

        private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private static void DropPass() { passSafety?.Dispose(); passSafety = null; passYields.Clear(); }

        private static EventLootFacts.HaulingSafety Pass(Map map)
        {
            var now = Find.TickManager.TicksGame;
            if (passSafety != null && now >= passBuilt && now - passBuilt < salvageSignatureEveryTicks) return passSafety;
            DropPass();
            passSafety = new EventLootFacts.HaulingSafety(map);
            passBuilt = now;
            salvageSignature = ColonySignature(map, passSafety);
            return passSafety;
        }

        private static Obs.SalvageEvidence Store(Map map, Building b, long local)
        {
            var safety = Pass(map);
            var entry = new SalvageEntry { Local = local, Colony = salvageSignature, Stamp = ++salvageStamp, Computed = Find.TickManager.TicksGame, Evidence = Salvage(map, b, safety, passYields) };
            salvageCache[b.thingIDNumber] = entry;
            return entry.Evidence;
        }

        private static bool Aged(SalvageEntry e)
        {
            var age = Find.TickManager.TicksGame - e.Computed;
            return age < 0 || age >= salvageMaxAgeTicks;
        }

        private static void Forget(IEnumerable<int> gone)
        {
            foreach (var id in gone.ToList()) { salvageTargets.Remove(id); salvageCache.Remove(id); }
        }

        // RefreshSalvage runs once per Unity update on the game thread.
        internal static void RefreshSalvage()
        {
            if (salvageTargets.Count == 0 || salvageGame != Current.Game) return;
            var map = Find.Maps.FirstOrDefault(m => m.uniqueID == salvageMap);
            if (map == null) return;
            var began = Now();
            Pass(map);
            List<int>? gone = null;
            var stale = new List<(long stamp, Building building, long local)>();
            foreach (var kv in salvageTargets) {
                var b = kv.Value;
                if (!b.Spawned || b.Map != map) { (gone ??= new List<int>()).Add(kv.Key); continue; }
                var local = LocalSignature(b);
                if (!salvageCache.TryGetValue(kv.Key, out var e)) stale.Add((long.MinValue, b, local));
                else if (e.Local != local || e.Colony != salvageSignature || Aged(e)) stale.Add((e.Stamp, b, local));
            }
            if (gone != null) Forget(gone);
            foreach (var s in stale.OrderBy(s => s.stamp)) {
                if (Ms(Now() - began) >= salvageFrameBudgetMs) break;
                Store(map, s.building, s.local);
                backgroundRows++;
            }
            backgroundTicks += Now() - began;
        }

        private sealed class SalvageRead
        {
            private readonly Map map;
            private readonly bool unbounded;
            private readonly HashSet<int> seen = new HashSet<int>();
            private long inline, served, inlineTicks;
            private SalvageRead(Map map, bool unbounded) { this.map = map; this.unbounded = unbounded; }

            internal static SalvageRead Begin(Map map)
            {
                if (salvageGame != Current.Game || salvageMap != map.uniqueID) {
                    salvageCache.Clear();
                    salvageTargets.Clear();
                    DropPass();
                    salvageGame = Current.Game;
                    salvageMap = map.uniqueID;
                }
                return new SalvageRead(map, salvageCache.Count == 0);
            }

            internal Obs.SalvageEvidence? Evidence(Building b)
            {
                var id = b.thingIDNumber;
                seen.Add(id);
                salvageTargets[id] = b;
                if (salvageCache.TryGetValue(id, out var e)) { served++; return e.Evidence.Clone(); }
                if (!unbounded && Ms(inlineTicks) >= salvageInlineBudgetMs) return null;
                var began = Now();
                var evidence = Store(map, b, LocalSignature(b));
                inlineTicks += Now() - began;
                inline++;
                return evidence.Clone();
            }

            // End evicts buildings that left the census (removed,
            // deconstructed, now in Home or fogged) and reports the counters.
            internal void End()
            {
                Forget(salvageTargets.Keys.Where(k => !seen.Contains(k)));
                ObservationWork.Captured("clearanceSalvageRecompute", 0, inline, served + inline);
                ObservationWork.Captured("clearanceSalvageBackground", backgroundTicks, backgroundRows);
                backgroundTicks = backgroundRows = 0;
            }
        }

        private static long ColonySignature(Map map, EventLootFacts.HaulingSafety safety)
        {
            unchecked {
                long sig = 17;
                void Mix(long v) { sig = sig * 1_000_003 + v; }
                Mix(safety.HazardCount);
                foreach (var p in safety.People) {
                    Mix(p.thingIDNumber);
                    Mix(p.Drafted ? 1 : 0);
                    Mix(p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) ? 1 : 0);
                    Mix(p.playerSettings?.AreaRestrictionInPawnCurrentMap?.ID ?? -1);
                }
                var groups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
                Mix(groups.Count);
                foreach (var g in groups) { Mix(g.CellsList.Count); Mix((int)(g.Settings?.Priority ?? 0)); }
                return sig;
            }
        }

        private static long LocalSignature(Building b)
        {
            unchecked {
                long sig = b.IsForbidden(Faction.OfPlayer) ? 1 : 0;
                sig = sig * 31 + (b.IsBurning() ? 1 : 0);
                sig = sig * 1_000_003 + b.HitPoints;
                sig = sig * 1_000_003 + b.Position.x * 1024 + b.Position.z;
                return sig;
            }
        }

        private const int dumpSiteCells = 16;
        private const int dumpSiteFloodBound = 512;
        private static List<IntVec3> DumpSites(Map map, Area home, List<Thing> chunks, List<Pawn> haulers)
        {
            var result = new List<IntVec3>();
            if (haulers.Count == 0) return result;
            bool Free(IntVec3 c) => c.InBounds(map) && home[c] && !c.Fogged(map) && c.Standable(map)
                && c.GetRoom(map)?.PsychologicallyOutdoors == true
                && !map.roofCollapseBuffer.IsMarkedToCollapse(c)
                && map.zoneManager.ZoneAt(c) == null && c.GetEdifice(map) == null && NativeZoneCreation.StorageEmpty(c, map);
            bool Reachable(IntVec3 c) => haulers.Any(h => !c.IsForbidden(h) && h.CanReach(c, PathEndMode.OnCell, Danger.None));
            var centroid = new IntVec3((int)chunks.Average(t => t.Position.x), 0, (int)chunks.Average(t => t.Position.z));
            var seed = GenRadial.RadialCellsAround(centroid, 20, true).Where(c => Free(c) && Reachable(c)).Cast<IntVec3?>().FirstOrDefault();
            if (seed == null) return result;
            var seen = new HashSet<IntVec3> { seed.Value };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(seed.Value);
            while (queue.Count > 0 && result.Count < dumpSiteCells && seen.Count < dumpSiteFloodBound) {
                var cell = queue.Dequeue();
                result.Add(cell);
                foreach (var next in GenAdj.CardinalDirections.Select(d => cell + d)) {
                    if (!seen.Add(next) || !Free(next) || !Reachable(next)) continue;
                    queue.Enqueue(next);
                }
            }
            return result;
        }

        private static Obs.ClearanceClass Classify(Building building) =>
            building is Building_AncientCryptosleepCasket ? Obs.ClearanceClass.AncientCasket :
            building.def == ThingDefOf.ShipChunk ? Obs.ClearanceClass.ShipChunk :
            building.def == ThingDefOf.Wall || building is Building_Door ? Obs.ClearanceClass.AncientWallDoor : Obs.ClearanceClass.Other;

        internal static bool AncientDanger(Map map, Building building, Faction player) => AncientDanger(map, building, player, TempleTriggers(map));

        // TempleTriggers lists the map's ancient-temple approach triggers once so
        // a census judging many buildings scans AllThings once (#984).
        internal static List<RectTrigger> TempleTriggers(Map map) =>
            map.listerThings.AllThings.OfType<RectTrigger>().Where(t => t.destroyIfUnfogged
                && t.signalTag?.StartsWith("ancientTempleApproached-", StringComparison.Ordinal) == true).ToList();

        internal static bool AncientDanger(Map map, Building building, Faction player, List<RectTrigger> triggers)
        {
            var occupied = building.OccupiedRect();
            // The warning trigger can disappear when a colonist approaches,
            // before the room opens. Retain the room-content check as well.
            if (triggers.Any(t => t.Rect.CenterCell.Fogged(map) && occupied.Any(c => t.Rect.Contains(c)))) return true;
            var rooms = new HashSet<Room>();
            foreach (var cell in occupied)
                foreach (var offset in GenAdj.CardinalDirectionsAndInside) {
                    var near = cell + offset;
                    if (near.InBounds(map) && near.GetRoom(map) is Room room) rooms.Add(room);
                }
            foreach (var room in rooms) {
                if (room.TouchesMapEdge || room.OpenRoofCount != 0) continue;
                foreach (var thing in room.ContainedAndAdjacentThings) {
                    if (thing is Building_AncientCryptosleepCasket casket && casket.HasAnyContents) return true;
                    if (room.Fogged && (thing is Hive || thing is Pawn pawn && pawn.Faction != null && pawn.Faction.HostileTo(player))) return true;
                }
            }
            return false;
        }
        private static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();
        private static Common.Cell Cell(int x, int z) => new Common.Cell { X = x, Z = z };
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static object Missing(Common.UnavailableReason reason, string detail) => ProtoBoundary.Encode(new Obs.ClearanceTargetsReply { Unavailable = new Common.Unavailable { Reason = reason, Detail = detail } });
    }
}
