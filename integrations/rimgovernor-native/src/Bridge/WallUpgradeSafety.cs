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
            PlayerUiRevision.Observe();
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
            .ById(id) is Building b && b.def == ThingDefOf.Wall ? b : null;
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
            if (ownership && r.UiRevision != PlayerUiRevision.Current) return "Player input invalidated pending demolition";
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
                var budgets = MaterialBudget.Budgets(map);
                var required = ThingDefOf.Wall.CostListAdjusted(stuff).Where(c => c.thingDef == stuff).Sum(c => c.count);
                budgets.TryGetValue(stuff.defName, out var available);
                if (available < required)
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
        internal static WallRemovalRecord NewRecord(Map map, string target, string original, string left, string right, IEnumerable<string> backup,
            string permanent, string material, int x, int z, int nx, int nz) => new WallRemovalRecord {
                Id = Guid.NewGuid().ToString("N"), Target = target, Original = original, Left = left, Right = right,
                Backup = backup.ToList(), Permanent = string.IsNullOrEmpty(permanent) ? null : permanent, Material = material,
                MapId = map.uniqueID, X = x, Z = z, Nx = nx, Nz = nz, Load = Load, UiRevision = PlayerUiRevision.Current };
        /// <summary>Why this record cannot be admitted now, or null with the builders who could take the job. Changes nothing.</summary>
        internal static string? Prepare(WallRemovalRecord r, out List<Pawn> workers)
        {
            Install();
            workers = new List<Pawn>();
            var map = Find.CurrentMap;
            if (map == null || Ledger().Records.Count >= 512) return "Native removal ledger unavailable";
            var wall = Wall(map, r.Target);
            if (wall == null) return "Exact native wall is unavailable";
            var blocker = Check(r, requireDesignation: false);
            if (blocker != null) return blocker;
            // A demolition designation no record of this ledger claims (the
            // player's, placed under Manual) is adopted rather than preserved
            // (#461): the game already accepted it, so the designator is
            // consulted only while the wall is still undesignated. A record
            // of this ledger on the wall is the caller's replay, refused upstream.
            if (map.designationManager.DesignationOn(wall, DesignationDefOf.Deconstruct) == null
                && !new Designator_Deconstruct().CanDesignateThing(wall).Accepted) return "Native deconstruction designator refused";
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
            // An adopted standing designation is not placed twice.
            try { if (map.designationManager.DesignationOn(wall, DesignationDefOf.Deconstruct) == null) new Designator_Deconstruct().DesignateThing(wall); }
            catch { r.Blocker = "Native designation outcome is uncertain"; throw; }
            return map.designationManager.DesignationOn(wall, DesignationDefOf.Deconstruct) == null
                ? "Native demolition designation was not observed" : null;
        }
    }
}
