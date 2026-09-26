using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Two-casket shrine on the tribal baseline. The default stages hostile
    // casket opening; sealedBreach stages a fogged guard room, one empty
    // casket, one filled casket and an armed squad behind three traps.
    // Returned map identities survive checkpoint reloads.
    public sealed class ShrineFixture
    {
        private const int Group = 9460;

        [Tool("test/shrine_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage a roofed two-casket shrine: open with longswords by default; sealedBreach stages one breach wall, scyther, empty/filled caskets, rifles and three traps; never issues controller orders.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, bool sealedBreach = false, bool heat = false)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused map required.");
                var player = Faction.OfPlayer;
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                var anchor = people.First(p => !p.Downed);
                // Clear the whole footprint, including the sealed variant's
                // trap lane and squad positions. Natural obstructions are fixture
                // preparation; existing buildings and zones remain protected.
                CellRect Footprint(IntVec3 c) => heat
                    ? new CellRect(c.x - 6, c.z - 10, 13, 15)
                    : sealedBreach
                    ? new CellRect(c.x - 4, c.z - 10, 11, 15)
                    : new CellRect(c.x - 3, c.z - 3, 9, 7);
                var candidates = map.AllCells.OrderBy(c => c.DistanceToSquared(anchor.Position)).Where(c =>
                    !c.Fogged(map) && Footprint(c).Cells.All(n => n.InBounds(map)
                        && (n.GetRoof(map) == null || n.GetRoof(map).isNatural)
                        && (n.GetEdifice(map) == null || n.GetEdifice(map) is Mineable)
                        && (n.Standable(map) || n.GetEdifice(map) is Mineable)
                        && n.GetZone(map) == null && n.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                    && anchor.CanReach(c, PathEndMode.OnCell, Danger.None)).Take(1).ToList();
                if (candidates.Count == 0)
                    throw new InvalidOperationException("Shrine fixture needs reachable, zone-free Heavy terrain on the map.");
                var site = candidates[0];
                foreach (var c in Footprint(site).Cells) {
                    map.fogGrid.Unfog(c);
                    map.roofGrid.SetRoof(c, null);
                    if (c.GetEdifice(map) is Mineable rock) rock.Destroy(DestroyMode.Vanish);
                    foreach (var t in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) t.Destroy(DestroyMode.Vanish);
                }
                if (Footprint(site).Cells.Any(c => !c.Standable(map) || c.Fogged(map) || c.Roofed(map) || c.GetEdifice(map) != null))
                    throw new InvalidOperationException("Shrine footprint did not clear to open, standable ground.");
                var interior = new CellRect(site.x - 2, site.z - 1, 5, 3);
                var ring = interior.ExpandedBy(1);
                if (sealedBreach)
                    foreach (var c in map.areaManager.Home.ActiveCells.ToList()) map.areaManager.Home[c] = false;
                foreach (var c in ring.ExpandedBy(1).Cells) map.areaManager.Home[c] = true;
                var stone = GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Where(d => d.stuffProps.categories.Contains(StuffCategoryDefOf.Stony)).OrderBy(d => d.defName).First();
                var door = new IntVec3(interior.minX + 2, 0, interior.minZ - 1);
                foreach (var c in ring.EdgeCells) {
                    var def = c == door && !sealedBreach ? ThingDefOf.Door : ThingDefOf.Wall;
                    var wall = ThingMaker.MakeThing(def, stone);
                    if (!sealedBreach || c != door) wall.SetFaction(player);
                    GenSpawn.Spawn(wall, c, map);
                }
                foreach (var c in ring.Cells) map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                var casketDef = DefDatabase<ThingDef>.GetNamed("AncientCryptosleepCasket");
                var caskets = new List<Building_AncientCryptosleepCasket>();
                foreach (var x in new[] { interior.minX + 1, interior.minX + 3 }) {
                    var casket = (Building_AncientCryptosleepCasket)ThingMaker.MakeThing(casketDef);
                    casket.groupID = Group;
                    GenSpawn.Spawn(casket, new IntVec3(x, 0, interior.minZ), map, Rot4.North);
                    var parms = default(ThingSetMakerParams);
                    parms.podContentsType = PodContentsType.AncientHostile;
                    parms.tile = map.Tile;
                    foreach (var thing in sealedBreach && caskets.Count == 0 ? new List<Thing>() : ThingSetMakerDefOf.MapGen_AncientPodContents.root.Generate(parms))
                        if (!casket.TryAcceptThing(thing, false)) throw new InvalidOperationException("Casket refused its contents.");
                    if (casket.Faction != null) casket.SetFaction(null);
                    if ((!sealedBreach && !casket.HasAnyContents) || !interior.Contains(casket.InteractionCell) || !casket.InteractionCell.Standable(map))
                        throw new InvalidOperationException("Casket must be filled with a standable interaction cell inside the room.");
                    caskets.Add(casket);
                }
                var swordDef = DefDatabase<ThingDef>.GetNamed(sealedBreach || heat ? "Gun_AssaultRifle" : "MeleeWeapon_LongSword");
                var armed = new List<string>();
                foreach (var p in people) {
                    p.jobs.StopAll();
                    if (sealedBreach || heat) {
                        p.Position = door + IntVec3.South * 8 + IntVec3.East * (people.IndexOf(p) - 3);
                        p.Notify_Teleported();
                        if (p.skills != null) p.skills.GetSkill(SkillDefOf.Shooting).Level = 16;
                    }
                    // Every variant needs a builder on the clock, not only the
                    // ones that deconstruct or build: ClearAncientShrine is
                    // profiled as Construction labor and the startup
                    // prerequisites withhold the baseline's only free
                    // construction pawn, so the casket opening was never
                    // selected at all (labor_unavailable, #659).
                    if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) {
                        p.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                        p.skills.GetSkill(SkillDefOf.Construction).Level = 16;
                    }
                    foreach (var bad in p.health.hediffSet.hediffs.Where(h => h.def.isBad && !(h is Hediff_MissingPart)).ToList()) p.health.RemoveHediff(bad);
                    for (int hour = 0; hour < 24; hour++) p.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                    if (p.equipment == null || p.WorkTagIsDisabled(WorkTags.Violent)) continue;
                    var prior = p.equipment.Primary;
                    if (prior != null) prior.Destroy();
                    var sword = (ThingWithComps)ThingMaker.MakeThing(swordDef, sealedBreach || heat ? null : ThingDefOf.Steel);
                    p.equipment.AddEquipment(sword);
                    if (p.equipment.Primary != sword) throw new InvalidOperationException("Fixture weapon was not assigned.");
                    armed.Add(p.GetUniqueLoadID());
                }
                string guard = "", salvage = "", breach = "";
                if (sealedBreach) {
                    breach = door.GetEdifice(map).GetUniqueLoadID();
                    var mech = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Mech_Scyther"), Faction.OfMechanoids);
                    GenSpawn.Spawn(mech, site + IntVec3.North, map);
                    guard = mech.GetUniqueLoadID();
                    var scrap = ThingMaker.MakeThing(ThingDefOf.Wall, stone);
                    GenSpawn.Spawn(scrap, site + IntVec3.West * 2, map);
                    salvage = scrap.GetUniqueLoadID();
                    for (int i = 1; i <= 3; i++) {
                        var trap = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("TrapSpike"), ThingDefOf.Steel);
                        trap.SetFaction(player);
                        GenSpawn.Spawn(trap, door + IntVec3.South * (i * 2), map);
                    }
                    var fog = FogSetter(map);
                    foreach (var cell in interior.Cells) fog(cell);
                    if (people.Count(p => !p.Downed && !p.WorkTagIsDisabled(WorkTags.Violent)) < 3)
                        throw new InvalidOperationException("Three healthy defenders required.");
                }
                if (heat) {
                    // The controller builds the missing door and heaters. The
                    // fixture supplies researched technology, materials and a
                    // powered network, so it starts at the heat precondition.
                    door.GetEdifice(map).Destroy(DestroyMode.Vanish);
                    var heaterDef = DefDatabase<ThingDef>.GetNamed("Heater");
                    foreach (var project in heaterDef.researchPrerequisites ?? new List<ResearchProjectDef>())
                        Find.ResearchManager.FinishProject(project, false);
                    var conduitDef = DefDatabase<ThingDef>.GetNamed("PowerConduit");
                    foreach (var cell in ring.Cells) {
                        var conduit = ThingMaker.MakeThing(conduitDef); conduit.SetFaction(player); GenSpawn.Spawn(conduit, cell, map);
                    }
                    var generator = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator"));
                    generator.SetFaction(player); GenSpawn.Spawn(generator, new IntVec3(ring.minX - 2, 0, ring.minZ), map);
                    generator.TryGetComp<CompRefuelable>().Refuel(75f);
                    int index = 0;
                    foreach (var supply in new[] { ("Steel", 600), ("ComponentIndustrial", 12), ("WoodLog", 200) }) {
                        var def = DefDatabase<ThingDef>.GetNamed(supply.Item1);
                        for (int left = supply.Item2; left > 0; left -= def.stackLimit) {
                            var stack = ThingMaker.MakeThing(def); stack.stackCount = Math.Min(left, def.stackLimit);
                            var cell = door + IntVec3.South * (3 + index / 5) + IntVec3.East * (index % 5 - 2);
                            GenSpawn.Spawn(stack, cell, map); stack.SetForbidden(false, false); map.areaManager.Home[cell] = true; index++;
                        }
                    }
                }
                var room = caskets[0].GetRoom();
                return new {
                    success = true, guard, salvage, breach, sealedBreach, heat, caskets = caskets.Select(c => c.GetUniqueLoadID()).ToList(),
                    interactionCells = caskets.Select(c => new { x = c.InteractionCell.x, z = c.InteractionCell.z }).ToList(),
                    x = site.x, z = site.z, door = door.x + "," + door.z, armed,
                    properRoom = room != null && room.ProperRoom && !room.PsychologicallyOutdoors,
                };
            }, cancellationToken);

        [Tool("test/shrine_audit", Description = "Read the staged caskets' contents, every ancient (pawn or corpse) on the map with its state, and the colonists' dead and downed counts.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken, string caskets = "", string guard = "", string breach = "", string salvage = "")
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var ids = caskets.Split(';');
                var rows = map.listerThings.AllThings.OfType<Building_Casket>().Where(c => ids.Contains(c.GetUniqueLoadID())).Select(c => new {
                    id = c.GetUniqueLoadID(), fogged = c.Position.Fogged(map), hasContents = c.HasAnyContents, playerOwned = c.Faction == Faction.OfPlayer,
                    designated = map.designationManager.DesignationOn(c, DesignationDefOf.Open) != null,
                }).ToList();
                bool ancient(Pawn p) => p.Faction != null && p.Faction.def == FactionDefOf.Ancients || p.Faction != null && p.Faction.def == FactionDefOf.AncientsHostile;
                var occupants = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike && ancient(p)).Select(p => new {
                    id = p.GetUniqueLoadID(), dead = p.Dead, downed = p.Downed, prisoner = p.IsPrisonerOfColony, hostile = p.Faction.HostileTo(Faction.OfPlayer), faction = p.Faction.def.defName,
                }).Cast<object>().ToList();
                occupants.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().Where(c => c.InnerPawn != null && c.InnerPawn.RaceProps.Humanlike && ancient(c.InnerPawn)).Select(c => new {
                    id = c.InnerPawn.GetUniqueLoadID(), dead = true, downed = false, prisoner = false, hostile = c.InnerPawn.Faction.HostileTo(Faction.OfPlayer), faction = c.InnerPawn.Faction.def.defName,
                }));
                var colonists = map.mapPawns.AllPawns.Where(p => p.IsColonist).ToList();
                return new {
                    success = true, caskets = rows, occupants,
                    guardPresent = map.mapPawns.AllPawnsSpawned.Any(p => p.GetUniqueLoadID() == guard && !p.Dead),
                    guardDead = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().Any(c => c.InnerPawn?.GetUniqueLoadID() == guard),
                    breachPresent = map.listerThings.AllThings.Any(t => t.GetUniqueLoadID() == breach),
                    salvagePresent = map.listerThings.AllThings.Any(t => t.GetUniqueLoadID() == salvage),
                    colonistsDead = colonists.Count(p => p.Dead) + map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().Count(c => c.InnerPawn?.Faction == Faction.OfPlayer && c.InnerPawn.RaceProps.Humanlike),
                    colonistsDowned = colonists.Count(p => !p.Dead && p.Downed),
                };
            }, cancellationToken);
        private static Action<IntVec3> FogSetter(Map map)
        {
            var grid = map.fogGrid;
            object storage = null;
            foreach (var name in new[] { "FogGrid_Unsafe", "fogGridDirect", "fogGrid" }) {
                var prop = HarmonyLib.AccessTools.Property(typeof(FogGrid), name);
                if (prop != null) { storage = prop.GetValue(grid); if (storage != null) break; }
                var field = HarmonyLib.AccessTools.Field(typeof(FogGrid), name);
                if (field != null) { storage = field.GetValue(grid); if (storage != null) break; }
            }
            if (storage is bool[] array) return c => array[map.cellIndices.CellToIndex(c)] = true;
            // NativeBitArray/NativeArray copies still point at the same native
            // memory, so writing through a boxed copy updates the grid.
            var setter = storage?.GetType().GetMethod("Set", new[] { typeof(int), typeof(bool) })
                ?? storage?.GetType().GetMethod("set_Item", new[] { typeof(int), typeof(bool) });
            if (setter != null) return c => setter.Invoke(storage, new object[] { map.cellIndices.CellToIndex(c), true });
            throw new InvalidOperationException("FogGrid storage unavailable: " + (storage?.GetType().FullName ?? "none"));
        }

    }
}
