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
        // Every Home edit advances the revision; once the controller has
        // observed the map, a removal joins the exclusion ledger and a cell
        // set back to Home (by anyone, including ExtendHome) leaves it
        // (#314). Fixture staging is neither.
        private static void Set(Area_Home __instance, IntVec3 c, bool val)
        {
            if (PreparingFixture || !c.InBounds(__instance.Map)) return;
            var state = __instance.Map.GetComponent<HomeCoverageState>();
            if (__instance[c] != val) state.Revision++;
            if (!state.Initialized) return;
            if (val) state.Excluded.Remove(c); else if (__instance[c]) state.Excluded.Add(c);
        }
        private static void Clear(Area __instance)
        {
            if (PreparingFixture || !(__instance is Area_Home)) return;
            var state = __instance.Map.GetComponent<HomeCoverageState>();
            state.Revision++;
            if (state.Initialized) state.Excluded.UnionWith(__instance.ActiveCells);
        }
        private static void Invert(Area __instance)
        {
            if (PreparingFixture || !(__instance is Area_Home)) return;
            var state = __instance.Map.GetComponent<HomeCoverageState>();
            state.Revision++;
            if (!state.Initialized) return;
            foreach (var c in __instance.Map.AllCells) { if (__instance[c]) state.Excluded.Add(c); else state.Excluded.Remove(c); }
        }
        // Targets are named by unique load id on both sides of the census:
        // buildings as native does, stockpiles as the CreateZone receipt's
        // zone_id, which is what a controller-created stockpile is owned by
        // (#315).
        internal static IEnumerable<string> Targets(Map map) => map.listerBuildings.allBuildingsColonist
            .Select(b => b.GetUniqueLoadID()).Concat(map.zoneManager.AllZones.OfType<Zone_Stockpile>().Select(z => z.GetUniqueLoadID()));
        internal static List<IntVec3>? Scope(Map map, string target)
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
                var zone = map.zoneManager.AllZones.OfType<Zone_Stockpile>().SingleOrDefault(z => z.GetUniqueLoadID() == target);
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
            state.Initialized = true;
            return state;
        }
        internal const string ExcludedBlocker = "Player removed Home over part of the footprint; the exclusion is preserved";
        internal static int Excluded(HomeCoverageState state, IEnumerable<IntVec3> missing) => missing.Count(state.Excluded.Contains);
        internal static object Read(Map map)
        {
            var state = State(map);
            var targets = Targets(map).OrderBy(id => id).Select(id => {
                var cells = Scope(map, id);
                var missing = cells?.Where(c => !map.areaManager.Home[c]).ToList();
                var excluded = missing == null ? 0 : Excluded(state, missing);
                return new { id, shape = cells == null ? null : Shape(id, cells), missing = missing?.Count, excluded,
                    blocker = cells == null ? "Bounded visible native facility geometry unavailable" : excluded > 0 ? ExcludedBlocker : null,
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
            if (Excluded(state, missing) > 0) return Refuse(ExcludedBlocker);
            if (!dryRun) foreach (var c in missing) map.areaManager.Home[c] = true;
            return new { success = true, accepted = true, dryRun, target, shape, changed = missing.Count,
                covered = cells.All(c => map.areaManager.Home[c]), revision = state.Revision };
        }
    }
    public sealed class HomeCoverageTools
    {
        public HomeCoverageTools() { HomeCoverage.Install(); }
        [Tool("home/upkeep_home", Description = "Add Home only over the observed bounded footprint of an exact facility or stockpile. Native enclosed roofed rooms may be included. Preserves player removals recorded since observation began. Requires unchanged native geometry and area revision; controller must independently prove autonomous ownership. Does not alter allowed areas or perform pawn work.")]
        public async Task<object> Apply(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string target, string shape, long revision, bool dryRun = true)
            => await ctx.MainThread.InvokeAsync<object>(() => HomeCoverage.Apply(target, shape, revision, dryRun), cancellationToken).ConfigureAwait(false);
    }
}
