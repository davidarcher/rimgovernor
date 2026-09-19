#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class HomeCoverage
    {
        private static bool installed;
        internal static bool PreparingFixture = false;
        internal static void Install()
        {
            if (installed) return;
            var harmony = new Harmony("rimgovernor.home-coverage");
            harmony.Patch(AccessTools.Method(typeof(Area_Home), "Set"),
                prefix: new HarmonyMethod(typeof(HomeCoverage), nameof(Set)));
            harmony.Patch(AccessTools.Method(typeof(Area), nameof(Area.Clear)),
                prefix: new HarmonyMethod(typeof(HomeCoverage), nameof(Clear)));
            harmony.Patch(AccessTools.Method(typeof(Area), nameof(Area.Invert)),
                prefix: new HarmonyMethod(typeof(HomeCoverage), nameof(Invert)));
            installed = true;
        }
        // Every Home edit advances the revision. Autonomous maintenance
        // restores missing cells; there is no player-exclusion ledger.
        private static void Set(Area_Home __instance, IntVec3 c, bool val)
        {
            if (PreparingFixture || !c.InBounds(__instance.Map)) return;
            var state = __instance.Map.GetComponent<HomeCoverageState>();
            if (__instance[c] != val) state.Revision++;
        }
        private static void Clear(Area __instance)
        {
            if (PreparingFixture || !(__instance is Area_Home)) return;
            var state = __instance.Map.GetComponent<HomeCoverageState>();
            state.Revision++;
        }
        private static void Invert(Area __instance)
        {
            if (PreparingFixture || !(__instance is Area_Home)) return;
            var state = __instance.Map.GetComponent<HomeCoverageState>();
            state.Revision++;
        }
        // Targets are named by unique load id on both sides of the census:
        // buildings as native does, stockpiles as the CreateZone receipt's
        // zone_id, which is what a controller-created stockpile is owned by
        // (#315).
        internal static IEnumerable<string> Targets(Map map) => map.listerBuildings.allBuildingsColonist
            .Select(b => b.GetUniqueLoadID()).Concat(map.zoneManager.AllZones.OfType<Zone_Stockpile>().Select(z => z.GetUniqueLoadID()));
        internal static List<IntVec3>? Scope(Map map, string target)
        {
            var cells = FullScope(map, target);
            if (cells == null) return null;
            return HomeCoverageGeometry.Batch(cells, c => map.areaManager.Home[c])
                .OrderBy(c => c.x).ThenBy(c => c.z).ToList();
        }

        // Derive the complete geometry before choosing a write batch. Crossing
        // doors is allowed only between visible enclosed roofed rooms, never
        // through an outside doorway or an open gap between buildings.
        internal static List<IntVec3>? FullScope(Map map, string target)
        {
            var cells = new HashSet<IntVec3>();
            var building = map.listerBuildings.allBuildingsColonist.SingleOrDefault(b => b.GetUniqueLoadID() == target);
            if (building != null) {
                if (building.IsForbidden(Faction.OfPlayerSilentFail) || building.IsBurning()) return null;
                var rooms = new Dictionary<Room, bool>();
                bool Enclosed(IntVec3 c) {
                    if (!c.InBounds(map) || c.Fogged(map)) return false;
                    var room = c.GetRoom(map);
                    if (room == null || room.IsDoorway) return false;
                    if (!rooms.TryGetValue(room, out var valid)) {
                        valid = !room.TouchesMapEdge && room.OpenRoofCount == 0 && room.Cells.All(p => !p.Fogged(map));
                        rooms.Add(room, valid);
                    }
                    return valid;
                }
                IEnumerable<IntVec3> Neighbors(IntVec3 c) => GenAdj.CardinalDirections.Select(d => c + d);
                bool Interior(IntVec3 c) {
                    if (!c.InBounds(map) || c.Fogged(map)) return false;
                    var edifice = c.GetEdifice(map);
                    if (edifice is Building_Door door) {
                        if (door.Faction != Faction.OfPlayerSilentFail || door.IsForbidden(Faction.OfPlayerSilentFail) || door.IsBurning()) return false;
                        return Neighbors(c).Where(Enclosed).Select(p => p.GetRoom(map)).Distinct().Count() == 2;
                    }
                    return (edifice == null || edifice.def.passability != Traversability.Impassable) && Enclosed(c);
                }
                cells = HomeCoverageGeometry.Connected(building.OccupiedRect(), Neighbors, Interior);
            } else {
                var zone = map.zoneManager.AllZones.OfType<Zone_Stockpile>().SingleOrDefault(z => z.GetUniqueLoadID() == target);
                if (zone == null) return null;
                cells.UnionWith(zone.cells);
            }
            if (cells.Count == 0 || cells.Any(c => !c.InBounds(map) || c.Fogged(map))) return null;
            return cells.OrderBy(c => c.x).ThenBy(c => c.z).ToList();
        }
        internal static string Shape(string target, List<IntVec3> cells)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                target + ":" + string.Join(";", cells.Select(c => c.x + "," + c.z))))).Replace("-", "").ToLowerInvariant();
        }
        internal static HomeCoverageState State(Map map)
        {
            Install();
            var state = map.GetComponent<HomeCoverageState>();
            return state;
        }
        internal static object Read(Map map)
        {
            var state = State(map);
            var targets = Targets(map).OrderBy(id => id).Select(id => {
                var cells = Scope(map, id);
                var missing = cells?.Where(c => !map.areaManager.Home[c]).ToList();
                return new { id, shape = cells == null ? null : Shape(id, cells), missing = missing?.Count, excluded = 0,
                    blocker = cells == null ? "Visible native facility geometry unavailable" : null,
                    cells = cells?.Select(c => new { x = c.x, z = c.z }).ToList() };
            }).Where(r => r.missing != 0).ToList();
            return new { revision = state.Revision, targets };
        }
        internal static object Apply(string target, string shape, long revision, bool dryRun)
        {
            var map = Find.CurrentMap;
            object Refuse(string error) => new { success = dryRun, accepted = false, error };
            if (map == null) return Refuse("Current map required");
            var state = State(map); var cells = Scope(map, target);
            if (state.Revision != revision || cells == null || Shape(target, cells) != shape)
                return Refuse("Home area or native facility geometry changed");
            var missing = cells.Where(c => !map.areaManager.Home[c]).ToList();
            if (!dryRun) foreach (var c in missing) map.areaManager.Home[c] = true;
            return new { success = true, accepted = true, dryRun, target, shape, changed = missing.Count,
                covered = cells.All(c => map.areaManager.Home[c]), revision = state.Revision };
        }
    }
    public sealed class HomeCoverageTools
    {
        public HomeCoverageTools() { HomeCoverage.Install(); }
        [Tool("home/upkeep_home", Description = "Restore Home in a bounded batch over an owned facility and connected enclosed roofed rooms, or an exact stockpile. Requires unchanged native geometry and area revision; controller must independently prove autonomous ownership. Does not alter allowed areas or perform pawn work.")]
        public async Task<object> Apply(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string target, string shape, long revision, bool dryRun = true)
            => await ctx.MainThread.InvokeAsync<object>(() => HomeCoverage.Apply(target, shape, revision, dryRun), cancellationToken).ConfigureAwait(false);
    }
}
