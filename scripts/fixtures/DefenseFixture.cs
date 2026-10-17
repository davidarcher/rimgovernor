using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Private disposable defense acceptance fixture. It stages supplies, equipment, raids and
    // trap breaches, and reads independent native evidence. Breach disables automatic
    // rebuilding, so recovery proves governor-issued work. Construction, movement and combat
    // remain native.
    public sealed class DefenseFixture
    {
        [Tool("test/defense_setup", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable defensive-layout fixture: op=stock|ranged|raid|breach|heal|inspect|instant-shells|power|muster|ieds|armory.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken, string op, string strategy = "ImmediateAttack", string arrival = "EdgeWalkIn", int points = 0, int rifles = 3, int x = -1, int z = -1, string cells = "", string side = "")
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
                    case "stock": return Stock(map, center);
                    case "ranged": return Ranged(map, colonists, rifles);
                    case "raid": return Raid(map, strategy, arrival, points, x < 0 ? IntVec3.Invalid : new IntVec3(x, 0, z), side);
                    case "breach": return Breach(map);
                    case "heal": return Heal(map);
                    case "inspect": return Inspect(map, player);
                    case "instant-shells": LayoutGridFixture.ArmInstantShells(); return new { success = true, armed = true };
                    case "power": return Power(map, new IntVec3(x, 0, z));
                    case "muster": return Muster(map, colonists, cells);
                    case "ieds": return IedGates(map, new IntVec3(x, 0, z));
                    case "armory": return Armory(map, new IntVec3(x, 0, z), points);
                    default: return Refuse("Use stock, ranged, raid, breach, heal, inspect, instant-shells, power, muster, ieds or armory.");
                }
            }, cancellationToken).ConfigureAwait(false);
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
        // then generates no pawns and refuses. The seed pins the
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

        // OnSide keeps an edge cell on the named map side (north, east,
        // south, west); an empty side keeps every edge cell.
        private static bool OnSide(Map map, IntVec3 c, string side)
        {
            switch (side)
            {
                case "": return true;
                case "north": return c.z == map.Size.z - 1;
                case "south": return c.z == 0;
                case "east": return c.x == map.Size.x - 1;
                case "west": return c.x == 0;
                default: return false;
            }
        }

        // Raid stages the game's own raid incident. With a `near` cell a
        // walk-in arrival starts at the standable map-edge cell closest to it
        // that reaches it (the raid's own worker otherwise picks any edge,
        // and a raid that walks in behind the colony never meets the
        // corridor the turrets cover); a drop arrival lands its pods on
        // the near cell itself. A side restricts the walk-in edge to
        // that map side: the nearest edge to the corridor entry can be a
        // flank one cell closer than the edge the corridor faces.
        // The faction is the lowest-tech
        // hostile humanlike faction the chosen strategy and arrival accept
        // natively (a siege needs canSiege, a centre drop an industrial
        // faction: pirates, not the tribe), at the strategy's point floor.
        // Pod pawns are not spawned until the pods open, so a drop arrival
        // reports the pawns inside the incoming pods as added.
        private static object Raid(Map map, string strategy, string arrival, int points, IntVec3 near, string side = "")
        {
            var def = DefDatabase<IncidentDef>.GetNamed("RaidEnemy");
            var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            parms.raidStrategy = DefDatabase<RaidStrategyDef>.GetNamed(strategy);
            parms.raidArrivalMode = DefDatabase<PawnsArrivalModeDef>.GetNamed(arrival);
            var drop = parms.raidArrivalMode.defName.Contains("Drop");
            if (near.IsValid)
            {
                if (!near.InBounds(map)) return Refuse("near cell is off the map.");
                if (drop) parms.spawnCenter = near;
                else
                {
                    var edge = CellRect.WholeMap(map).EdgeCells.Where(c => OnSide(map, c, side) && c.Standable(map) && !c.Fogged(map)
                            && map.reachability.CanReach(c, near, Verse.AI.PathEndMode.OnCell, TraverseMode.PassDoors, Danger.Deadly))
                        .OrderBy(c => c.DistanceToSquared(near)).FirstOrDefault();
                    if (!edge.IsValid) return Refuse("No standable map-edge cell reaches the near cell.");
                    parms.spawnCenter = edge;
                }
            }
            var candidates = Find.FactionManager.AllFactionsVisible.Where(f => f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction
                    && ((IncidentWorker_RaidEnemy)def.Worker).FactionCanBeGroupSource(f, parms))
                .OrderBy(f => f.def.techLevel).ThenBy(f => f.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat)).ToList();
            var refused = new List<object>();
            Faction faction = null;
            foreach (var candidate in candidates)
            {
                parms.faction = candidate;
                parms.points = Math.Max(points, StrategyPointFloor(def, parms.raidStrategy, candidate));
                var strategyOk = parms.raidStrategy.Worker.CanUseWith(parms, PawnGroupKindDefOf.Combat);
                var arrivalOk = parms.raidArrivalMode.Worker.CanUseWith(parms);
                if (strategyOk && arrivalOk) { faction = candidate; break; }
                refused.Add(new { faction = candidate.def.defName, points = parms.points, strategyOk, arrivalOk });
            }
            if (faction == null) return new { success = false, reason = "No currently eligible hostile humanlike faction for " + strategy + "/" + arrival + ".", refused };
            parms.forced = true;
            parms.pawnGroupMakerSeed = RequiredKindGroupMakerSeed(parms);
            var before = new HashSet<string>(map.mapPawns.AllPawnsSpawned.Select(p => p.GetUniqueLoadID()));
            var podsBefore = new HashSet<int>(map.listerThings.AllThings.OfType<DropPodIncoming>().Select(t => t.thingIDNumber));
            var applied = def.Worker.TryExecute(parms);
            var arrived = map.mapPawns.AllPawnsSpawned.Where(p => !before.Contains(p.GetUniqueLoadID())).Select(p => new { pawn = p, inPod = false }).ToList();
            var pods = map.listerThings.AllThings.OfType<DropPodIncoming>().Where(t => !podsBefore.Contains(t.thingIDNumber)).ToList();
            foreach (var pod in pods)
                foreach (var pawn in pod.Contents.innerContainer.OfType<Pawn>()) arrived.Add(new { pawn, inPod = true });
            var added = arrived.Select(a => new { id = a.pawn.GetUniqueLoadID(), kind = a.pawn.kindDef.defName, hostile = a.pawn.HostileTo(Faction.OfPlayer), x = a.pawn.Position.x, z = a.pawn.Position.z,
                    lordJob = a.pawn.GetLord()?.LordJob?.GetType().Name, lordToil = a.pawn.GetLord()?.CurLordToil?.GetType().Name, a.inPod }).ToList();
            return new { success = applied && added.Count > 0, applied, faction = faction.def.defName, points = parms.points, strategy, arrival, groupMakerSeed = parms.pawnGroupMakerSeed,
                spawnCenter = new { x = parms.spawnCenter.x, z = parms.spawnCenter.z }, pods = pods.Select(p => new { x = p.Position.x, z = p.Position.z, openDelay = p.Contents.openDelay }).ToList(),
                added, tick = Find.TickManager.TicksGame };
        }

        // Breach stages the repair precondition so the layout planner's own
        // rebuild is what restores the trap corridor: the game's
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
        // quiet marker (QuietStoryteller) is re-applied rather than the comps
        // merely cleared: it is what keeps the storyteller tick and the pawns'
        // inspiration rolls off in this process after a reload.
        private static void Quiet() => QuietStoryteller.ApplyDifficulty(Find.Storyteller);

        // Power stages the turret tier's observed gates without placing
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

        // IedGates stages the IED tier's observed gates without
        // placing any IED: the IEDs research and its prerequisites are
        // finished natively and high-explosive shells for the traps (two a
        // trap, spares for the rebuild) are dropped near x,z. Where the IEDs
        // go stays the layout planner's.
        private static object IedGates(Map map, IntVec3 near)
        {
            if (!near.InBounds(map)) return Refuse("x and z must name a map cell for the shells.");
            var finished = FinishResearch("IEDs");
            if (finished == null) return Refuse("Research project IEDs not found.");
            if (!DropStock(map, near, DefDatabase<ThingDef>.GetNamed("Shell_HighExplosive"), 24)) return Refuse("Shell placement failed.");
            return new { success = true, finished, shells = 24, tick = Find.TickManager.TicksGame };
        }

        // Armory stages a threat-tier rise: the storyteller's
        // difficulty threat scale steps up until DefaultThreatPointsNow
        // reaches points, Smithing, Machining and HeavyTurrets are finished,
        // a fuelled smithy stands on a reachable cell near x,z with steel,
        // plasteel and components beside it, and the best builder is raised
        // to the autocannon's construction skill. Which turret is replaced
        // and what the armory crafts stays the controller's.
        private static object Armory(Map map, IntVec3 near, int points)
        {
            if (!near.InBounds(map)) return Refuse("x and z must name a map cell for the smithy.");
            if (points <= 0) return Refuse("points must name the raid points to reach.");
            var finished = new List<string>();
            foreach (var name in new[] { "Smithing", "Machining", "HeavyTurrets" })
            {
                var done = FinishResearch(name);
                if (done == null) return Refuse("Research project " + name + " not found.");
                finished.AddRange(done);
            }
            var difficulty = Find.Storyteller.difficulty;
            var before = StorytellerUtility.DefaultThreatPointsNow(map);
            for (var scale = Math.Max(1f, difficulty.threatScale); scale <= 50f && StorytellerUtility.DefaultThreatPointsNow(map) < points; scale += 0.5f)
                difficulty.threatScale = scale;
            if (StorytellerUtility.DefaultThreatPointsNow(map) < points) return Refuse("Threat scale 50 does not reach " + points + " raid points.");
            var smithyDef = DefDatabase<ThingDef>.GetNamed("FueledSmithy");
            IntVec3 anchor = IntVec3.Invalid;
            foreach (var cell in GenRadial.RadialCellsAround(near, 10, true).Where(c => c.InBounds(map)))
            {
                if (!GenConstruct.CanPlaceBlueprintAt(smithyDef, cell, Rot4.North, map).Accepted) continue;
                if (!map.mapPawns.FreeColonistsSpawned.Any(p => map.reachability.CanReach(p.Position, cell, Verse.AI.PathEndMode.Touch, TraverseMode.PassDoors, Danger.Deadly))) continue;
                anchor = cell; break;
            }
            if (!anchor.IsValid) return Refuse("No placeable smithy anchor within 10 cells of the requested cell.");
            var smithy = ThingMaker.MakeThing(smithyDef, GenStuff.DefaultStuffFor(smithyDef));
            smithy.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(smithy, anchor, map, Rot4.North);
            var fuel = smithy.TryGetComp<CompRefuelable>();
            fuel?.Refuel(fuel.Props.fuelCapacity);
            if (!DropStock(map, near, ThingDefOf.Steel, 1200) || !DropStock(map, near, ThingDefOf.Plasteel, 200) || !DropStock(map, near, ThingDefOf.ComponentIndustrial, 30))
                return Refuse("Armory stock placement failed.");
            var autocannon = DefDatabase<ThingDef>.GetNamed("Turret_Autocannon");
            var builder = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && p.skills != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Construction).Level).FirstOrDefault();
            if (builder == null) return Refuse("No colonist can construct.");
            var construction = builder.skills.GetSkill(SkillDefOf.Construction);
            if (construction.Level < autocannon.constructionSkillPrerequisite) construction.Level = autocannon.constructionSkillPrerequisite;
            return new { success = true, finished, raidPointsBefore = before, threatScale = difficulty.threatScale,
                smithy = new { id = smithy.GetUniqueLoadID(), x = anchor.x, z = anchor.z, fuel = fuel?.Fuel ?? -1f },
                builder = new { id = builder.GetUniqueLoadID(), construction = construction.Level }, tick = Find.TickManager.TicksGame };
        }

        // FinishResearch finishes a project and its prerequisites without a
        // letter, returning the ones it finished; null for an unknown name.
        private static List<string> FinishResearch(string name)
        {
            var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);
            if (project == null) return null;
            var finished = new List<string>();
            foreach (var prerequisite in Prerequisites(project))
            {
                if (prerequisite.IsFinished) continue;
                Find.ResearchManager.FinishProject(prerequisite, doCompletionDialog: false, researcher: null, doCompletionLetter: false);
                finished.Add(prerequisite.defName);
            }
            return finished;
        }

        // DropStock drops count of def, unforbidden, near a cell.
        private static bool DropStock(Map map, IntVec3 near, ThingDef def, int count)
        {
            for (var left = count; left > 0;)
            {
                var thing = ThingMaker.MakeThing(def); thing.stackCount = Math.Min(def.stackLimit, left); left -= thing.stackCount;
                if (!GenPlace.TryPlaceThing(thing, near, map, ThingPlaceMode.Near)) return false;
                thing.SetForbidden(false, false);
            }
            return true;
        }

        // Ieds are the colony's IED traps: a sprung IED explodes and
        // is gone, so a later inspect tells a sprung one by its missing id.
        private static List<object> Ieds(Map map) =>
            map.listerBuildings.allBuildingsColonist.Where(b => b.def.defName.StartsWith("TrapIED_")).OrderBy(b => b.thingIDNumber)
                .Select(b => (object)new { id = b.GetUniqueLoadID(), def = b.def.defName, x = b.Position.x, z = b.Position.z }).ToList();

        private static IEnumerable<ResearchProjectDef> Prerequisites(ResearchProjectDef project)
        {
            foreach (var p in project.prerequisites ?? new List<ResearchProjectDef>())
                foreach (var q in Prerequisites(p)) yield return q;
            yield return project;
        }

        // Turrets reads every player turret gun with the evidence the
        // acceptance needs: power, hit points, the tick the turret last took
        // aim and its ranged-fire entries in the battle log (each burst the
        // turret starts is logged with the turret as the initiator), and
        // the barrel's fuel and auto-refuel setting.
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
        // and moves had got them behind the firing line, so a
        // from-checkpoint raid musters them there first.
        private static object Muster(Map map, List<Pawn> colonists, string cells)
        {
            var targets = FixtureKit.Cells(cells);
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
                .Select(p => new { id = p.GetUniqueLoadID(), x = p.Position.x, z = p.Position.z, drafted = p.Drafted, dead = p.Dead, downed = p.Downed, primary = p.equipment?.Primary?.def.defName }).ToList();
            // Trap ids let a later inspect tell a destroyed trap (its id is
            // gone) from its replacement (a new id on the same cell).
            var trapIds = traps.Select(t => t.GetUniqueLoadID()).OrderBy(id => id).ToList();
            var cells = traps.OrderBy(t => t.thingIDNumber).Select(t => new { x = t.Position.x, z = t.Position.z }).ToList();
            var conduits = map.listerBuildings.allBuildingsColonist.Where(b => b.def.defName == "PowerConduit" || b.def.defName == "HiddenConduit").OrderBy(b => b.thingIDNumber)
                .Select(b => new { x = b.Position.x, z = b.Position.z }).ToList();
            var generators = map.listerBuildings.allBuildingsColonist.Where(b => b.TryGetComp<CompPowerPlant>() != null).OrderBy(b => b.thingIDNumber)
                .Select(b => new { id = b.GetUniqueLoadID(), def = b.def.defName, x = b.Position.x, z = b.Position.z, output = b.TryGetComp<CompPowerPlant>().PowerOutput,
                    fuel = b.TryGetComp<CompRefuelable>()?.Fuel ?? -1f }).ToList();
            return new { success = true, traps = traps.Count, trapIds, trapCells = cells, sprung, colonistsOnTraps, colonists, hostiles, walls,
                ieds = Ieds(map), turrets = Turrets(map), conduits, generators, tick = Find.TickManager.TicksGame, paused = Find.TickManager.Paused };
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
