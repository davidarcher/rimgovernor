using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (issue #5 M4). Ops: terrain (a rock
    // band around the colony with one straight corridor gap), stock (wood),
    // ranged (rifles for existing colonists), raid (a real RaidEnemy incident
    // with the chosen strategy/arrival), predator (a wild predator spawned
    // inside the band already hunting a colonist, #157), damage (one wall
    // hit), breach (the game's auto-rebuild off, one trap gone, #117),
    // inspect (colonists on trap cells, trap ids and cells, sprung traps,
    // hostiles, the fixture predator, turrets with their power and last
    // attack tick, conduits, generators, the wealth split and raid points,
    // #395), power (#61: turret and electricity
    // research finished, a fuelled wood generator with a conduit stub near
    // x,z, steel and components in stock), depower (one conduit at x,z
    // vanishes with the game's auto-rebuild off), empty (#205: the turret at
    // x,z has its barrel emptied and its auto-refuel switched off, so any
    // later fuel in it came from a rearm order), hostile (#246: one insect
    // hive or crashed ship part of def kind spawned in the open near a
    // colonist under its native hostile faction, its pawn and child-hive
    // spawning switched off so the building itself is the only threat),
    // wealth (#395: the wealth watcher recounted now, so stock just placed
    // counts, then the same wealth split and raid points inspect reads). No
    // completed-work injection: construction, movement and combat stay
    // native.
    public sealed class DefenseFixture
    {
        private const int Half = 22;
        private const int BandInner = 13;
        private const int GapHalfWidth = 1;

        private static Pawn fixturePredator;
        // The world the predator was staged in. A kept process reloads the
        // checkpoint between runs, and a pawn from the unloaded world still
        // answers Spawned/Map through its old map index, so the predator is
        // only the fixture's while its world is the current one.
        private static World fixturePredatorWorld;
        private static Pawn CurrentPredator()
        {
            if (fixturePredator == null || fixturePredatorWorld != Find.World) { fixturePredator = null; fixturePredatorWorld = null; }
            return fixturePredator;
        }
        // The hostile building the fixture spawned (#246), guarded by its
        // world the same way; a destroyed thing keeps answering Destroyed,
        // so it is remembered, not cleared, once it is gone.
        private static Thing fixtureHostile;
        private static World fixtureHostileWorld;
        private static Thing CurrentHostile()
        {
            if (fixtureHostile == null || fixtureHostileWorld != Find.World) { fixtureHostile = null; fixtureHostileWorld = null; }
            return fixtureHostile;
        }

        [Tool("test/defense_setup", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable defensive-layout fixture: op=terrain|stock|ranged|raid|predator|damage|breach|heal|inspect|quiet|power|depower|muster|empty|hostile|wealth.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken, string op, string strategy = "ImmediateAttack", string arrival = "EdgeWalkIn", int points = 0, string wall = "", int rifles = 3, string kind = "Cougar", int x = -1, int z = -1, string cells = "")
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
                    case "raid": return Raid(map, strategy, arrival, points, x < 0 ? IntVec3.Invalid : new IntVec3(x, 0, z));
                    case "predator": return Predator(map, colonists, kind);
                    case "damage": return Damage(map, wall);
                    case "breach": return Breach(map);
                    case "heal": return Heal(map);
                    case "inspect": return Inspect(map, player);
                    case "quiet": return Quiet();
                    case "power": return Power(map, new IntVec3(x, 0, z));
                    case "depower": return Depower(map, new IntVec3(x, 0, z));
                    case "muster": return Muster(map, colonists, cells);
                    case "empty": return Empty(map, new IntVec3(x, 0, z));
                    case "hostile": return Hostile(map, colonists, kind);
                    case "wealth": map.wealthWatcher.ForceRecount(); return new { success = true, threat = Threat(map), tick = Find.TickManager.TicksGame };
                    default: return Refuse("Use terrain, stock, ranged, raid, predator, damage, breach, heal, inspect, quiet, power, depower, muster or empty.");
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
                    // A non-destroyable feature (a steam geyser) cannot be
                    // cleared; destroying it only logs an error RimBridge
                    // raises as a blocking attention (#316). Its cell stays.
                    if (cell.GetThingList(map).Any(t => !t.def.destroyable)) { skipped++; continue; }
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

        // The storyteller only offers a strategy where its selection curve is
        // positive, and the strategy's pawn generation assumes that floor:
        // sappers below 700 points generate nothing and the incident refuses.
        private static float StrategyPointFloor(IncidentDef def, RaidStrategyDef strategy, Faction faction)
        {
            var floor = Math.Max(def.minThreatPoints, faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat));
            floor = Math.Max(floor, strategy.Worker.MinimumPoints(faction, PawnGroupKindDefOf.Combat));
            CurvePoint? previous = null;
            foreach (var point in strategy.selectionWeightPerPointsCurve)
            {
                if (point.y > 0f) return Math.Max(floor, previous.HasValue ? previous.Value.x + 1f : point.x);
                previous = point;
            }
            return floor;
        }

        // A strategy with a required pawn kind (sappers need canBeSapper) must
        // draw a combat group maker that carries one; the tribal faction's
        // ranged-only maker (commonality 60 of 225) has none, and the raid
        // then generates no pawns and refuses (#347). The seed pins the
        // maker draw to one the strategy can use.
        private static int? RequiredKindGroupMakerSeed(IncidentParms parms)
        {
            if (!(parms.raidStrategy.Worker is RaidStrategyWorker_WithRequiredPawnKinds worker)) return null;
            var probe = IncidentParmsUtility.GetDefaultPawnGroupMakerParms(PawnGroupKindDefOf.Combat, parms);
            probe.points = IncidentWorker_Raid.AdjustedRaidPoints(parms.points, parms.raidArrivalMode, parms.raidStrategy, parms.faction, PawnGroupKindDefOf.Combat, parms.target);
            for (var seed = 0; seed < 64; seed++)
            {
                probe.seed = seed;
                if (PawnGroupMakerUtility.TryGetRandomPawnGroupMaker(probe, out var maker) && worker.CanUseWithGroupMaker(maker)) return seed;
            }
            return null;
        }

        // Raid stages the game's own raid incident. With a `near` cell the
        // edge arrival starts at the standable map-edge cell closest to it
        // that reaches it (the raid's own worker otherwise picks any edge,
        // and a raid that walks in behind the colony never meets the
        // corridor the #61 turrets cover).
        private static object Raid(Map map, string strategy, string arrival, int points, IntVec3 near)
        {
            var def = DefDatabase<IncidentDef>.GetNamed("RaidEnemy");
            var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            if (near.IsValid)
            {
                if (!near.InBounds(map)) return Refuse("near cell is off the map.");
                var edge = CellRect.WholeMap(map).EdgeCells.Where(c => c.Standable(map) && !c.Fogged(map)
                        && map.reachability.CanReach(c, near, Verse.AI.PathEndMode.OnCell, TraverseMode.PassDoors, Danger.Deadly))
                    .OrderBy(c => c.DistanceToSquared(near)).FirstOrDefault();
                if (!edge.IsValid) return Refuse("No standable map-edge cell reaches the near cell.");
                parms.spawnCenter = edge;
            }
            var faction = Find.FactionManager.AllFactionsVisible.Where(f => f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction
                    && ((IncidentWorker_RaidEnemy)def.Worker).FactionCanBeGroupSource(f, parms))
                .OrderBy(f => f.def.techLevel).ThenBy(f => f.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat)).FirstOrDefault();
            if (faction == null) return Refuse("No currently eligible hostile humanlike faction.");
            parms.faction = faction;
            parms.raidStrategy = DefDatabase<RaidStrategyDef>.GetNamed(strategy);
            parms.points = Math.Max(points, StrategyPointFloor(def, parms.raidStrategy, faction));
            parms.raidArrivalMode = DefDatabase<PawnsArrivalModeDef>.GetNamed(arrival);
            parms.forced = true;
            parms.pawnGroupMakerSeed = RequiredKindGroupMakerSeed(parms);
            var before = new HashSet<string>(map.mapPawns.AllPawnsSpawned.Select(p => p.GetUniqueLoadID()));
            var applied = def.Worker.TryExecute(parms);
            var added = map.mapPawns.AllPawnsSpawned.Where(p => !before.Contains(p.GetUniqueLoadID()))
                .Select(p => new { id = p.GetUniqueLoadID(), kind = p.kindDef.defName, hostile = p.HostileTo(Faction.OfPlayer), x = p.Position.x, z = p.Position.z,
                    lordJob = p.GetLord()?.LordJob?.GetType().Name, lordToil = p.GetLord()?.CurLordToil?.GetType().Name }).ToList();
            return new { success = applied && added.Count > 0, applied, faction = faction.def.defName, points = parms.points, strategy, arrival, groupMakerSeed = parms.pawnGroupMakerSeed, added, tick = Find.TickManager.TicksGame };
        }

        // Predator stages the #157 precondition directly: a wild predator of
        // the given kind spawned inside the band, a few cells from the first
        // colonist, already running the game's own PredatorHunt job on that
        // colonist (the job a hungry cougar starts by itself; RimWorld's
        // JobDriver_PredatorHunt does the rest). The native census then lists
        // it under huntingPredators, the clock stops with predator_hunt and
        // the emergency holds it as an unsafe threat: exactly the state
        // productionaccept parked in. The predator is starving so the driver
        // has no reason to abandon the hunt on its own.
        private static object Predator(Map map, List<Pawn> colonists, string kind)
        {
            var staged = CurrentPredator();
            if (staged != null && staged.Spawned && !staged.Dead && staged.Map == map)
                return Refuse("Fixture predator already on the map; inspect it before staging another.");
            var kindDef = DefDatabase<PawnKindDef>.GetNamedSilentFail(kind);
            if (kindDef == null || !kindDef.RaceProps.predator) return Refuse("kind must name a predator PawnKindDef.");
            var prey = colonists.FirstOrDefault(p => !p.Dead && !p.Downed);
            if (prey == null) return Refuse("No standing colonist to hunt.");
            var cell = GenRadial.RadialCellsAround(prey.Position, 12, true).FirstOrDefault(c => c.InBounds(map)
                && c.Standable(map) && !c.Fogged(map) && c.DistanceTo(prey.Position) >= 8
                && map.reachability.CanReach(c, prey.Position, Verse.AI.PathEndMode.Touch, TraverseMode.NoPassClosedDoors, Danger.Deadly));
            if (!cell.IsValid) return Refuse("No standable cell 8-12 cells from the prey that reaches it.");
            var predator = PawnGenerator.GeneratePawn(kindDef, null);
            GenSpawn.Spawn(predator, cell, map);
            if (predator.needs?.food != null) predator.needs.food.CurLevelPercentage = 0.02f;
            var job = JobMaker.MakeJob(JobDefOf.PredatorHunt, prey);
            job.killIncappedTarget = true;
            predator.jobs.StartJob(job, Verse.AI.JobCondition.InterruptForced);
            var hunting = predator.CurJobDef == JobDefOf.PredatorHunt && predator.CurJob?.GetTarget(Verse.AI.TargetIndex.A).Thing == prey;
            if (!hunting) { predator.Destroy(DestroyMode.Vanish); return Refuse("The predator did not take the PredatorHunt job."); }
            fixturePredator = predator; fixturePredatorWorld = Find.World;
            return new { success = true, predator = predator.GetUniqueLoadID(), kind = kindDef.defName, bodySize = predator.BodySize,
                prey = prey.GetUniqueLoadID(), x = cell.x, z = cell.z, distance = cell.DistanceTo(prey.Position),
                job = predator.CurJobDef.defName, faction = predator.Faction?.def.defName, tick = Find.TickManager.TicksGame };
        }

        // Hostile stages the #246 threat: one hostile building (an insect
        // hive, or a crashed ship part) in open ground 8-14 cells from a
        // standing colonist, with two cells of clearance on every side so
        // it never sits in the corridor gap or against the hut. The hive's
        // own pawn and child-hive spawners are switched off before it is
        // spawned, so no insect ever appears and the building is the only
        // threat the census lists; a ship part comes without its incident's
        // mechanoid guards. Destroying it is left to the service.
        private static object Hostile(Map map, List<Pawn> colonists, string kind)
        {
            var staged = CurrentHostile();
            if (staged != null && staged.Spawned && !staged.Destroyed && staged.Map == map)
                return Refuse("Fixture hostile building already on the map; inspect it before staging another.");
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(kind);
            if (def == null || !def.useHitPoints || !(typeof(Hive).IsAssignableFrom(def.thingClass) || def.building != null && def.building.combatPower > 0))
                return Refuse("kind must name Hive or a building ThingDef with combatPower.");
            var isHive = typeof(Hive).IsAssignableFrom(def.thingClass);
            var faction = Find.FactionManager.FirstFactionOfDef(isHive ? FactionDefOf.Insect : FactionDefOf.Mechanoid);
            if (faction == null) return Refuse("The world has no " + (isHive ? "insect" : "mechanoid") + " faction.");
            // The building goes 8-14 cells from a standing colonist, a
            // ranged-armed one first, in that colonist's line of sight: a
            // shooter with a line of fire from where it stands is what the
            // ranged assignment on a building needs (#327).
            var near = colonists.FirstOrDefault(p => !p.Dead && !p.Downed && p.equipment?.Primary?.def.IsRangedWeapon == true)
                ?? colonists.FirstOrDefault(p => !p.Dead && !p.Downed);
            if (near == null) return Refuse("No standing colonist to threaten.");
            var rot = Rot4.North;
            var cell = GenRadial.RadialCellsAround(near.Position, 14, true).FirstOrDefault(c => c.DistanceTo(near.Position) >= 8
                && GenAdj.OccupiedRect(c, rot, def.size).ExpandedBy(2).Cells.All(o => o.InBounds(map) && o.Standable(map) && !o.Fogged(map))
                && GenSight.LineOfSight(near.Position, c, map, true)
                && map.reachability.CanReach(near.Position, c, Verse.AI.PathEndMode.Touch, TraverseMode.NoPassClosedDoors, Danger.Deadly));
            if (!cell.IsValid) return Refuse("No clear cell 8-14 cells from a colonist, in its line of sight, for a " + def.defName + ".");
            var thing = ThingMaker.MakeThing(def);
            thing.SetFactionDirect(faction);
            var spawner = thing.TryGetComp<CompSpawnerPawn>();
            if (spawner != null) spawner.canSpawnPawns = false;
            var hives = thing.TryGetComp<CompSpawnerHives>();
            if (hives != null) hives.canSpawnHives = false;
            // A hive its insects never maintain deteriorates and destroys
            // itself in about 10000 ticks; park its maintenance clock far in
            // the past so only the colonists' damage can bring it down.
            var maintainable = thing.TryGetComp<CompMaintainable>();
            if (maintainable != null) maintainable.ticksSinceMaintain = int.MinValue / 2;
            var insectsBefore = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == faction).Select(p => p.thingIDNumber).ToHashSet();
            GenSpawn.Spawn(thing, cell, map, rot);
            if (!thing.Spawned) return Refuse("The " + def.defName + " did not spawn.");
            // Belt and braces: any pawn the spawn nevertheless produced is
            // vanished so the building stays the only threat.
            var stray = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == faction && !insectsBefore.Contains(p.thingIDNumber)).ToList();
            foreach (var pawn in stray) pawn.Destroy(DestroyMode.Vanish);
            if (spawner != null) spawner.spawnedPawns.Clear();
            var hostile = thing.HostileTo(Faction.OfPlayer);
            if (!hostile) { thing.Destroy(DestroyMode.Vanish); return Refuse("The spawned " + def.defName + " is not hostile to the player."); }
            fixtureHostile = thing; fixtureHostileWorld = Find.World;
            return new { success = true, building = thing.GetUniqueLoadID(), def = def.defName, faction = faction.def.defName, hostile,
                hp = thing.HitPoints, max = thing.MaxHitPoints, x = cell.x, z = cell.z, sizeX = def.size.x, sizeZ = def.size.z,
                distance = cell.DistanceTo(near.Position), near = near.GetUniqueLoadID(), nearRanged = near.equipment?.Primary?.def.IsRangedWeapon == true,
                strayVanished = stray.Count, tick = Find.TickManager.TicksGame };
        }

        private static object Damage(Map map, string wall)
        {
            var target = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.GetUniqueLoadID() == wall);
            if (target == null) return Refuse("Wall id not found among player buildings.");
            var before = target.HitPoints;
            target.TakeDamage(new DamageInfo(DamageDefOf.Blunt, Math.Max(1, target.MaxHitPoints / 2)));
            return new { success = !target.Destroyed && target.HitPoints < before, before, after = target.HitPoints, max = target.MaxHitPoints };
        }

        // Breach stages the repair precondition so the layout planner's own
        // rebuild is what restores the trap corridor (#117): the game's
        // auto-rebuild is switched off (the play setting and every trap's
        // auto-rearm), its pending spike-trap blueprints and frames are
        // removed, and one standing trap (if any survived) vanishes. Every
        // trap missing afterwards can only come back through the planner.
        private static object Breach(Map map)
        {
            Find.PlaySettings.autoRebuild = false;
            var spike = DefDatabase<ThingDef>.GetNamed("TrapSpike");
            var traps = map.listerBuildings.allBuildingsColonist.Where(b => b.def == spike).OrderBy(b => b.thingIDNumber).ToList();
            // autoRearm is private to Building_Trap itself, so it is looked
            // up on that type, not on the spike trap's subclass.
            var autoRearm = typeof(Building_Trap).GetField("autoRearm", BindingFlags.Instance | BindingFlags.NonPublic);
            if (autoRearm == null) return Refuse("Building_Trap.autoRearm not found.");
            var disarmed = 0;
            foreach (var trap in traps) { autoRearm.SetValue(trap, false); disarmed++; }
            var pending = map.listerThings.AllThings.Where(t => t.Faction == Faction.OfPlayer
                && ((t is Blueprint_Build bp && bp.def.entityDefToBuild == spike) || (t is Frame fr && fr.def.entityDefToBuild == spike))).ToList();
            var cleared = pending.Select(t => new { x = t.Position.x, z = t.Position.z, kind = t.def.category.ToString() }).ToList();
            foreach (var thing in pending) thing.Destroy(DestroyMode.Cancel);
            // A raid that sprang every trap leaves nothing to vanish; the
            // corridor is already the planner's to rebuild.
            object breached = null;
            var target = traps.FirstOrDefault();
            if (target != null)
            {
                breached = new { id = target.GetUniqueLoadID(), x = target.Position.x, z = target.Position.z };
                target.Destroy(DestroyMode.Vanish);
                if (!target.Destroyed) return Refuse("Trap was not destroyed.");
            }
            var standing = map.listerBuildings.allBuildingsColonist.Count(b => b.def == spike);
            return new { success = true, breached, disarmed, cleared, standing, autoRebuild = Find.PlaySettings.autoRebuild, tick = Find.TickManager.TicksGame };
        }

        // Heal stages the post-raid precondition for the repair scenario:
        // every colonist's injury is removed so no CriticalMedical hold
        // suspends the layout goal. The controller's own tend order has no
        // native preview yet and the fixture colony has no beds, so the
        // wounded would otherwise wander untended for the whole budget.
        private static object Heal(Map map)
        {
            var healed = new List<object>();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead))
            {
                var injuries = pawn.health.hediffSet.hediffs.Where(h => h is Hediff_Injury || h.TendableNow(true)).ToList();
                foreach (var h in injuries) pawn.health.RemoveHediff(h);
                healed.Add(new { id = pawn.GetUniqueLoadID(), removed = injuries.Count, downed = pawn.Downed, needsTend = pawn.health.HasHediffsNeedingTend() });
            }
            return new { success = true, healed };
        }

        // The scenario stages its own raid; a storyteller incident or quest
        // letter meanwhile pauses the game and cancels the layout plans. The
        // comps are rebuilt from the storyteller def on every load, so a run
        // resumed from a checkpoint save calls this op again.
        // The quiet marker (QuietStoryteller) is re-applied rather than the
        // comps merely cleared: it is what keeps the storyteller tick and the
        // pawns' inspiration rolls off in this process after a reload (#228).
        private static object Quiet()
        {
            QuietStoryteller.ApplyDifficulty(Find.Storyteller);
            return new { success = true, quiet = QuietStoryteller.IsQuiet(Find.Storyteller) };
        }

        // Power stages the turret tier's observed gates (#61) without placing
        // any turret: the research the turret and conduit definitions require
        // is finished natively (no completion letter, so nothing pauses the
        // clock), a fuelled wood-fired generator with a three-cell conduit
        // stub stands on the first placeable anchor near x,z (the case picks
        // a cell well outside connector reach of the firing row, so the
        // planner has to route its own conduit chain), and steel and
        // components for the turrets are dropped by the colonists. Where the
        // turrets go, and whether they are built, powered and fire, stays
        // the controller's and the game's.
        private static object Power(Map map, IntVec3 near)
        {
            if (!near.InBounds(map)) return Refuse("x and z must name a map cell for the generator.");
            var finished = new List<string>();
            foreach (var name in new[] { "Electricity", "GunTurrets" })
            {
                var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);
                if (project == null) return Refuse("Research project " + name + " not found.");
                foreach (var prerequisite in Prerequisites(project))
                {
                    if (prerequisite.IsFinished) continue;
                    Find.ResearchManager.FinishProject(prerequisite, doCompletionDialog: false, researcher: null, doCompletionLetter: false);
                    finished.Add(prerequisite.defName);
                }
            }
            var turret = DefDatabase<ThingDef>.GetNamed("Turret_MiniTurret");
            var conduit = DefDatabase<ThingDef>.GetNamed("PowerConduit");
            var generator = DefDatabase<ThingDef>.GetNamed("WoodFiredGenerator");
            if (!turret.IsResearchFinished || !conduit.IsResearchFinished || !generator.IsResearchFinished)
                return Refuse("Turret, conduit or generator research still unfinished after FinishProject.");
            // Anchor search: the generator's footprint and the stub north of
            // it must all be placeable on a cell the colonists can reach.
            IntVec3 anchor = IntVec3.Invalid; var stub = new List<IntVec3>();
            foreach (var cell in GenRadial.RadialCellsAround(near, 8, true).Where(c => c.InBounds(map)))
            {
                if (!GenConstruct.CanPlaceBlueprintAt(generator, cell, Rot4.North, map).Accepted) continue;
                var rect = GenAdj.OccupiedRect(cell, Rot4.North, generator.size);
                var line = Enumerable.Range(1, 3).Select(i => new IntVec3(cell.x, 0, rect.maxZ + i)).ToList();
                if (line.Any(c => !c.InBounds(map) || rect.Contains(c) || !GenConstruct.CanPlaceBlueprintAt(conduit, c, Rot4.North, map).Accepted || c.GetEdifice(map) != null)) continue;
                if (!map.mapPawns.FreeColonistsSpawned.Any(p => map.reachability.CanReach(p.Position, cell, Verse.AI.PathEndMode.Touch, TraverseMode.PassDoors, Danger.Deadly))) continue;
                anchor = cell; stub = line; break;
            }
            if (!anchor.IsValid) return Refuse("No placeable generator anchor within 8 cells of the requested cell.");
            var gen = ThingMaker.MakeThing(generator);
            gen.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(gen, anchor, map, Rot4.North);
            var refuelable = gen.TryGetComp<CompRefuelable>();
            if (refuelable == null) return Refuse("Generator has no refuelable comp.");
            refuelable.Refuel(refuelable.Props.fuelCapacity);
            var conduits = new List<object>();
            foreach (var cell in stub)
            {
                var wire = ThingMaker.MakeThing(conduit);
                wire.SetFactionDirect(Faction.OfPlayer);
                GenSpawn.Spawn(wire, cell, map, Rot4.North);
                conduits.Add(new { x = cell.x, z = cell.z });
            }
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            var plant = gen.TryGetComp<CompPowerPlant>();
            var spawned = new List<object>();
            foreach (var stock in new[] { new { def = ThingDefOf.Steel, count = 400 }, new { def = ThingDefOf.ComponentIndustrial, count = 20 } })
            {
                var total = 0;
                for (var left = stock.count; left > 0;)
                {
                    var thing = ThingMaker.MakeThing(stock.def); thing.stackCount = Math.Min(stock.def.stackLimit, left); left -= thing.stackCount;
                    if (!GenPlace.TryPlaceThing(thing, near, map, ThingPlaceMode.Near)) return Refuse("Fixture " + stock.def.defName + " placement failed.");
                    thing.SetForbidden(false, false); total += thing.stackCount;
                }
                spawned.Add(new { resource = stock.def.defName, count = total });
            }
            // The mini turret needs Construction 5: the best builder is
            // raised to it so the tier is buildable by someone, as a colony
            // that researched turrets would have.
            var builder = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && p.skills != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Construction).Level).FirstOrDefault();
            if (builder == null) return Refuse("No colonist can construct.");
            var construction = builder.skills.GetSkill(SkillDefOf.Construction);
            var before = construction.Level;
            if (construction.Level < turret.constructionSkillPrerequisite) construction.Level = turret.constructionSkillPrerequisite;
            return new { success = true, finished, builder = new { id = builder.GetUniqueLoadID(), constructionBefore = before, construction = construction.Level },
                generator = new { id = gen.GetUniqueLoadID(), x = anchor.x, z = anchor.z, fuel = refuelable.Fuel,
                output = plant?.PowerOutput ?? 0f, network = plant?.PowerNet != null }, conduits, spawned, tick = Find.TickManager.TicksGame };
        }

        private static IEnumerable<ResearchProjectDef> Prerequisites(ResearchProjectDef project)
        {
            foreach (var p in project.prerequisites ?? new List<ResearchProjectDef>())
                foreach (var q in Prerequisites(p)) yield return q;
            yield return project;
        }

        // Depower stages the upkeep precondition for the turret scenario: the
        // game's auto-rebuild is off and the conduit on x,z vanishes, so any
        // conduit standing there later was placed by the controller's own
        // routine (the layout's missing conduit or a power-family route).
        private static object Depower(Map map, IntVec3 cell)
        {
            Find.PlaySettings.autoRebuild = false;
            var conduit = cell.InBounds(map) ? cell.GetThingList(map).OfType<Building>().FirstOrDefault(b => b.def.defName == "PowerConduit" && b.Faction == Faction.OfPlayer) : null;
            if (conduit == null) return Refuse("No player conduit on the requested cell.");
            conduit.Destroy(DestroyMode.Vanish);
            if (!conduit.Destroyed) return Refuse("Conduit was not destroyed.");
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            return new { success = true, x = cell.x, z = cell.z, turrets = Turrets(map), autoRebuild = Find.PlaySettings.autoRebuild, tick = Find.TickManager.TicksGame };
        }

        // Empty stages the rearm precondition (#205): the turret's barrel is
        // consumed to nothing and the game's own auto-refuel is switched off
        // for it, so the only way fuel returns is a forced refuel order, the
        // controller's rearm.
        private static object Empty(Map map, IntVec3 cell)
        {
            var turret = cell.InBounds(map) ? cell.GetThingList(map).OfType<Building_TurretGun>().FirstOrDefault(t => t.Faction == Faction.OfPlayer) : null;
            if (turret == null) return Refuse("No player turret on the requested cell.");
            var fuel = turret.TryGetComp<CompRefuelable>();
            if (fuel == null) return Refuse("Turret has no refuelable barrel.");
            var before = fuel.Fuel;
            fuel.ConsumeFuel(fuel.Fuel);
            fuel.allowAutoRefuel = false;
            if (fuel.HasFuel) return Refuse("Barrel still has fuel after consumption.");
            return new { success = true, id = turret.GetUniqueLoadID(), x = cell.x, z = cell.z, fuelBefore = before, fuel = fuel.Fuel, targetFuel = fuel.TargetFuelLevel,
                autoRefuel = fuel.allowAutoRefuel, fuelDefs = fuel.Props.fuelFilter.AllowedThingDefs.Select(d => d.defName).ToList(), tick = Find.TickManager.TicksGame };
        }

        // Turrets reads every player turret gun with the evidence the #61
        // acceptance needs: power, hit points, the tick the turret last took
        // aim and its ranged-fire entries in the battle log (each burst the
        // turret starts is logged with the turret as the initiator), and
        // the barrel's fuel and auto-refuel setting (#205).
        private static List<object> Turrets(Map map)
        {
            var shots = Find.BattleLog?.Battles?.SelectMany(b => b.Entries).OfType<BattleLogEntry_RangedFire>().ToList() ?? new List<BattleLogEntry_RangedFire>();
            return map.listerBuildings.allBuildingsColonist.OfType<Building_TurretGun>().OrderBy(t => t.thingIDNumber).Select(t => {
                var power = t.TryGetComp<CompPowerTrader>();
                var net = power?.PowerNet;
                var fuel = t.TryGetComp<CompRefuelable>();
                var fired = shots.Where(s => s.Concerns(t)).Select(s => s.Tick).ToList();
                return (object)new {
                    id = t.GetUniqueLoadID(), def = t.def.defName, x = t.Position.x, z = t.Position.z, hp = t.HitPoints, max = t.MaxHitPoints,
                    powered = power?.PowerOn ?? false, connected = net != null,
                    fuel = fuel?.Fuel ?? -1f, targetFuel = fuel?.TargetFuelLevel ?? -1f, hasFuel = fuel?.HasFuel ?? true, autoRefuel = fuel?.allowAutoRefuel ?? false,
                    parent = power?.connectParent == null ? null : new { x = power.connectParent.parent.Position.x, z = power.connectParent.parent.Position.z, def = power.connectParent.parent.def.defName },
                    netGain = net?.CurrentEnergyGainRate() ?? 0f, netStored = net?.CurrentStoredEnergy() ?? 0f,
                    netTransmitters = net?.transmitters.Count ?? 0, netConnectors = net?.connectors.Count ?? 0,
                    lastAttackTick = t.LastAttackTargetTick, shots = fired.Count, lastShotTick = fired.Count == 0 ? -1 : fired.Max(),
                    targeting = t.CurrentTarget.IsValid,
                    home = map.areaManager.Home[t.Position] };
            }).ToList();
        }

        // Muster stands the colonists on the given cells ("x,z;x,z;...", one
        // per colonist, round-robin when there are more colonists than cells;
        // the nearest standable cell when one is blocked) and ends their
        // jobs. The layout checkpoint was saved with the colonists parked
        // inside the corridor they had been building; a raid staged from it
        // reached them before the hold plan's one-action-per-window drafts
        // and moves had got them behind the firing line (#222), so a
        // from-checkpoint raid musters them there first.
        private static object Muster(Map map, List<Pawn> colonists, string cells)
        {
            var targets = (cells ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(pair => {
                var xz = pair.Split(',');
                return new IntVec3(int.Parse(xz[0].Trim()), 0, int.Parse(xz[1].Trim()));
            }).ToList();
            if (targets.Count == 0) return Refuse("muster needs cells=x,z;x,z;...");
            var moved = new List<object>();
            for (var i = 0; i < colonists.Count; i++)
            {
                var pawn = colonists[i];
                var want = targets[i % targets.Count];
                var at = want;
                object blocked = null;
                // A tree grown onto a firing cell since the checkpoint blocks
                // the hold plan's move; the layout keeps no cell clear.
                if (want.InBounds(map))
                    foreach (var plant in want.GetThingList(map).OfType<Plant>().ToList()) plant.Destroy(DestroyMode.Vanish);
                if (!at.InBounds(map) || !at.Standable(map))
                {
                    blocked = want.InBounds(map)
                        ? new { terrain = want.GetTerrain(map)?.defName, things = want.GetThingList(map).Select(t => t.def.defName).ToList() }
                        : new { terrain = "out of bounds", things = new List<string>() };
                    at = GenRadial.RadialCellsAround(want, 5.9f, true).FirstOrDefault(c => c.InBounds(map) && c.Standable(map));
                }
                if (!at.IsValid) return Refuse("No standable cell near " + want);
                pawn.jobs?.StopAll();
                pawn.Position = at;
                pawn.Notify_Teleported(true, true);
                moved.Add(new { id = pawn.GetUniqueLoadID(), x = at.x, z = at.z, wantX = want.x, wantZ = want.z, blocked });
            }
            return new { success = true, moved };
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
            // The fixture predator's fate: gone (despawned or destroyed),
            // dead, downed, or still on the map with its current job and
            // whether that job is a PredatorHunt on a colonist -- the
            // census's huntingPredators predicate, the threat the service
            // answers; a predator that broke off to eat wildlife is not one.
            object predator = null;
            if (CurrentPredator() != null)
            {
                var p = fixturePredator;
                var prey = p.CurJobDef == JobDefOf.PredatorHunt ? p.CurJob?.GetTarget(Verse.AI.TargetIndex.A).Thing as Pawn : null;
                var huntingColonist = prey != null && prey.Faction == Faction.OfPlayer && prey.RaceProps.Humanlike;
                predator = new { id = p.GetUniqueLoadID(), spawned = p.Spawned && p.Map == map, dead = p.Dead, downed = p.Downed,
                    job = p.CurJobDef?.defName, huntingColonist, x = p.Position.x, z = p.Position.z };
            }
            // The fixture hostile building's fate (#246): destroyed, or still
            // spawned with its hit points; plus every hostile-faction pawn on
            // the map so a run can tell an insect that slipped past the
            // spawner switch from the building itself.
            object hostileBuilding = null;
            if (CurrentHostile() != null)
            {
                var b = fixtureHostile;
                hostileBuilding = new { id = b.GetUniqueLoadID(), def = b.def.defName, destroyed = b.Destroyed, spawned = b.Spawned && b.Map == map,
                    hp = b.Destroyed ? 0 : b.HitPoints, max = b.MaxHitPoints, x = b.Position.x, z = b.Position.z,
                    factionPawns = map.mapPawns.AllPawnsSpawned.Count(p => p.Faction != null && p.Faction == b.Faction) };
            }
            // Trap ids let a later inspect tell a destroyed trap (its id is
            // gone) from its replacement (a new id on the same cell).
            var trapIds = traps.Select(t => t.GetUniqueLoadID()).OrderBy(id => id).ToList();
            var cells = traps.OrderBy(t => t.thingIDNumber).Select(t => new { x = t.Position.x, z = t.Position.z }).ToList();
            var conduits = map.listerBuildings.allBuildingsColonist.Where(b => b.def.defName == "PowerConduit").OrderBy(b => b.thingIDNumber)
                .Select(b => new { x = b.Position.x, z = b.Position.z }).ToList();
            var generators = map.listerBuildings.allBuildingsColonist.Where(b => b.TryGetComp<CompPowerPlant>() != null).OrderBy(b => b.thingIDNumber)
                .Select(b => new { id = b.GetUniqueLoadID(), def = b.def.defName, x = b.Position.x, z = b.Position.z, output = b.TryGetComp<CompPowerPlant>().PowerOutput,
                    fuel = b.TryGetComp<CompRefuelable>()?.Fuel ?? -1f }).ToList();
            return new { success = true, traps = traps.Count, trapIds, trapCells = cells, sprung, colonistsOnTraps, colonists, hostiles, predator, hostileBuilding, walls,
                turrets = Turrets(map), conduits, generators, threat = Threat(map), tick = Find.TickManager.TicksGame, paused = Find.TickManager.Paused };
        }

        // The wealth split and raid points the game itself computes (#395),
        // read the same way the typed colony facts read them, so a case can
        // hold the projected threat section to the native figure on a paused
        // map at one tick.
        private static object Threat(Map map)
        {
            var wealth = map.wealthWatcher;
            return new { wealthItems = wealth.WealthItems, wealthBuildings = wealth.WealthBuildings, wealthPawns = wealth.WealthPawns, wealthTotal = wealth.WealthTotal,
                storytellerWealth = map.PlayerWealthForStoryteller, raidPoints = StorytellerUtility.DefaultThreatPointsNow(map),
                adaptationFactor = Find.StoryWatcher.watcherAdaptation.TotalThreatPointsFactor, difficultyThreatScale = Find.Storyteller.difficulty.threatScale };
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
