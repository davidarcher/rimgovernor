using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only (#1075). On the blank lab it raises a
    // granite block under thick rock roof east of the colonists, holding two
    // compacted steel deposits: one on the face, bordering open ground, and
    // one buried five cells in, fogged with the rest of the interior. A steel
    // stockpile stands beside the colonists and every need is frozen, so the
    // only work the run owes is MaintainResource's steel: mine the face
    // deposit, tunnel to the buried one and mine it, all under the roof.
    // The fixture never designates, mines or builds anything itself.
    public sealed class BuriedSteelFixture
    {
        private const int FaceOffset = 8;   // face column east of the centre
        private const int Depth = 13;       // block extent into the mountain
        private const int HalfWidth = 6;    // block extent either side of the centre line
        private const int BuriedDepth = 5;  // buried deposit columns past the face

        // Layout is fixed from the lab centre, so an audit on a resumed or
        // restarted process reads the same cells prepare wrote.
        private static (CellRect block, List<IntVec3> open, List<IntVec3> buried) Layout(Map map)
        {
            var center = map.Center;
            var face = center.x + FaceOffset;
            return (new CellRect(face, center.z - HalfWidth, Depth, 2 * HalfWidth + 1),
                new List<IntVec3> { new IntVec3(face, 0, center.z - 3), new IntVec3(face, 0, center.z - 2) },
                new List<IntVec3> { new IntVec3(face + BuriedDepth, 0, center.z + 2), new IntVec3(face + BuriedDepth, 0, center.z + 3) });
        }

        [Tool("test/buried_steel", Description = "UNSAFE FOR MODEL EXECUTION. Disposable lab fixture (#1075): action=prepare raises a thick-roofed granite block beside the colonists with one compacted steel deposit on the face and one buried and fogged behind it, a stockpile and frozen needs; action=audit reads the deposits left, the steel on the map and in storage, and any roof lost, collapsed rock or pending collapse in the block.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "prepare", string stockpile = "west")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game with a current map is required.");
                if (action == "audit") return Audit(map);
                if (action != "prepare") throw new ArgumentException("action must be prepare or audit.");
                if (!Find.TickManager.Paused) return new { success = false, reason = "Paused map required for prepare." };
                return Prepare(map, stockpile == "face");
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Prepare(Map map, bool besideFace)
        {
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
            if (people.Count == 0) return new { success = false, reason = "No colonists." };
            var center = map.Center;
            var (block, openDeposit, buriedDeposit) = Layout(map);
            var granite = DefDatabase<ThingDef>.GetNamed("Granite");
            var steel = DefDatabase<ThingDef>.GetNamed("MineableSteel");
            foreach (var c in block.Cells) {
                foreach (var t in c.GetThingList(map).ToList()) t.Destroy(DestroyMode.Vanish);
                var def = openDeposit.Contains(c) || buriedDeposit.Contains(c) ? steel : granite;
                GenSpawn.Spawn(ThingMaker.MakeThing(def), c, map);
                map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
            }
            var setFog = FogSetter(map);
            foreach (var c in block.Cells.Where(c => c.x != block.minX)) setFog(c);
            map.mapDrawer.WholeMapChanged(MapMeshFlagDefOf.FogOfWar);
            map.roofGrid.Drawer.SetDirty();

            // No steel anywhere else: the runway starts at zero.
            foreach (var t in map.listerThings.ThingsOfDef(ThingDefOf.Steel).ToList()) t.Destroy(DestroyMode.Vanish);
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            // The stockpile (the colony extent) sits west of the colonists,
            // away from the face: colony space next to a deposit protects it.
            // stockpile=face (#1133) puts it against the face deposit instead:
            // ore is still mined, followed by a replacement wall.
            var zoneRect = besideFace ? new CellRect(center.x + FaceOffset - 3, center.z - 4, 3, 4) : new CellRect(center.x - 7, center.z - 2, 4, 4);
            foreach (var c in zoneRect.Cells) zone.AddCell(c);
            // Lab colonists start unarmed, and resource reach holds every
            // source outside the base below two armed colonists (far reach,
            // which needs no extent margin, below six).
            var revolver = DefDatabase<ThingDef>.GetNamed("Gun_Revolver");
            foreach (var p in people) {
                if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)) p.workSettings.SetPriority(WorkTypeDefOf.Mining, 1);
                if (p.equipment != null && p.equipment.Primary == null)
                    p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(revolver));
            }
            var frozen = FrozenNeeds.Apply(new string[0]);
            return new {
                success = true,
                center = new { x = center.x, z = center.z },
                block = new { minX = block.minX, minZ = block.minZ, maxX = block.maxX, maxZ = block.maxZ },
                open = openDeposit.Select(c => new { c.x, c.z }).ToList(),
                buried = buriedDeposit.Select(c => new { c.x, c.z }).ToList(),
                buriedFogged = buriedDeposit.All(c => c.Fogged(map)),
                colonists = people.Count,
                frozen,
            };
        }

        private static object Audit(Map map)
        {
            var (block, openDeposit, buriedDeposit) = Layout(map);
            int Left(List<IntVec3> cells) => cells.Count(c => c.GetEdifice(map)?.def.defName == "MineableSteel");
            var inBlock = block.Cells.ToList();
            var steel = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => t.Spawned).ToList();
            return new {
                success = true,
                tick = Find.TickManager.TicksGame,
                openLeft = Left(openDeposit),
                buriedLeft = Left(buriedDeposit),
                steelOnMap = steel.Sum(t => t.stackCount),
                steelStored = map.resourceCounter.GetCount(ThingDefOf.Steel),
                roofless = inBlock.Count(c => c.GetRoof(map) == null),
                collapsedRocks = inBlock.Count(c => c.GetEdifice(map)?.def.defName == "CollapsedRocks"),
                collapsing = inBlock.Count(c => map.roofCollapseBuffer.IsMarkedToCollapse(c)),
                mined = inBlock.Count(c => c.GetEdifice(map) == null),
                replacementWalls = openDeposit.Count(c => c.GetThingList(map).Any(t => t.def == ThingDefOf.Wall || t.def.entityDefToBuild == ThingDefOf.Wall)),
            };
        }

        // Direct fog writes, as MountainFixture: the game only exposes Unfog.
        private static Action<IntVec3> FogSetter(Map map)
        {
            var grid = map.fogGrid;
            object storage = null;
            foreach (var name in new[] { "FogGrid_Unsafe", "fogGridDirect", "fogGrid" }) {
                var prop = AccessTools.Property(typeof(FogGrid), name);
                if (prop != null) { storage = prop.GetValue(grid); if (storage != null) break; }
                var field = AccessTools.Field(typeof(FogGrid), name);
                if (field != null) { storage = field.GetValue(grid); if (storage != null) break; }
            }
            if (storage is bool[] array) return c => array[map.cellIndices.CellToIndex(c)] = true;
            var setter = storage?.GetType().GetMethod("Set", new[] { typeof(int), typeof(bool) })
                ?? storage?.GetType().GetMethod("set_Item", new[] { typeof(int), typeof(bool) });
            if (setter != null) return c => setter.Invoke(storage, new object[] { map.cellIndices.CellToIndex(c), true });
            throw new InvalidOperationException("FogGrid storage unavailable: " + (storage?.GetType().FullName ?? "none"));
        }
    }
}
