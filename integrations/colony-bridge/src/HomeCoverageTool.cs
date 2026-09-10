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
        private static void Set(Area_Home __instance, IntVec3 c, bool val)
        {
            if (PreparingFixture || !c.InBounds(__instance.Map)) return;
            var state = __instance.Map.GetComponent<HomeCoverageState>();
            if (!val && !state.Excluded[c]) { state.Excluded[c] = true; state.Revision++; }
            if (__instance[c] != val) state.Revision++;
        }
        private static void Clear(Area __instance)
        {
            if (PreparingFixture || !(__instance is Area_Home)) return;
            var state = __instance.Map.GetComponent<HomeCoverageState>();
            foreach (var c in __instance.Map.AllCells) state.Excluded[c] = true;
            state.Revision++;
        }
        private static void Invert(Area __instance)
        {
            if (PreparingFixture || !(__instance is Area_Home)) return;
            var state = __instance.Map.GetComponent<HomeCoverageState>();
            foreach (var c in __instance.ActiveCells) state.Excluded[c] = true;
            state.Revision++;
        }
        internal static IEnumerable<string> Targets(Map map) => map.listerBuildings.allBuildingsColonist
            .Select(b => b.GetUniqueLoadID()).Concat(map.zoneManager.AllZones.OfType<Zone_Stockpile>().Select(z => "stockpile:" + z.ID));
        internal static List<IntVec3> Scope(Map map, string target)
        {
            var cells = new HashSet<IntVec3>();
            var building = map.listerBuildings.allBuildingsColonist.SingleOrDefault(b => b.GetUniqueLoadID() == target);
            if (building != null) {
                if (building.IsForbidden(Faction.OfPlayerSilentFail) || building.IsBurning()) return null;
                cells.UnionWith(building.OccupiedRect());
                foreach (var c in building.OccupiedRect().SelectMany(p => GenAdj.CardinalDirections.Select(d => p + d)).ToList()) {
                    if (!c.InBounds(map) || c.Fogged(map)) continue;
                    var room = c.GetRoom(map);
                    if (room == null || room.TouchesMapEdge || room.OpenRoofCount != 0 || room.CellCount > 128) continue;
                    cells.UnionWith(room.Cells);
                }
            } else {
                var zone = map.zoneManager.AllZones.OfType<Zone_Stockpile>().SingleOrDefault(z => "stockpile:" + z.ID == target);
                if (zone == null) return null;
                cells.UnionWith(zone.cells);
            }
            if (cells.Count == 0 || cells.Count > 256 || cells.Any(c => !c.InBounds(map) || c.Fogged(map))) return null;
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
            if (!state.Initialized) {
                // Existing omissions may be player choices predating observation.
                foreach (var target in Targets(map)) foreach (var c in Scope(map, target) ?? new List<IntVec3>())
                    if (!map.areaManager.Home[c]) state.Excluded[c] = true;
                state.Initialized = true;
            }
            return state;
        }
        internal static object Read(Map map)
        {
            var state = State(map);
            var targets = Targets(map).OrderBy(id => id).Select(id => {
                var cells = Scope(map, id);
                var missing = cells?.Where(c => !map.areaManager.Home[c]).ToList();
                return new { id, shape = cells == null ? null : Shape(id, cells), missing = missing?.Count,
                    excluded = missing?.Count(c => state.Excluded[c]),
                    blocker = cells == null ? "Bounded visible native facility geometry unavailable" : null,
                    cells = cells?.Select(c => new { x = c.x, z = c.z }).ToList() };
            }).Where(r => r.missing != 0).ToList();
            return new { revision = state.Revision, targets };
        }
        internal static object Apply(string target, string shape, long revision, bool dryRun)
        {
            var map = Find.CurrentMap;
            object Refuse(string error) => new { success = dryRun, accepted = false, error };
            if (map == null || Find.TickManager.CurTimeSpeed != TimeSpeed.Paused) return Refuse("Paused current map required");
            var state = State(map); var cells = Scope(map, target);
            if (state.Revision != revision || cells == null || Shape(target, cells) != shape)
                return Refuse("Home area or native facility geometry changed");
            var missing = cells.Where(c => !map.areaManager.Home[c]).ToList();
            if (missing.Any(c => state.Excluded[c])) return Refuse("Player or pre-observation Home exclusions are preserved");
            if (!dryRun) foreach (var c in missing) map.areaManager.Home[c] = true;
            return new { success = true, accepted = true, dryRun, target, shape, changed = missing.Count,
                covered = cells.All(c => map.areaManager.Home[c]), revision = state.Revision };
        }
    }
    public sealed class HomeCoverageTools
    {
        public HomeCoverageTools() { HomeCoverage.Install(); }
        [Tool("home/upkeep_home", Description = "Add Home only over the observed bounded footprint of an exact facility or stockpile. Native enclosed roofed rooms may be included. Preserves player removals and pre-observation omissions. Requires unchanged native geometry and area revision; controller must independently prove autonomous ownership. Does not alter allowed areas or perform pawn work.")]
        public async Task<object> Apply(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string target, string shape, long revision, bool dryRun = true)
            => await ctx.MainThread.InvokeAsync<object>(() => HomeCoverage.Apply(target, shape, revision, dryRun), cancellationToken).ConfigureAwait(false);
    }
}
