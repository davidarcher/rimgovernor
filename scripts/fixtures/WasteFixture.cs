using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable setup only; never exposed through the controller gameplay surface.
    public sealed class WasteFixture
    {
        [Tool("test/waste_fixture", Description = "Prepare disposable waste hauling acceptance inputs.")]
        public async Task<object> Create(IRimBridgeContext ctx, CancellationToken cancellationToken, bool burial = false)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
                // A generous pool of candidate cells, not one fixed cell per
                // item: ThingPlaceMode.Direct placement of a fresh item was
                // observed live to fail outright (TryPlaceThing returns
                // false, the thing left unspawned) on some individual cells
                // for reasons this fixture does not need to fully diagnose
                // (e.g. terrain/roof affordances GetEdifice/Standable do not
                // capture). Each item independently walks the same candidate
                // pool until placement actually succeeds, rather than
                // committing to a single guessed cell.
                //
                // Candidate cells hold no item already: Direct placement
                // absorbs a fixture stack into an existing stack of the same
                // def at the cell (the debug start's own starting Steel, #185),
                // leaving the fixture's Thing destroyed and its Position
                // off-map. The pool is filtered up front and the placement is
                // checked afterwards so the returned item is the one spawned.
                var candidates = GenRadial.RadialCellsAround(pawn.Position, 10, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null
                    && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)).Distinct().ToList();
                if (candidates.Count < 8) throw new System.InvalidOperationException("Not enough empty standable cells near the fixture pawn; reroll the world.");
                Thing PlaceSomewhere(Thing thing)
                {
                    foreach (var cell in candidates)
                    {
                        if (cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)) continue;
                        if (!GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Direct, out var placed)) continue;
                        if (placed != thing || !thing.Spawned || thing.Position != cell)
                            throw new System.InvalidOperationException($"Fixture {thing.def.defName} was absorbed or displaced at {cell} instead of spawning there.");
                        return thing;
                    }
                    throw new System.InvalidOperationException($"Could not place a fixture {thing.def.defName} on any of {candidates.Count} candidate cells.");
                }
                var source = candidates[0];
                var destination = GenRadial.RadialCellsAround(source, 35, true).First(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map) && c.GetZone(map) == null
                    && !map.areaManager.Home[c] && c.DistanceToSquared(source) > 225
                    && c.GetRoom(map)?.UsesOutdoorTemperature == true
                    && !map.listerBuildings.allBuildingsColonist.Any(b => b.Position.DistanceToSquared(c) < 196));
                var kind = burial ? PawnKindDefOf.Colonist : DefDatabase<PawnKindDef>.AllDefsListForReading.First(k => k.race.defName == "Squirrel");
                var animal = PawnGenerator.GeneratePawn(kind, burial ? Faction.OfPlayer : null);
                GenSpawn.Spawn(animal, source, map);
                animal.Kill(null);
                var corpse = animal.Corpse;
                // Pawn.Kill's own corpse placement can silently nudge the
                // corpse to a neighboring cell instead of exactly "source"
                // (observed live); re-place it exactly through the same pool.
                if (corpse.Spawned) corpse.DeSpawn(DestroyMode.Vanish);
                PlaceSomewhere(corpse);
                corpse.TryGetComp<CompRottable>().RotProgress = 200000f;
                corpse.SetForbidden(false, false);
                var protectedItem = (Thing)PlaceSomewhere(ThingMaker.MakeThing(ThingDefOf.Steel));
                protectedItem.SetForbidden(true, false);
                var protectedCell = protectedItem.Position;
                if (burial)
                {
                    var grave = ThingMaker.MakeThing(ThingDefOf.Grave);
                    grave.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(grave, destination, map);
                    ((Building_Grave)grave).GetStoreSettings().filter.SetAllow(corpse.def, true);
                }
                else
                {
                    var zone = new Zone_Stockpile(StorageSettingsPreset.DumpingStockpile, map.zoneManager);
                    map.zoneManager.RegisterZone(zone);
                    zone.AddCell(destination);
                    zone.AddCell(destination + IntVec3.East);
                    zone.GetStoreSettings().filter.SetDisallowAll();
                    zone.GetStoreSettings().filter.SetAllow(corpse.def, true);
                    foreach (var special in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Where(d => d.configurable))
                        zone.GetStoreSettings().filter.SetAllow(special, true);
                    zone.GetStoreSettings().Priority = StoragePriority.Critical;
                    // Native stockpile creation may expand Home; this fixture
                    // deliberately models player-selected dirty storage outside it.
                    foreach (var cell in zone.Cells) map.areaManager.Home[cell] = false;
                }
                // Disable Hauling for every colonist (including the returned
                // pawn), not enable it: the incineration dispatch under
                // test issues its own forced WorkGiver_Scanner job directly
                // (TryTakeOrderedJob), bypassing work settings entirely, so
                // no colonist's own priority needs to be enabled for it. Left
                // enabled, a live run observed the normal think-tree grab
                // this exact unforbidden corpse into the dumping
                // stockpile within the same handful of ticks fixture setup
                // itself consumes, before the acceptance tool ever issues its
                // own dispatch -- disabling it here removes that race.
                foreach (var colonist in map.mapPawns.FreeColonistsSpawned)
                    colonist.workSettings?.SetPriority(WorkTypeDefOf.Hauling, 0);
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return (object)new { success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    pawn = pawn.GetUniqueLoadID(), corpse = corpse.GetUniqueLoadID(),
                    protectedItem = protectedItem.GetUniqueLoadID(),
                    source = BridgeCommon.Pos(source), destination = BridgeCommon.Pos(destination),
                    // The forbidden Steel may land on a different candidate cell
                    // than "source" (see PlaceSomewhere above); report its actual
                    // cell so the acceptance tool scans exactly where it is.
                    protectedCell = BridgeCommon.Pos(protectedCell) };
            }, cancellationToken);

        // Burial ranking acceptance (#2196): a morgue zone at MorguePriority
        // holding a fresh colonist corpse and a fresh stranger corpse, one free
        // grave a few cells off, and a hauling colonist. Vanilla hauling must
        // carry the colonist into the grave (a higher storage priority than the
        // morgue) and leave the stranger, whom the grave refuses, in the morgue.
        [Tool("test/burial_stage", Description = "Disposable fixture (#2196): a morgue zone holding a colonist and a stranger corpse, a free grave and a hauler. Test builds only.")]
        public async Task<object> BurialStage(IRimBridgeContext ctx, CancellationToken cancellationToken, string priority = "Normal")
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First();
                var open = GenRadial.RadialCellsAround(pawn.Position, 20, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map) && c.GetEdifice(map) == null && c.GetZone(map) == null
                    && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item) && c.GetFirstBuilding(map) == null
                    && c.GetTerrain(map).passability == Traversability.Standable).Distinct().ToList();
                var origin = open.FirstOrDefault(c => Enumerable.Range(0, 3).All(dx => Enumerable.Range(0, 3).All(dz => open.Contains(c + new IntVec3(dx, 0, dz)))));
                if (origin == default) throw new System.InvalidOperationException("No open 3x3 near the first colonist for the morgue.");
                var zoneCells = Enumerable.Range(0, 3).SelectMany(dx => Enumerable.Range(0, 3).Select(dz => origin + new IntVec3(dx, 0, dz))).ToList();
                var graveCell = open.Where(c => !zoneCells.Contains(c) && zoneCells.All(z => z.DistanceToSquared(c) > 9)
                    && c.GetThingList(map).All(t => t.def.category != ThingCategory.Item)).OrderBy(c => c.DistanceToSquared(origin)).FirstOrDefault();
                if (graveCell == default) throw new System.InvalidOperationException("No free cell for the grave.");
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                foreach (var c in zoneCells) zone.AddCell(c);
                var settings = zone.GetStoreSettings();
                settings.filter.SetDisallowAll();
                settings.filter.SetAllow(ThingCategoryDefOf.CorpsesHumanlike, true);
                foreach (var special in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Where(d => d.configurable))
                    settings.filter.SetAllow(special, true);
                settings.Priority = (StoragePriority)System.Enum.Parse(typeof(StoragePriority), priority);
                var grave = (Building_Grave)ThingMaker.MakeThing(ThingDefOf.Grave);
                grave.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(grave, graveCell, map);
                Corpse MakeCorpse(Faction faction, IntVec3 cell)
                {
                    var dead = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, faction);
                    GenSpawn.Spawn(dead, cell, map);
                    dead.Kill(null);
                    var corpse = dead.Corpse;
                    if (corpse.Spawned) corpse.DeSpawn(DestroyMode.Vanish);
                    GenPlace.TryPlaceThing(corpse, cell, map, ThingPlaceMode.Direct, out var placed);
                    if (placed != corpse || !corpse.Spawned || corpse.Position != cell)
                        throw new System.InvalidOperationException($"Fixture corpse displaced from {cell}.");
                    corpse.SetForbidden(false, false);
                    return corpse;
                }
                var colonist = MakeCorpse(Faction.OfPlayer, zoneCells[0]);
                var stranger = MakeCorpse(null, zoneCells[1]);
                foreach (var colonistPawn in map.mapPawns.FreeColonistsSpawned)
                    colonistPawn.workSettings?.SetPriority(WorkTypeDefOf.Hauling, 1);
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return (object)new { success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    colonistCorpse = colonist.GetUniqueLoadID(), strangerCorpse = stranger.GetUniqueLoadID(), grave = grave.GetUniqueLoadID(),
                    zone = zone.label };
            }, cancellationToken);

        [Tool("test/burial_read", Description = "Disposable fixture (#2196): where the staged corpses lie: in the grave, in the morgue zone or elsewhere. Test builds only.")]
        public async Task<object> BurialRead(IRimBridgeContext ctx, CancellationToken cancellationToken, string ids = "")
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var things = ids.Split(',').Where(id => id.Length > 0).Select(id =>
                {
                    var corpse = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().FirstOrDefault(c => c.GetUniqueLoadID() == id);
                    var grave = corpse?.ParentHolder as Building_Grave;
                    if (corpse == null)
                        grave = map.listerBuildings.allBuildingsColonist.OfType<Building_Grave>().FirstOrDefault(g => g.HasAnyContents && g.Corpse?.GetUniqueLoadID() == id);
                    var held = corpse ?? grave?.Corpse;
                    return new { id, found = held != null, inGrave = grave != null, spawned = corpse?.Spawned ?? false,
                        zone = corpse != null && corpse.Spawned && corpse.Position.GetZone(map) is Zone_Stockpile,
                        x = corpse != null && corpse.Spawned ? corpse.Position.x : -1, z = corpse != null && corpse.Spawned ? corpse.Position.z : -1 };
                }).ToList();
                return (object)new { success = true, tick = Find.TickManager.TicksGame, things };
            }, cancellationToken);

        // Stranger sarcophagus loop acceptance (#2338): one fresh Sarcophagus
        // accepting any humanlike corpse, a fresh stranger corpse (a humanlike
        // pawn of no faction) a few cells away, and every colonist hauling.
        [Tool("test/tomb_stage", Description = "Disposable fixture (#2338): a fresh sarcophagus and a fresh stranger corpse near the first colonist. Test builds only.")]
        public async Task<object> TombStage(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First();
                var open = GenRadial.RadialCellsAround(pawn.Position, 20, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map) && c.GetEdifice(map) == null && c.GetZone(map) == null
                    && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item) && c.GetFirstBuilding(map) == null
                    && c.GetTerrain(map).passability == Traversability.Standable).Distinct().ToList();
                var sarcophagusCell = open.FirstOrDefault(c => open.Contains(c + IntVec3.North));
                if (sarcophagusCell == default) throw new System.InvalidOperationException("No open 1x2 near the first colonist for the sarcophagus.");
                var sarcophagusCells = new[] { sarcophagusCell, sarcophagusCell + IntVec3.North };
                var corpseCell = open.Where(c => !sarcophagusCells.Contains(c) && sarcophagusCells.All(s => s.DistanceToSquared(c) > 16))
                    .OrderBy(c => c.DistanceToSquared(sarcophagusCell)).FirstOrDefault();
                if (corpseCell == default) throw new System.InvalidOperationException("No free cell for the stranger corpse.");
                var def = DefDatabase<ThingDef>.GetNamed("Sarcophagus");
                var sarcophagus = (Building_Grave)ThingMaker.MakeThing(def, ThingDefOf.Steel);
                sarcophagus.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(sarcophagus, sarcophagusCell, map, Rot4.North);
                var filter = sarcophagus.GetStoreSettings().filter;
                filter.SetAllow(ThingCategoryDefOf.CorpsesHumanlike, true);
                foreach (var special in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Where(d => d.configurable))
                    filter.SetAllow(special, true);
                var corpse = TombCorpse(map, corpseCell);
                foreach (var colonist in map.mapPawns.FreeColonistsSpawned)
                    colonist.workSettings?.SetPriority(WorkTypeDefOf.Hauling, 1);
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return (object)new { success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    sarcophagus = sarcophagus.GetUniqueLoadID(), corpse = corpse.GetUniqueLoadID(),
                    sarcophagusX = sarcophagusCell.x, sarcophagusZ = sarcophagusCell.z,
                    colonists = map.mapPawns.FreeColonistsSpawned.Count };
            }, cancellationToken);

        // A fresh stranger corpse exactly on cell, unforbidden.
        private static Corpse TombCorpse(Map map, IntVec3 cell)
        {
            var dead = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, null);
            GenSpawn.Spawn(dead, cell, map);
            dead.Kill(null);
            var corpse = dead.Corpse;
            if (corpse.Spawned) corpse.DeSpawn(DestroyMode.Vanish);
            GenPlace.TryPlaceThing(corpse, cell, map, ThingPlaceMode.Direct, out var placed);
            if (placed != corpse || !corpse.Spawned || corpse.Position != cell)
                throw new System.InvalidOperationException($"Fixture corpse displaced from {cell}.");
            corpse.SetForbidden(false, false);
            return corpse;
        }

        // The second burial: the buried corpse is ejected and forbidden so
        // nothing hauls it back, and a second fresh stranger corpse lies out
        // for the same, now used, sarcophagus.
        [Tool("test/tomb_second", Description = "Disposable fixture (#2338): eject the sarcophagus contents, forbid them and stage a second stranger corpse. Test builds only.")]
        public async Task<object> TombSecond(IRimBridgeContext ctx, CancellationToken cancellationToken, string sarcophagus)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var grave = map.listerBuildings.allBuildingsColonist.OfType<Building_Grave>().FirstOrDefault(g => g.GetUniqueLoadID() == sarcophagus)
                    ?? throw new System.InvalidOperationException($"No sarcophagus {sarcophagus}.");
                var first = grave.Corpse ?? throw new System.InvalidOperationException("The sarcophagus holds no corpse to eject.");
                grave.EjectContents();
                first.SetForbidden(true, false);
                var cell = GenRadial.RadialCellsAround(grave.Position, 12, true).Where(c => c.InBounds(map) && c.Standable(map)
                    && c.GetEdifice(map) == null && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)
                    && c.DistanceToSquared(grave.Position) > 16).OrderBy(c => c.DistanceToSquared(grave.Position)).First();
                var second = TombCorpse(map, cell);
                return (object)new { success = true, tick = Find.TickManager.TicksGame, first = first.GetUniqueLoadID(), second = second.GetUniqueLoadID() };
            }, cancellationToken);

        // Where the sarcophagus, each colonist's KnowBuriedInSarcophagus
        // memories and each listed corpse stand.
        [Tool("test/tomb_read", Description = "Disposable fixture (#2338): the sarcophagus, every colonist's KnowBuriedInSarcophagus memories and the listed corpses. Test builds only.")]
        public async Task<object> TombRead(IRimBridgeContext ctx, CancellationToken cancellationToken, string sarcophagus, string ids = "")
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var memory = DefDatabase<ThoughtDef>.GetNamed("KnowBuriedInSarcophagus");
                var grave = map.listerBuildings.allBuildingsColonist.OfType<Building_Grave>().FirstOrDefault(g => g.GetUniqueLoadID() == sarcophagus);
                var colonists = map.mapPawns.FreeColonistsSpawned.Select(p => new { id = p.GetUniqueLoadID(), mood = p.needs?.mood != null,
                    memories = p.needs?.mood?.thoughts?.memories?.Memories.Count(m => m.def == memory) ?? -1 }).ToList();
                var corpses = ids.Split(',').Where(id => id.Length > 0).Select(id =>
                {
                    var spawned = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().FirstOrDefault(c => c.GetUniqueLoadID() == id);
                    var held = grave?.Corpse != null && grave.Corpse.GetUniqueLoadID() == id ? grave.Corpse : null;
                    var corpse = spawned ?? held;
                    return new { id, found = corpse != null, spawned = spawned != null, inSarcophagus = held != null,
                        everBuried = corpse?.everBuriedInSarcophagus ?? false,
                        rot = corpse?.TryGetComp<CompRottable>()?.Stage.ToString() ?? "",
                        x = spawned?.Position.x ?? -1, z = spawned?.Position.z ?? -1 };
                }).ToList();
                return (object)new { success = true, tick = Find.TickManager.TicksGame, present = grave != null && !grave.Destroyed,
                    holds = grave?.HasAnyContents ?? false, colonists, corpses };
            }, cancellationToken);

        // Corpse disposal acceptance (#1817), staged on the tribal baseline
        // colony: a rotten animal corpse, a rotten and a fresh stranger
        // corpse, one worn apparel and stone blocks for the walls, each on its
        // own free open cell near the first colonist. Strangers are humanlike
        // corpses of no faction.
        [Tool("test/disposal_stage", Description = "Disposable fixture (#1817): stage rotten and fresh corpses, a worn apparel and stone blocks near the first colonist. Test builds only.")]
        public async Task<object> DisposalStage(IRimBridgeContext ctx, CancellationToken cancellationToken, int blocks = 225)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First();
                var candidates = GenRadial.RadialCellsAround(pawn.Position, 16, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.Standable(map) && !c.Roofed(map) && c.GetEdifice(map) == null && c.GetZone(map) == null
                    && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item) && c.GetFirstBuilding(map) == null
                    && c.GetTerrain(map).passability == Traversability.Standable).Distinct().ToList();
                if (candidates.Count < 16) throw new System.InvalidOperationException("Not enough empty open cells near the first colonist.");
                var next = 0;
                Thing Place(Thing thing)
                {
                    while (next < candidates.Count)
                    {
                        var cell = candidates[next++];
                        if (cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)) continue;
                        if (!GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Direct, out var placed)) continue;
                        if (placed != thing || !thing.Spawned || thing.Position != cell)
                            throw new System.InvalidOperationException($"Fixture {thing.def.defName} was absorbed or displaced at {cell}.");
                        thing.SetForbidden(false, false);
                        return thing;
                    }
                    throw new System.InvalidOperationException($"Could not place a fixture {thing.def.defName} on any open cell.");
                }
                Corpse MakeCorpse(PawnKindDef kind, float rotProgress)
                {
                    var dead = PawnGenerator.GeneratePawn(kind, null);
                    GenSpawn.Spawn(dead, pawn.Position, map);
                    dead.Kill(null);
                    var corpse = dead.Corpse;
                    if (corpse.Spawned) corpse.DeSpawn(DestroyMode.Vanish);
                    Place(corpse);
                    corpse.GetComp<CompRottable>().RotProgress = rotProgress;
                    return corpse;
                }
                var squirrel = DefDatabase<PawnKindDef>.AllDefsListForReading.First(k => k.race.defName == "Squirrel");
                var animal = MakeCorpse(squirrel, 200000f);
                var rottenStranger = MakeCorpse(PawnKindDefOf.Villager, 200000f);
                var freshStranger = MakeCorpse(PawnKindDefOf.Villager, 0f);
                var pants = DefDatabase<ThingDef>.GetNamed("Apparel_Pants");
                var worn = Place(ThingMaker.MakeThing(pants, GenStuff.DefaultStuffFor(pants)));
                worn.HitPoints = worn.MaxHitPoints / 5;
                var stone = DefDatabase<ThingDef>.GetNamed("BlocksGranite");
                for (var left = blocks; left > 0; left -= stone.stackLimit)
                {
                    var stack = ThingMaker.MakeThing(stone);
                    stack.stackCount = System.Math.Min(left, stone.stackLimit);
                    Place(stack);
                }
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return (object)new { success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    animal = animal.GetUniqueLoadID(), animalDef = animal.def.defName, rottenStranger = rottenStranger.GetUniqueLoadID(),
                    freshStranger = freshStranger.GetUniqueLoadID(), worn = worn.GetUniqueLoadID(), wornDef = worn.def.defName };
            }, cancellationToken);

        // Worn apparel (a fifth of its hit points) on each cell ("x,z;x,z"):
        // what the incinerator zone takes, enough to fill a batch.
        [Tool("test/disposal_seed", Description = "Disposable fixture (#1817): spawn one worn apparel on each listed free cell and molotovs loose near the first colonist. Test builds only.")]
        public async Task<object> DisposalSeed(IRimBridgeContext ctx, CancellationToken cancellationToken, string cells, int molotovs = 0)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var parsed = (cells ?? "").Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split(','))
                    .Select(p => new IntVec3(int.Parse(p[0]), 0, int.Parse(p[1]))).ToList();
                var pants = DefDatabase<ThingDef>.GetNamed("Apparel_Pants");
                var ids = new System.Collections.Generic.List<string>();
                foreach (var cell in parsed)
                {
                    if (!cell.InBounds(map) || cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Item))
                        throw new System.InvalidOperationException($"Cell {cell} is out of bounds or already holds an item.");
                    var thing = ThingMaker.MakeThing(pants, GenStuff.DefaultStuffFor(pants));
                    thing.HitPoints = thing.MaxHitPoints / 5;
                    GenSpawn.Spawn(thing, cell, map);
                    thing.SetForbidden(false, false);
                    ids.Add(thing.GetUniqueLoadID());
                }
                // Loose molotovs near the first colonist: the burner's weapon.
                var near = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).First().Position;
                for (var i = 0; i < molotovs; i++)
                {
                    var molotov = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Weapon_GrenadeMolotov"));
                    if (!GenPlace.TryPlaceThing(molotov, near, map, ThingPlaceMode.Near)) throw new System.InvalidOperationException("Could not place a molotov.");
                    molotov.SetForbidden(false, false);
                    ids.Add(molotov.GetUniqueLoadID());
                }
                return (object)new { success = true, ids };
            }, cancellationToken);

        // The incinerator as the game sees it (#1817): each listed thing, the
        // interior cells with the room cell.GetRoom resolves them to, the
        // walled ring's edifices with their flammability, the ash and the
        // fires. door is the ring's door cell; the cell beyond it is outside.
        [Tool("test/disposal_read", Description = "Disposable fixture (#1817): read the staged things and the incinerator room, ring, ash and fires. Test builds only.")]
        public async Task<object> DisposalRead(IRimBridgeContext ctx, CancellationToken cancellationToken, string ids, int minX, int minZ, int maxX, int maxZ, int doorX, int doorZ)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var rect = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                var things = (ids ?? "").Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries).Select(id =>
                {
                    var thing = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == id);
                    var carried = map.mapPawns.AllPawns.Any(p => p.carryTracker?.CarriedThing?.GetUniqueLoadID() == id);
                    return (object)new { id, spawned = thing != null && thing.Spawned, carried,
                        x = thing?.Position.x ?? -1, z = thing?.Position.z ?? -1,
                        rot = thing?.TryGetComp<CompRottable>()?.Stage.ToString() ?? "",
                        inside = thing != null && thing.Spawned && rect.Contains(thing.Position) };
                }).ToList();
                var interior = rect.Cells.Select(c =>
                {
                    var room = c.GetRoom(map);
                    return new { x = c.x, z = c.z, room = room?.ID ?? -1, roofed = c.Roofed(map),
                        items = c.GetThingList(map).Where(t => t.def.category == ThingCategory.Item).Select(t => t.def.defName).ToList() };
                }).ToList();
                var first = rect.CenterCell.GetRoom(map);
                var door = new IntVec3(doorX, 0, doorZ);
                var step = new IntVec3(System.Math.Sign(doorX - rect.CenterCell.x), 0, System.Math.Sign(doorZ - rect.CenterCell.z));
                var beyond = door + step;
                var ring = rect.ExpandedBy(1).EdgeCells.Select(c =>
                {
                    var edifice = c.GetEdifice(map);
                    return new { x = c.x, z = c.z, def = edifice?.def.defName ?? "", stuff = edifice?.Stuff?.defName ?? "",
                        door = edifice is Building_Door, flammability = edifice == null ? 0f : edifice.GetStatValue(StatDefOf.Flammability) };
                }).ToList();
                var ash = rect.Cells.SelectMany(c => c.GetThingList(map)).Count(t => t.def.defName == "Filth_Ash");
                var fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
                return (object)new { success = true, tick = Find.TickManager.TicksGame, things, interior, ring, ash, fires,
                    room = new { id = first?.ID ?? -1, cellCount = first?.CellCount ?? 0, outdoorTemperature = first?.UsesOutdoorTemperature ?? false, properRoom = first?.ProperRoom ?? false },
                    beyondDoorRoom = beyond.InBounds(map) ? (beyond.GetRoom(map)?.ID ?? -1) : -1, doorRoom = door.GetRoom(map)?.ID ?? -1 };
            }, cancellationToken);

        // The permanent ColonyFacts equality probe (#1296): the snapshot read
        // with every read optimization off and then on, in one game-thread
        // call so no tick passes between them, must be byte-identical (row
        // order included). Add each new optimization's switch here.
        [Tool("test/colony_facts_equality", Description = "Compare ColonyFacts bytes with read optimizations off and on.")]
        public async Task<object> ColonyFactsEquality(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync(() => CompareColonyFacts(Find.CurrentMap), cancellationToken);

        // On the main thread: the equality probe's reply for map.
        private static object CompareColonyFacts(Map map)
        {
            {
                if (!ProtoBoundary.TryReadContext(map, out var context, out var unavailable))
                    throw new System.InvalidOperationException(unavailable.Detail);
                RimGovernor.Protocol.Observations.ColonyFactsSnapshot Capture(bool optimized)
                {
                    var previous = NativeUpkeepFacts.SharedPass;
                    NativeUpkeepFacts.SharedPass = optimized;
                    try
                    {
                        var request = new RimGovernor.Protocol.Observations.ColonyFactsRequest {
                            Scope = new RimGovernor.Protocol.Observations.ReadScope { ExpectedIdentity = context.Identity.Clone() }, Planning = true };
                        return NativeColonyObservationTools.Read(map, request, context);
                    }
                    finally { NativeUpkeepFacts.SharedPass = previous; }
                }
                var off = Capture(false);
                var on = Capture(true);
                var offBytes = Google.Protobuf.MessageExtensions.ToByteArray(off);
                var onBytes = Google.Protobuf.MessageExtensions.ToByteArray(on);
                var upkeep = on.Upkeep?.Observed;
                // ResourceSources (#1295): each resource's reply with the
                // cheap census and support shortcut off and on. Plant yields
                // round at random per read, so non-mine yields are blanked.
                var resourcesEqual = true; long resourceRows = 0; double offMs = 0, onMs = 0;
                byte[] Sources(string resource, bool cheap, ref double ms)
                {
                    NativeResourceSourcesTool.Cheap = cheap; ExcavationSafety.Shortcut = cheap;
                    try
                    {
                        var began = System.Diagnostics.Stopwatch.GetTimestamp();
                        var reply = NativeResourceSourcesTool.Read(map, new RimGovernor.Protocol.Observations.ResourceSourcesRequest { Resource = resource }, context);
                        ms += (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        foreach (var row in reply.Observed?.Sources ?? new Google.Protobuf.Collections.RepeatedField<RimGovernor.Protocol.Observations.ResourceSource>())
                            if (row.Method != "mine") row.Yield = 0;
                        return Google.Protobuf.MessageExtensions.ToByteArray(reply);
                    }
                    finally { NativeResourceSourcesTool.Cheap = true; ExcavationSafety.Shortcut = true; }
                }
                foreach (var resource in new[] { "Steel", "WoodLog", "ComponentIndustrial", "Silver", "Gold", "Plasteel", "Uranium", "Jade", "ChunkGranite", "ChunkSandstone", "ChunkLimestone", "ChunkSlate", "ChunkMarble" })
                    for (var i = 0; i < 3; i++)
                    {
                        var a = Sources(resource, false, ref offMs);
                        var b = Sources(resource, true, ref onMs);
                        if (!a.SequenceEqual(b)) resourcesEqual = false;
                        if (i == 0) resourceRows += RimGovernor.Protocol.Observations.ResourceSourcesReply.Parser.ParseFrom(b).Observed?.Sources.Count ?? 0;
                    }
                return (object)new { equal = offBytes.SequenceEqual(onBytes) && resourcesEqual, resourcesEqual, resourceRows,
                    resourcesOffMs = System.Math.Round(offMs, 1), resourcesOnMs = System.Math.Round(onMs, 1), offBytes = offBytes.Length, onBytes = onBytes.Length,
                    upkeepItems = upkeep?.Items.Count ?? 0, upkeepStructures = upkeep?.Structures.Count ?? 0,
                    upkeepFilth = upkeep?.Filth.Count ?? 0, upkeepBeds = upkeep?.Beds.Count ?? 0,
                    wasteRows = on.Waste?.Observed?.Items.Count ?? 0 };
            }
        }

        // The snapshot capture profiling loop (#1320): SnapshotFrames.Capture
        // count times back to back on the game thread, paused, with the
        // stream's current subscription. Each capture's total and every
        // ObservationWork section (families and their Detail spans) come back
        // raw; `acceptance profile-capture` computes the percentiles.
        // equality also runs the ColonyFacts equality probe afterwards.
        [Tool("test/profile_capture", Description = "Time SnapshotFrames.Capture count times, paused, per family and detail span.")]
        public async Task<object> ProfileCapture(IRimBridgeContext ctx, CancellationToken cancellationToken, int count = 20, bool equality = false)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                if (count < 1 || count > 1000) throw new System.ArgumentOutOfRangeException(nameof(count), "count must be 1..1000");
                var map = Find.CurrentMap;
                var tickManager = Find.TickManager;
                var speed = tickManager.CurTimeSpeed;
                tickManager.CurTimeSpeed = TimeSpeed.Paused;
                try
                {
                    var shape = SnapshotStream.Subscription;
                    var captures = new System.Collections.Generic.List<object>(count);
                    long bytes = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var began = System.Diagnostics.Stopwatch.GetTimestamp();
                        var hop = ObservationWork.BeginCapture();
                        RimGovernor.Protocol.Observations.BundleSnapshot frame;
                        try { frame = SnapshotFrames.Capture(map, shape); }
                        finally { ObservationWork.End(); }
                        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - began;
                        if (frame == null) throw new System.InvalidOperationException("SnapshotFrames.Capture read no context.");
                        if (i == 0) bytes = frame.CalculateSize();
                        captures.Add(new { totalMs = Ms(elapsed), sections = hop.Sections.Select(s =>
                            (object)new { name = s.Name, ms = Ms(s.Ticks), rows = s.Rows, detail = s.Detail }).ToList() });
                    }
                    return (object)new { success = true, count, tick = tickManager.TicksGame, frameBytes = bytes,
                        pawns = map.mapPawns.AllPawnsSpawnedCount, things = map.listerThings.AllThings.Count,
                        captures, equality = equality ? CompareColonyFacts(map) : null };
                }
                finally { tickManager.CurTimeSpeed = speed; }
            }, cancellationToken);

        // The cell grid cache's equality probe (#1575): the cached whole-map
        // read must encode to the same bytes as a read from scratch after the
        // cache has been warmed and then each kind of map change is made and
        // undone (wall, item, door blueprint, terrain, roof).
        [Tool("test/grid_cache_equality", Description = "Compare the cached cell grid read with a fresh one across map changes.")]
        public async Task<object> GridCacheEquality(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                var steps = new System.Collections.Generic.List<object>();
                bool Same(string step)
                {
                    var cached = Google.Protobuf.MessageExtensions.ToByteArray(CellGridEncoder.Encode(CellGridEncoder.Read(map), null)!);
                    var fresh = Google.Protobuf.MessageExtensions.ToByteArray(CellGridEncoder.Encode(CellGridEncoder.ReadUncached(map), null)!);
                    var equal = cached.SequenceEqual(fresh);
                    steps.Add(new { step, equal, cachedBytes = cached.Length, freshBytes = fresh.Length });
                    return equal;
                }
                var free = GenRadial.RadialCellsAround(map.Center, 40f, true)
                    .Where(c => c.InBounds(map) && c.Standable(map) && !c.Roofed(map) && !c.Fogged(map) && c.GetThingList(map).Count == 0
                        && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)).Take(8).ToList();
                if (free.Count < 8) throw new System.InvalidOperationException("Too few open cells near the map centre.");
                Same("warm");
                Same("rewarm");
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
                wall.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(wall, free[0], map);
                Same("wall spawned");
                wall.Destroy();
                Same("wall removed");
                var steel = ThingMaker.MakeThing(ThingDefOf.Steel); steel.stackCount = 10;
                GenSpawn.Spawn(steel, free[1], map);
                Same("item spawned");
                steel.Destroy();
                Same("item removed");
                var blueprint = GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Door, free[2], map, Rot4.North, Faction.OfPlayer, ThingDefOf.Steel);
                Same("door blueprint placed");
                blueprint.Destroy();
                Same("door blueprint removed");
                var terrain = map.terrainGrid.TerrainAt(free[3]);
                map.terrainGrid.SetTerrain(free[3], TerrainDefOf.Concrete);
                Same("terrain changed");
                map.terrainGrid.SetTerrain(free[3], terrain);
                Same("terrain restored");
                map.roofGrid.SetRoof(free[4], RoofDefOf.RoofConstructed);
                Same("roof set");
                map.roofGrid.SetRoof(free[4], null);
                Same("roof cleared");
                // A closed ring of walls round one cell makes a room and then unmakes it.
                var ring = new System.Collections.Generic.List<Thing>();
                foreach (var cell in GenAdj.CellsAdjacent8Way(new TargetInfo(free[5], map)).Where(c => c.InBounds(map) && c.Standable(map) && c.GetThingList(map).Count == 0))
                {
                    var piece = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
                    piece.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(piece, cell, map);
                    ring.Add(piece);
                }
                Same("wall ring built");
                foreach (var piece in ring) piece.Destroy();
                Same("wall ring removed");
                return (object)new { success = true, equal = steps.All(s => (bool)s.GetType().GetProperty("equal")!.GetValue(s)!), steps };
            }, cancellationToken);

        // The gear census memo's equality probe (#1575): the colony's gear
        // snapshot read with the per-read memo on and then off, in one
        // game-thread call, must be byte-identical.
        [Tool("test/gear_memo_equality", Description = "Compare the gear census bytes with the read memo on and off.")]
        public async Task<object> GearMemoEquality(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.TryReadContext(map, out var context, out var unavailable))
                    throw new System.InvalidOperationException(unavailable.Detail);
                byte[] Read(bool off)
                {
                    CensusMemo.Off = off;
                    try { return Google.Protobuf.MessageExtensions.ToByteArray(NativeGearFacts.Read(map, context)); }
                    finally { CensusMemo.Off = false; }
                }
                var on = Read(false);
                var fresh = Read(true);
                return (object)new { success = true, equal = on.SequenceEqual(fresh), onBytes = on.Length, freshBytes = fresh.Length };
            }, cancellationToken);

        private static double Ms(long stopwatchTicks) => stopwatchTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }
}
