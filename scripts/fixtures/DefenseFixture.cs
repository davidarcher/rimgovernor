using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (issue #5 M4). Ops: terrain (a rock
    // band around the colony with one straight corridor gap), stock (wood),
    // ranged (rifles for existing colonists), raid (a real RaidEnemy incident
    // with the chosen strategy/arrival), damage (one wall hit), inspect
    // (colonists on trap cells, sprung traps, hostiles). No completed-work
    // injection: construction, movement and combat stay native.
    public sealed class DefenseFixture
    {
        private const int Half = 22;
        private const int BandInner = 13;
        private const int GapHalfWidth = 1;

        [Tool("test/defense_setup", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable defensive-layout fixture: op=terrain|stock|ranged|raid|damage|inspect.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken, string op, string strategy = "ImmediateAttack", string arrival = "EdgeWalkIn", int points = 0, string wall = "", int rifles = 3)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var center = map.Center;
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Count > 0) center = new IntVec3((int)colonists.Average(p => p.Position.x), 0, (int)colonists.Average(p => p.Position.z));
                switch (op)
                {
                    case "terrain": return Terrain(map, center);
                    case "stock": return Stock(map, center);
                    case "ranged": return Ranged(map, colonists, rifles);
                    case "raid": return Raid(map, strategy, arrival, points);
                    case "damage": return Damage(map, wall);
                    case "inspect": return Inspect(map, player);
                    case "quiet": return Quiet();
                    default: return Refuse("Use terrain, stock, ranged, raid, damage, inspect or quiet.");
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        // Terrain surrounds the colony's 45x45 census square with a granite
        // band (radius 13..22 from the colonists' centre) leaving one 3-wide
        // straight gap on the north side, so the only edge approach is a
        // corridor the layout policy can choke. Impassable edifices already
        // close their cell; player buildings and pawns are left alone and
        // reported as open; everything else on a band cell is removed.
        private static object Terrain(Map map, IntVec3 center)
        {
            var granite = DefDatabase<ThingDef>.GetNamed("Granite");
            if (center.x - Half < 1 || center.z - Half < 1 || center.x + Half >= map.Size.x - 1 || center.z + Half >= map.Size.z - 1)
                return Refuse("Colony centre is too close to the map edge for the rock band.");
            var placed = new List<object>(); var open = new List<object>(); var skipped = 0;
            for (var dx = -Half; dx <= Half; dx++)
                for (var dz = -Half; dz <= Half; dz++)
                {
                    var ring = Math.Max(Math.Abs(dx), Math.Abs(dz));
                    if (ring < BandInner) continue;
                    if (dz > 0 && Math.Abs(dx) <= GapHalfWidth) continue; // north gap
                    var cell = new IntVec3(center.x + dx, 0, center.z + dz);
                    if (!cell.InBounds(map)) { skipped++; continue; }
                    var edifice = cell.GetEdifice(map);
                    if (edifice != null)
                    {
                        // Any impassable non-door edifice already closes the cell
                        // (natural rock, ancient ruin walls); passable ones the
                        // player owns are left alone and reported, unowned ones
                        // (ruin doors, furniture) are replaced by rock.
                        if (edifice.def.passability == Traversability.Impassable && !edifice.def.IsDoor) { skipped++; continue; }
                        if (edifice.Faction == Faction.OfPlayer) { open.Add(new { x = cell.x, z = cell.z, edifice = edifice.def.defName }); continue; }
                    }
                    // Player pawns keep their cell open; wild or ruin pawns (dormant
                    // mechanoids, insects) are removed with the rest of the cell.
                    if (cell.GetThingList(map).Any(t => t is Pawn p && p.Faction == Faction.OfPlayer)) { open.Add(new { x = cell.x, z = cell.z, edifice = "pawn" }); continue; }
                    foreach (var thing in cell.GetThingList(map).Where(t => t.def.category != ThingCategory.Filth).ToList()) thing.Destroy();
                    GenSpawn.Spawn(ThingMaker.MakeThing(granite), cell, map);
                    placed.Add(new { x = cell.x, z = cell.z });
                }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            var gap = Enumerable.Range(BandInner, Half - BandInner + 1).Select(d => new { x = center.x, z = center.z + d }).ToList();
            return new { success = open.Count == 0, center = new { x = center.x, z = center.z }, placed = placed.Count, skipped, open, gap,
                gapDirection = "north", corridorWidth = 2 * GapHalfWidth + 1, corridorLength = Half - BandInner + 1 };
        }

        // Stock also provisions the colonists for the multi-hour build: fed and
        // rested now, with pemmican within reach, so a starvation mental break
        // does not hold the clock (and the layout) before the raid is staged.
        private static object Stock(Map map, IntVec3 center)
        {
            var wood = ThingDefOf.WoodLog; var spawned = 0;
            for (var left = 1200; left > 0;) {
                var thing = ThingMaker.MakeThing(wood); thing.stackCount = Math.Min(wood.stackLimit, left); left -= thing.stackCount;
                if (!GenPlace.TryPlaceThing(thing, center, map, ThingPlaceMode.Near)) return Refuse("Fixture wood placement failed.");
                thing.SetForbidden(false, false); spawned += thing.stackCount;
            }
            var pemmican = DefDatabase<ThingDef>.GetNamed("Pemmican"); var food = 0;
            for (var left = 300; left > 0;) {
                var thing = ThingMaker.MakeThing(pemmican); thing.stackCount = Math.Min(pemmican.stackLimit, left); left -= thing.stackCount;
                if (!GenPlace.TryPlaceThing(thing, center, map, ThingPlaceMode.Near)) return Refuse("Fixture food placement failed.");
                thing.SetForbidden(false, false); food += thing.stackCount;
            }
            Quiet();
            var provisioned = 0;
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.Where(p => p.needs != null)) {
                if (pawn.needs.food != null) pawn.needs.food.CurLevelPercentage = 1f;
                if (pawn.needs.rest != null) pawn.needs.rest.CurLevelPercentage = 1f;
                provisioned++;
            }
            return new { success = true, spawned, resource = wood.defName, food, provisioned };
        }

        private static object Ranged(Map map, List<Pawn> colonists, int rifles)
        {
            var def = DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"); var armed = new List<object>();
            foreach (var pawn in colonists.Where(p => p.equipment != null && !p.WorkTagIsDisabled(WorkTags.Violent)).Take(Math.Max(0, Math.Min(rifles, 8)))) {
                var prior = pawn.equipment.Primary;
                if (prior != null && !pawn.equipment.TryDropEquipment(prior, out _, pawn.Position, false)) return Refuse("Existing equipment could not be dropped.");
                var weapon = (ThingWithComps)ThingMaker.MakeThing(def);
                pawn.equipment.AddEquipment(weapon);
                if (pawn.equipment.Primary != weapon) return Refuse("Rifle was not assigned.");
                armed.Add(new { pawn = pawn.GetUniqueLoadID(), weapon = weapon.GetUniqueLoadID(), range = def.Verbs[0].range });
            }
            return new { success = armed.Count > 0, armed };
        }

        private static object Raid(Map map, string strategy, string arrival, int points)
        {
            var def = DefDatabase<IncidentDef>.GetNamed("RaidEnemy");
            var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            var faction = Find.FactionManager.AllFactionsVisible.Where(f => f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction
                    && ((IncidentWorker_RaidEnemy)def.Worker).FactionCanBeGroupSource(f, parms))
                .OrderBy(f => f.def.techLevel).ThenBy(f => f.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat)).FirstOrDefault();
            if (faction == null) return Refuse("No currently eligible hostile humanlike faction.");
            parms.faction = faction;
            parms.points = Math.Max(points, Math.Max(def.minThreatPoints, faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat)));
            parms.raidStrategy = DefDatabase<RaidStrategyDef>.GetNamed(strategy);
            parms.raidArrivalMode = DefDatabase<PawnsArrivalModeDef>.GetNamed(arrival);
            parms.forced = true;
            var before = new HashSet<string>(map.mapPawns.AllPawnsSpawned.Select(p => p.GetUniqueLoadID()));
            var applied = def.Worker.TryExecute(parms);
            var added = map.mapPawns.AllPawnsSpawned.Where(p => !before.Contains(p.GetUniqueLoadID()))
                .Select(p => new { id = p.GetUniqueLoadID(), kind = p.kindDef.defName, hostile = p.HostileTo(Faction.OfPlayer), x = p.Position.x, z = p.Position.z,
                    lordJob = p.GetLord()?.LordJob?.GetType().Name, lordToil = p.GetLord()?.CurLordToil?.GetType().Name }).ToList();
            return new { success = applied && added.Count > 0, applied, faction = faction.def.defName, points = parms.points, strategy, arrival, added, tick = Find.TickManager.TicksGame };
        }

        private static object Damage(Map map, string wall)
        {
            var target = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.GetUniqueLoadID() == wall);
            if (target == null) return Refuse("Wall id not found among player buildings.");
            var before = target.HitPoints;
            target.TakeDamage(new DamageInfo(DamageDefOf.Blunt, Math.Max(1, target.MaxHitPoints / 2)));
            return new { success = !target.Destroyed && target.HitPoints < before, before, after = target.HitPoints, max = target.MaxHitPoints };
        }

        // The scenario stages its own raid; a storyteller incident or quest
        // letter meanwhile pauses the game and cancels the layout plans. The
        // comps are rebuilt from the storyteller def on every load, so a run
        // resumed from a checkpoint save calls this op again.
        private static object Quiet()
        {
            Find.Storyteller.storytellerComps.Clear();
            Find.Storyteller.incidentQueue.Clear();
            return new { success = true };
        }

        private static object Inspect(Map map, Faction player)
        {
            var traps = map.listerBuildings.allBuildingsColonist.Where(b => b.def.defName == "TrapSpike").ToList();
            var trapCells = new HashSet<IntVec3>(traps.Select(t => t.Position));
            var colonistsOnTraps = map.mapPawns.FreeColonistsSpawned.Where(p => trapCells.Contains(p.Position)).Select(p => p.GetUniqueLoadID()).ToList();
            var sprung = traps.Count(t => !Armed(t));
            var hostiles = map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(player) && p.RaceProps.Humanlike)
                .Select(p => new { id = p.GetUniqueLoadID(), dead = p.Dead, downed = p.Downed, x = p.Position.x, z = p.Position.z,
                    lordJob = p.GetLord()?.LordJob?.GetType().Name, lordToil = p.GetLord()?.CurLordToil?.GetType().Name }).ToList();
            var walls = map.listerBuildings.allBuildingsColonist.Where(b => b.def == ThingDefOf.Wall)
                .Select(b => new { id = b.GetUniqueLoadID(), x = b.Position.x, z = b.Position.z, hp = b.HitPoints, max = b.MaxHitPoints }).ToList();
            var colonists = map.mapPawns.FreeColonistsSpawned
                .Select(p => new { id = p.GetUniqueLoadID(), x = p.Position.x, z = p.Position.z, drafted = p.Drafted, dead = p.Dead, downed = p.Downed }).ToList();
            return new { success = true, traps = traps.Count, sprung, colonistsOnTraps, colonists, hostiles, walls, tick = Find.TickManager.TicksGame, paused = Find.TickManager.Paused };
        }

        // Building_TrapRearmable keeps its armed state private; a trap whose
        // state cannot be read counts as armed so sprung is never inflated.
        private static bool Armed(Building trap)
        {
            var field = trap.GetType().GetField("armedInt", BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null || (bool)field.GetValue(trap);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
