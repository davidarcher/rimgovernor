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
    // Stages an open, guard-free two-casket ancient shrine touching Home on
    // the tribal baseline (#460): a roofed stone room with a player door,
    // two filled AncientCryptosleepCaskets of one group (hostile ancient
    // soldiers, the game's own pod contents), unclaimed and unknown, and a
    // steel longsword in every violence-capable colonist's hands so the
    // melee lock has its staff. All identity comes from arguments or the
    // saved map, so stage reloads exercise the same fixture.
    public sealed class ShrineFixture
    {
        private const int Group = 9460;

        [Tool("test/shrine_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage an open two-casket ancient shrine (filled with hostile ancients) in a roofed stone room touching Home and arm the colonists with longswords; never issues controller orders.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused map required.");
                var player = Faction.OfPlayer;
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                var anchor = people.First(p => !p.Downed);
                // Interior 5x3 at (sx, sz); walls on the ring around it; a door
                // in the south wall's middle. The site is flat, open, unroofed
                // ground the colonist can reach.
                var site = GenRadial.RadialCellsAround(anchor.Position, 40, true).First(c => {
                    var outer = new CellRect(c.x - 3, c.z - 3, 9, 7);
                    return outer.Cells.All(n => n.InBounds(map) && !n.Fogged(map) && !n.Roofed(map) && n.GetEdifice(map) == null
                        && n.Standable(map) && n.GetZone(map) == null && n.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                        && anchor.CanReach(c, PathEndMode.OnCell, Danger.None);
                });
                var interior = new CellRect(site.x - 2, site.z - 1, 5, 3);
                var ring = interior.ExpandedBy(1);
                foreach (var c in ring.ExpandedBy(1).Cells) {
                    foreach (var t in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) t.Destroy(DestroyMode.Vanish);
                    map.areaManager.Home[c] = true;
                }
                var stone = GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Where(d => d.stuffProps.categories.Contains(StuffCategoryDefOf.Stony)).OrderBy(d => d.defName).First();
                var door = new IntVec3(interior.minX + 2, 0, interior.minZ - 1);
                foreach (var c in ring.EdgeCells) {
                    var def = c == door ? ThingDefOf.Door : ThingDefOf.Wall;
                    var wall = ThingMaker.MakeThing(def, stone);
                    wall.SetFaction(player);
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
                    foreach (var thing in ThingSetMakerDefOf.MapGen_AncientPodContents.root.Generate(parms))
                        if (!casket.TryAcceptThing(thing, false)) throw new InvalidOperationException("Casket refused its contents.");
                    if (casket.Faction != null) casket.SetFaction(null);
                    if (!casket.HasAnyContents || !interior.Contains(casket.InteractionCell) || !casket.InteractionCell.Standable(map))
                        throw new InvalidOperationException("Casket must be filled with a standable interaction cell inside the room.");
                    caskets.Add(casket);
                }
                var swordDef = DefDatabase<ThingDef>.GetNamed("MeleeWeapon_LongSword");
                var armed = new List<string>();
                foreach (var p in people) {
                    p.jobs.StopAll();
                    foreach (var bad in p.health.hediffSet.hediffs.Where(h => h.def.isBad && !(h is Hediff_MissingPart)).ToList()) p.health.RemoveHediff(bad);
                    for (int hour = 0; hour < 24; hour++) p.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                    if (p.equipment == null || p.WorkTagIsDisabled(WorkTags.Violent)) continue;
                    var prior = p.equipment.Primary;
                    if (prior != null) prior.Destroy();
                    var sword = (ThingWithComps)ThingMaker.MakeThing(swordDef, ThingDefOf.Steel);
                    p.equipment.AddEquipment(sword);
                    if (p.equipment.Primary != sword) throw new InvalidOperationException("Longsword was not assigned.");
                    armed.Add(p.GetUniqueLoadID());
                }
                var room = caskets[0].GetRoom();
                return new {
                    success = true, caskets = caskets.Select(c => c.GetUniqueLoadID()).ToList(),
                    interactionCells = caskets.Select(c => new { x = c.InteractionCell.x, z = c.InteractionCell.z }).ToList(),
                    x = site.x, z = site.z, door = door.x + "," + door.z, armed,
                    properRoom = room != null && room.ProperRoom && !room.PsychologicallyOutdoors,
                };
            }, cancellationToken);

        [Tool("test/shrine_audit", Description = "Read the staged caskets' contents, every ancient (pawn or corpse) on the map with its state, and the colonists' dead and downed counts.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken, string caskets = "")
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var ids = caskets.Split(';');
                var rows = map.listerThings.AllThings.OfType<Building_Casket>().Where(c => ids.Contains(c.GetUniqueLoadID())).Select(c => new {
                    id = c.GetUniqueLoadID(), hasContents = c.HasAnyContents,
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
                    colonistsDead = colonists.Count(p => p.Dead) + map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().Count(c => c.InnerPawn?.Faction == Faction.OfPlayer && c.InnerPawn.RaceProps.Humanlike),
                    colonistsDowned = colonists.Count(p => !p.Dead && p.Downed),
                };
            }, cancellationToken);
    }
}
