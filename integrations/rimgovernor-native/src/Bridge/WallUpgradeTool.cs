#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    internal static class WallUpgradeSafety
    {
        private static bool installed;
        private static WallRemovalState? State()
        {
            return Current.Game?.GetComponent<WallRemovalState>();
        }
        private static WallRemovalState Ledger()
        {
            var game = Current.Game ?? throw new InvalidOperationException("No loaded game holds the wall removal ledger.");
            var state = game.GetComponent<WallRemovalState>();
            if (state == null) { state = new WallRemovalState(game); game.components.Add(state); }
            return state;
        }
        private static string? Load => Current.Game?.GetComponent<ColonyIdentity>()?.LoadToken;
        internal static void Install()
        {
            if (installed) return;
            PlayerFrame.ObserveUi();
            var harmony = new Harmony("rimgovernor.wall-upgrade");
            harmony.Patch(AccessTools.Method(typeof(WorkGiver_Deconstruct), nameof(WorkGiver_Deconstruct.HasJobOnThing)),
                postfix: new HarmonyMethod(typeof(WallUpgradeSafety), nameof(Eligible)));
            harmony.Patch(AccessTools.Method(typeof(JobDriver_Deconstruct), "FinishedRemoving"),
                prefix: new HarmonyMethod(typeof(WallUpgradeSafety), nameof(BeforeRemoval)),
                finalizer: new HarmonyMethod(typeof(WallUpgradeSafety), nameof(AfterRemoval)));
            harmony.Patch(AccessTools.Method(typeof(JobDriver_Deconstruct), "MakeNewToils"),
                postfix: new HarmonyMethod(typeof(WallUpgradeSafety), nameof(GuardJob)));
            installed = true;
        }
        internal static Building? Wall(Map map, string id) => map.listerBuildings.allBuildingsColonist
            .FirstOrDefault(b => b.def == ThingDefOf.Wall && b.GetUniqueLoadID() == id);
        internal static bool Stone(Building? b) => b?.Stuff?.stuffProps?.categories?.Contains(StuffCategoryDefOf.Stony) == true;
        private static bool At(Building? b, IntVec3 cell) => b != null && b.Spawned && b.Position == cell
            && !b.IsForbidden(Faction.OfPlayerSilentFail) && !b.IsBurning();
        private static IntVec3 Origin(WallRemovalRecord r) => new IntVec3(r.X, 0, r.Z);
        internal static IEnumerable<IntVec3> Directions => GenAdj.CardinalDirections.Concat(new[] {
            new IntVec3(1, 0, 1), new IntVec3(1, 0, -1), new IntVec3(-1, 0, 1), new IntVec3(-1, 0, -1) });
        internal static bool Corner(IntVec3 normal) => normal.x != 0 && normal.z != 0;
        internal static IntVec3 LeftCell(IntVec3 origin, IntVec3 normal) => Corner(normal)
            ? origin - new IntVec3(normal.x, 0, 0) : origin - new IntVec3(-normal.z, 0, normal.x);
        internal static IntVec3 RightCell(IntVec3 origin, IntVec3 normal) => Corner(normal)
            ? origin - new IntVec3(0, 0, normal.z) : origin + new IntVec3(-normal.z, 0, normal.x);
        internal static IEnumerable<IntVec3> CornerApproaches(IntVec3 origin, IntVec3 normal)
        {
            yield return origin + new IntVec3(normal.x, 0, 0);
            yield return origin + new IntVec3(0, 0, normal.z);
        }
        internal static bool CornerAccess(Map map, IntVec3 origin, IntVec3 normal) =>
            CornerApproaches(origin, normal).All(c => c.InBounds(map) && !c.Fogged(map) && c.Standable(map)
                && !c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame))
            && map.mapPawns.FreeColonistsSpawned.Any(p => !p.Downed && !p.Drafted && !p.InMentalState
                && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) && p.workSettings?.GetPriority(WorkTypeDefOf.Hauling) > 0
                && CornerApproaches(origin, normal).Any(c => !c.IsForbidden(p)
                    && p.CanReach(c, PathEndMode.OnCell, Danger.None)));
        private static IEnumerable<IntVec3> BackupCells(WallRemovalRecord r) => BackupCells(Origin(r), new IntVec3(r.Nx, 0, r.Nz));
        internal static IEnumerable<IntVec3> BackupCells(IntVec3 origin, IntVec3 normal)
        {
            if (!Corner(normal)) {
                var side = new IntVec3(-normal.z, 0, normal.x);
                yield return origin + normal - side; yield return origin + normal; yield return origin + normal + side;
            }
        }
        internal static string? Check(WallRemovalRecord r, bool ownership = true, bool requireDesignation = true)
        {
            var map = Find.CurrentMap;
            if (map == null || map.uniqueID != r.MapId || ownership && r.Load != Load) return "Colony/load/map changed";
            if (r.Blocker != null) return r.Blocker;
            if (ownership && r.UiRevision != PlayerFrame.CurrentUiRevision) return "Player input invalidated pending demolition";
            if (Math.Abs(r.Nx) > 1 || Math.Abs(r.Nz) > 1 || r.Nx == 0 && r.Nz == 0) return "Invalid wall orientation";
            var origin = Origin(r); var normal = new IntVec3(r.Nx, 0, r.Nz);
            var inside = origin - normal;
            if (!inside.InBounds(map) || inside.Fogged(map) || !inside.Roofed(map)
                || !inside.Standable(map)
                || inside.GetRoom(map) == null || inside.GetRoom(map).TouchesMapEdge
                || inside.GetRoom(map).OpenRoofCount != 0) return "Original enclosed roofed interior is unavailable";
            if (!At(Wall(map, r.Left), LeftCell(origin, normal)) || !At(Wall(map, r.Right), RightCell(origin, normal)))
                return "Original neighboring walls changed";
            var target = Wall(map, r.Target);
            if (target == null || target.IsForbidden(Faction.OfPlayerSilentFail) || target.IsBurning()) return "Exact demolition target is unavailable or unsafe";
            if (requireDesignation && map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct) == null)
                return "Demolition designation was removed";
            if (r.Permanent == null) {
                var cells = BackupCells(r).ToList();
                if (!At(target, origin) || target.GetUniqueLoadID() != r.Original || r.Backup.Count != cells.Count
                    || r.Backup.Distinct().Count() != cells.Count)
                    return "Original wall or temporary enclosure identity changed";
                for (int i = 0; i < cells.Count; i++) if (!At(Wall(map, r.Backup[i]), cells[i]) || !Stone(Wall(map, r.Backup[i])))
                    return "Completed stone backup enclosure is unavailable";
                if (Corner(normal) && !CornerAccess(map, origin, normal)) return "Corner salvage and construction access is unavailable";
                var stuff = Corner(normal) ? DefDatabase<ThingDef>.GetNamedSilentFail(r.Material ?? "") : Wall(map, r.Backup[0])?.Stuff;
                if (stuff == null || stuff.stuffProps?.categories?.Contains(StuffCategoryDefOf.Stony) != true
                    || !GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Contains(stuff)
                    || r.Backup.Any(id => Wall(map, id)?.Stuff != stuff)) return "Native stone replacement material changed";
                var policy = ProductionPolicyGuard.LoadedState(); var key = ProductionPolicyGuard.Key(map, stuff.defName);
                if (policy.Stopped.Contains(key)) return "Player resource policy prevents replacement";
                var budgets = ProductionPolicyGuard.Budgets(map);
                var required = ThingDefOf.Wall.CostListAdjusted(stuff).Where(c => c.thingDef == stuff).Sum(c => c.count);
                policy.Commitments.TryGetValue(key, out var held);
                budgets.TryGetValue(stuff.defName, out var available);
                if (available + (Supervisor.IsActive ? Math.Min(required, held) : 0) < required)
                    return "Materials no longer cover the permanent wall and existing reservations";
            } else {
                var permanent = Wall(map, r.Permanent);
                if (!At(permanent, origin) || !Stone(permanent) || !BackupCells(r).Contains(target.Position)
                    || !r.Backup.Contains(r.Target)) return "Completed permanent wall or exact backup identity is unavailable";
            }
            return RoofSupportSafety.Blocker(target, out _);
        }
        private static WallRemovalRecord? Claim(Thing t) => t == null ? null : State()?.Records.LastOrDefault(r =>
            r.Target == t.GetUniqueLoadID() && !r.Complete);
        /// <summary>The ledger's open removal of this exact wall, if any; read-only for the typed census.</summary>
        internal static WallRemovalRecord? Pending(Thing t) => Claim(t);
        private static void Eligible(Thing t, ref bool __result)
        {
            var r = Claim(t); if (r == null || !__result) return;
            if (!Supervisor.IsActive) { __result = false; return; }
            var blocker = Check(r); if (blocker != null) { r.Blocker = blocker; __result = false; }
        }
        private static void GuardJob(JobDriver_Deconstruct __instance)
        {
            if (Claim(__instance.job.targetA.Thing) == null) return;
            __instance.FailOn(() => {
                var r = Claim(__instance.job.targetA.Thing);
                if (r == null) return false;
                var blocker = Check(r);
                if (blocker != null) r.Blocker = blocker;
                return blocker != null || !Supervisor.IsActive;
            });
        }
        private static bool BeforeRemoval(JobDriver_Deconstruct __instance, out WallRemovalRecord? __state)
        {
            __state = Claim(__instance.job.targetA.Thing);
            if (__state == null) return true;
            if (!Supervisor.IsActive) { __state.Blocker = "Demolition requires an active supervised automation window"; return false; }
            var blocker = Check(__state);
            if (blocker != null) { __state.Blocker = blocker; return false; }
            return true;
        }
        private static Exception? AfterRemoval(JobDriver_Deconstruct __instance, WallRemovalRecord __state, Exception __exception)
        {
            if (__state == null || __state.Blocker != null) return __exception;
            if (__exception != null || __instance.job.targetA.Thing?.Destroyed != true)
                __state.Blocker = "Native demolition outcome is unverified";
            else { __state.Complete = true; __state.CompletedTick = Find.TickManager.TicksGame; }
            return __exception;
        }
        internal static object Read(Map map)
        {
            Install();
            return (State()?.Records ?? new List<WallRemovalRecord>()).Where(r => r.MapId == map.uniqueID).Select(r => {
                if (!r.Complete && r.Blocker == null) r.Blocker = Check(r);
                return new { id = r.Id, target = r.Target, complete = r.Complete, completedTick = r.CompletedTick,
                    blocker = r.Blocker, retired = r.Retired,
                    targetPresent = Wall(map, r.Target) != null,
                    designated = Wall(map, r.Target) is Building target
                        && map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct) != null };
            }).ToList();
        }
        internal static object Release(bool dryRun)
        {
            var released = dryRun ? (State()?.Records ?? new List<WallRemovalRecord>()).Count(r => !r.Complete) : ReleaseAll();
            return new { success = true, accepted = true, dryRun, released };
        }
        /// <summary>Invalidate every open guarded removal and drop its designation; returns how many were open.</summary>
        internal static int ReleaseAll()
        {
            var records = (State()?.Records ?? new List<WallRemovalRecord>()).Where(r => !r.Complete).ToList();
            foreach (var r in records) {
                r.Blocker = "Automation stopped; pending demolition invalidated";
                var map = Find.CurrentMap;
                if (map == null || map.uniqueID != r.MapId) continue;
                var target = Wall(map, r.Target);
                if (target == null) continue;
                // Keep the guard on an already-running job until it observes cancellation.
                var designation = map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct);
                if (designation != null) map.designationManager.RemoveDesignation(designation);
                r.Retired = map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct) == null;
            }
            return records.Count;
        }
        internal static WallRemovalRecord NewRecord(Map map, string target, string original, string left, string right, IEnumerable<string> backup,
            string permanent, string material, int x, int z, int nx, int nz) => new WallRemovalRecord {
                Id = Guid.NewGuid().ToString("N"), Target = target, Original = original, Left = left, Right = right,
                Backup = backup.ToList(), Permanent = string.IsNullOrEmpty(permanent) ? null : permanent, Material = material,
                MapId = map.uniqueID, X = x, Z = z, Nx = nx, Nz = nz, Load = Load, UiRevision = PlayerFrame.CurrentUiRevision };
        /// <summary>Why this record cannot be admitted now, or null with the builders who could take the job. Changes nothing.</summary>
        internal static string? Prepare(WallRemovalRecord r, out List<Pawn> workers)
        {
            Install();
            workers = new List<Pawn>();
            var map = Find.CurrentMap;
            if (map == null || Ledger().Records.Count >= 512) return "Native removal ledger unavailable";
            var wall = Wall(map, r.Target);
            if (wall == null) return "Exact native wall is unavailable";
            if (map.designationManager.DesignationOn(wall, DesignationDefOf.Deconstruct) != null)
                return "Existing demolition designation is preserved; observe its original receipt";
            var blocker = Check(r, requireDesignation: false);
            if (blocker != null) return blocker;
            if (!new Designator_Deconstruct().CanDesignateThing(wall).Accepted) return "Native deconstruction designator refused";
            workers = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState
                && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.health.HasHediffsNeedingTend()
                && p.health.hediffSet.BleedRateTotal <= 0
                && p.CanReserveAndReach(wall, PathEndMode.Touch, Danger.None)).ToList();
            return workers.Count == 0 ? "No enabled available builder with safe native access" : null;
        }
        /// <summary>Record the guarded removal and place its native deconstruct designation; null on success.</summary>
        internal static string? Commit(WallRemovalRecord r)
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("No current map to commit a wall removal on.");
            var wall = Wall(map, r.Target) ?? throw new InvalidOperationException("Exact native wall is unavailable.");
            Ledger().Records.Add(r);
            try { new Designator_Deconstruct().DesignateThing(wall); }
            catch { r.Blocker = "Native designation outcome is uncertain"; throw; }
            return map.designationManager.DesignationOn(wall, DesignationDefOf.Deconstruct) == null
                ? "Native demolition designation was not observed" : null;
        }
        internal static object Remove(string target, string original, string left, string right, string backup,
            string permanent, string material, int x, int z, int nx, int nz, bool dryRun)
        {
            object Refuse(string why) => new { success = dryRun, accepted = false, error = why, reason = why };
            var map = Find.CurrentMap;
            if (map == null) return Refuse("Native removal ledger unavailable");
            var r = NewRecord(map, target, original, left, right, (backup ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
                permanent, material, x, z, nx, nz);
            var blocker = Prepare(r, out var workers) ?? (dryRun ? null : Commit(r));
            if (blocker != null) return Refuse(blocker);
            return new { success = true, accepted = true, dryRun, removalId = dryRun ? null : r.Id,
                target, workers = workers.Select(p => p.GetUniqueLoadID()).ToList(),
                meaning = "Guarded native designation; completed pawn demolition is observed separately" };
        }
    }
    public sealed class WallUpgradeTools
    {
        public WallUpgradeTools() { WallUpgradeSafety.Install(); }
        [Tool("home/wall_upgrade_sites", Description = "Read bounded straight-wall and corner replacement geometry and installed stone material costs. Empty exterior backup cells and an enclosed roofed interior are required. No work is admitted or designated.")]
        public async Task<object> Sites(IRimBridgeContext ctx, CancellationToken cancellationToken, string target)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) return new { success = false, error = "Exact wall unavailable" };
                var wall = map.listerBuildings.allBuildingsColonist.SingleOrDefault(b => b.GetUniqueLoadID() == target && b.def == ThingDefOf.Wall);
                if (wall == null) return new { success = false, error = "Exact wall unavailable" };
                var sites = new List<object>();
                foreach (var normal in WallUpgradeSafety.Directions) {
                    var inside = wall.Position - normal; var outside = wall.Position + normal;
                    var cells = WallUpgradeSafety.BackupCells(wall.Position, normal).ToList();
                    if (!inside.InBounds(map) || inside.Fogged(map) || !inside.Roofed(map)
                        || !inside.Standable(map)
                        || inside.GetRoom(map) == null || inside.GetRoom(map).TouchesMapEdge || inside.GetRoom(map).OpenRoofCount != 0
                        || !outside.InBounds(map) || outside.GetRoom(map)?.TouchesMapEdge != true) continue;
                    var left = WallUpgradeSafety.LeftCell(wall.Position, normal).GetEdifice(map);
                    var right = WallUpgradeSafety.RightCell(wall.Position, normal).GetEdifice(map);
                    if (left?.def != ThingDefOf.Wall || right?.def != ThingDefOf.Wall
                        || left.Faction != Faction.OfPlayerSilentFail || right.Faction != Faction.OfPlayerSilentFail) continue;
                    if (WallUpgradeSafety.Corner(normal) && (RoofSupportSafety.Blocker(wall, out _) != null
                        || !WallUpgradeSafety.CornerAccess(map, wall.Position, normal))) continue;
                    if (cells.Any(c => !c.InBounds(map) || c.Fogged(map) || !c.Standable(map) || map.zoneManager.ZoneAt(c) != null
                        || !RoofSupportSafety.GeometryKnown(map, c)
                        || c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame || t is Plant || t.def.category == ThingCategory.Item))) continue;
                    sites.Add(new { x = wall.Position.x, z = wall.Position.z, nx = normal.x, nz = normal.z,
                        left = left.GetUniqueLoadID(), right = right.GetUniqueLoadID(),
                        backupCells = cells.Select(c => new { x = c.x, z = c.z }).ToList() });
                }
                var materials = GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Where(s => s.stuffProps?.categories?.Contains(StuffCategoryDefOf.Stony) == true)
                    .OrderBy(s => s.defName).Select(s => new { defName = s.defName,
                        costs = ThingDefOf.Wall.CostListAdjusted(s).ToDictionary(c => c.thingDef.defName, c => c.count) }).ToList();
                return new { success = true, target, tick = Find.TickManager.TicksGame, sites, materials };
            }, cancellationToken).ConfigureAwait(false);
        [Tool("home/upkeep_wall", Description = "Guard one exact wall's ordinary native demolition during an admitted replacement. Requires completed straight-wall backups, an open corner hauling approach with existing support, or a completed permanent wall for cleanup. Side walls remain unchanged. Rechecks roof/enclosure before native completion; player input, Manual and load invalidate pending work. Controller must independently prove every demolition target's autonomous construction ownership.")]
        public async Task<object> Apply(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action, string target = "", string original = "", string left = "", string right = "", string backup = "",
            string permanent = "", string material = "", int x = 0, int z = 0, int nx = 0, int nz = 0, bool dryRun = true)
            => await ctx.MainThread.InvokeAsync<object>(() => action == "release" ? WallUpgradeSafety.Release(dryRun)
                : action == "remove" ? WallUpgradeSafety.Remove(target, original, left, right, backup, permanent, material, x, z, nx, nz, dryRun)
                : new { success = false, error = "Unknown wall upkeep action" }, cancellationToken).ConfigureAwait(false);
    }
}
